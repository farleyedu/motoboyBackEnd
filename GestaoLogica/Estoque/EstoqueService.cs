using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using APIBack.Acontecimentos;
using APIBack.Security;
using Dapper;
using Npgsql;

namespace APIBack.GestaoLogica.Estoque;

public sealed record PerdaRequest(string? ChaveIdempotencia, Guid InsumoId, decimal Quantidade, string? Motivo, bool ConfirmacaoHumana);

public sealed record InventarioRequest(string? ChaveIdempotencia, IReadOnlyList<Contagem>? Contagens, string? Motivo, bool ConfirmacaoHumana);

public sealed record BaixaVendasRequest(IReadOnlyList<PedidoBaixa>? Pedidos, bool ConfirmacaoHumana);

public sealed record InventarioRegistrado(AcontecimentoDto Acontecimento, IReadOnlyList<DivergenciaInventario> Divergencias);

public sealed record BaixaVendasRegistrada(AcontecimentoDto Acontecimento, BaixaVendasPrevia Previa);

/// <summary>
/// Estoque pelo livro: o saldo é a soma dos movimentos. Perda não tem lançamento (o custo vai para a Gestão e Inteligência);
/// inventário grava só divergências; a baixa por venda é calculada pela ficha técnica e só move o estoque quando uma pessoa
/// confirma (regra 4). Cada escrita passa por <see cref="GestaoPapeis.ValidarPublicacao"/> (operador: só estoque e só hoje).
/// </summary>
public interface IEstoqueService
{
    Task<IReadOnlyList<SaldoInsumo>> Saldos(Guid loja, CancellationToken ct);
    Task<AcontecimentoDto> RegistrarPerda(Guid loja, int usuario, GestaoPapeis.Acesso acesso, PerdaRequest req, CancellationToken ct);
    Task<IReadOnlyList<DivergenciaInventario>> PreviaInventario(Guid loja, IReadOnlyList<Contagem> contagens, CancellationToken ct);
    Task<InventarioRegistrado> RegistrarInventario(Guid loja, int usuario, GestaoPapeis.Acesso acesso, InventarioRequest req, CancellationToken ct);
    Task<BaixaVendasPrevia> PreviaBaixa(Guid loja, IReadOnlyList<PedidoBaixa> pedidos, CancellationToken ct);
    Task<BaixaVendasRegistrada> RegistrarBaixa(Guid loja, int usuario, GestaoPapeis.Acesso acesso, BaixaVendasRequest req, CancellationToken ct);
}

public sealed class EstoqueService(NpgsqlDataSource dataSource, IAcontecimentosService livro, IFichaTecnicaLeitor fichas,
    TimeProvider? relogio = null) : IEstoqueService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly TimeProvider _relogio = relogio ?? TimeProvider.System;

    public const string TipoBaixa = "baixa_vendas";

    public async Task<IReadOnlyList<SaldoInsumo>> Saldos(Guid loja, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        return (await SaldosPorInsumo(c, loja)).Values.OrderBy(s => s.Nome).ToList();
    }

    public async Task<AcontecimentoDto> RegistrarPerda(Guid loja, int usuario, GestaoPapeis.Acesso acesso, PerdaRequest req, CancellationToken ct)
    {
        if (req.Quantidade <= 0) throw Invalido("Informe quanto se perdeu (maior que zero, na unidade do insumo).");
        if (string.IsNullOrWhiteSpace(req.Motivo)) throw Invalido("Diga o motivo da perda.");
        SaldoInsumo insumo;
        await using (var c = await dataSource.OpenConnectionAsync(ct))
            insumo = (await SaldosPorInsumo(c, loja)).GetValueOrDefault(req.InsumoId) ?? throw Invalido("Insumo não encontrado nesta loja.");
        var pedido = new PublicarAcontecimentoRequest
        {
            ChaveIdempotencia = Chave(req.ChaveIdempotencia), Tipo = TipoAcontecimento.Perda, Modulo = ModuloGestao.Estoque,
            DataFato = _relogio.GetUtcNow(), Descricao = Cortar($"Perda: {insumo.Nome}", 300),
            Movimentos = new()
            {
                new MovimentoRequest { ItemId = insumo.InsumoId, ItemNome = insumo.Nome, Unidade = insumo.Unidade, Tipo = TipoMovimento.Perda,
                    Quantidade = -Math.Round(req.Quantidade, 4), Motivo = Cortar(req.Motivo.Trim(), 300) },
            },
            ConfirmacaoHumana = req.ConfirmacaoHumana,
        };
        await Validar(loja, acesso, pedido, ct);
        return (await livro.Publicar(loja, usuario, pedido, ct)).Acontecimento;
    }

    public async Task<IReadOnlyList<DivergenciaInventario>> PreviaInventario(Guid loja, IReadOnlyList<Contagem> contagens, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        return EstoqueRegras.Divergencias(contagens, await SaldosPorInsumo(c, loja));
    }

    public async Task<InventarioRegistrado> RegistrarInventario(Guid loja, int usuario, GestaoPapeis.Acesso acesso, InventarioRequest req,
        CancellationToken ct)
    {
        var divergencias = await PreviaInventario(loja, req.Contagens ?? Array.Empty<Contagem>(), ct);
        var motivo = string.IsNullOrWhiteSpace(req.Motivo) ? "Contagem de inventário" : req.Motivo.Trim();
        var pedido = new PublicarAcontecimentoRequest
        {
            ChaveIdempotencia = Chave(req.ChaveIdempotencia), Tipo = TipoAcontecimento.Inventario, Modulo = ModuloGestao.Estoque,
            DataFato = _relogio.GetUtcNow(), Descricao = $"Inventário de {divergencias.Count} {(divergencias.Count == 1 ? "item" : "itens")}",
            // A contagem inteira fica no acontecimento; só a diferença vira movimento (e nunca vira perda sozinha).
            Dados = JsonSerializer.SerializeToElement(new { contagens = divergencias }, Json),
            Movimentos = divergencias.Where(d => d.Diferenca != 0).Select(d => new MovimentoRequest
            {
                ItemId = d.InsumoId, ItemNome = d.Nome, Unidade = d.Unidade, Tipo = TipoMovimento.Divergencia, Quantidade = d.Diferenca,
                Motivo = Cortar($"{motivo}: contado {d.Contado:0.####}, esperado {d.Esperado:0.####} {d.Unidade}", 300),
            }).ToList(),
            ConfirmacaoHumana = req.ConfirmacaoHumana,
        };
        await Validar(loja, acesso, pedido, ct);
        return new InventarioRegistrado((await livro.Publicar(loja, usuario, pedido, ct)).Acontecimento, divergencias);
    }

    public async Task<BaixaVendasPrevia> PreviaBaixa(Guid loja, IReadOnlyList<PedidoBaixa> pedidos, CancellationToken ct)
    {
        EstoqueRegras.ValidarPedidos(pedidos);
        await using var c = await dataSource.OpenConnectionAsync(ct);
        var ids = pedidos.Select(p => p.PedidoId).ToArray();
        var daLoja = (await c.QueryAsync<int>("SELECT id FROM pedido WHERE id_estabelecimento = @loja AND id = ANY(@ids)", new { loja, ids })).ToHashSet();
        foreach (var fora in ids.Where(i => !daLoja.Contains(i)).Take(1))
            throw new GestaoException("NAO_ENCONTRADO", $"Pedido {fora} não encontrado nesta loja.", 404);
        var jaBaixados = (await c.QueryAsync<int>(
            "SELECT pedido_id FROM gestao_baixas_pedido WHERE estabelecimento_id = @loja AND pedido_id = ANY(@ids)", new { loja, ids })).ToHashSet();
        var itens = (await c.QueryAsync<ItemVendido>(
            "SELECT pedido_id AS PedidoId, produto_id AS ProdutoId, nome, quantidade FROM pedido_item WHERE pedido_id = ANY(@ids) ORDER BY pedido_id, ordem",
            new { ids })).ToList();
        var produtos = itens.Where(i => i.ProdutoId != null).Select(i => i.ProdutoId!.Value).Distinct().ToList();
        var lidas = await fichas.Ler(loja, produtos, ct);
        var (consumo, baixaveis, semFicha) = EstoqueRegras.Consumo(pedidos.Where(p => !jaBaixados.Contains(p.PedidoId)).ToList(), itens, lidas);
        return new BaixaVendasPrevia(consumo, baixaveis, semFicha, jaBaixados.Order().ToList());
    }

    public async Task<BaixaVendasRegistrada> RegistrarBaixa(Guid loja, int usuario, GestaoPapeis.Acesso acesso, BaixaVendasRequest req,
        CancellationToken ct)
    {
        var pedidos = req.Pedidos ?? Array.Empty<PedidoBaixa>();
        var previa = await PreviaBaixa(loja, pedidos, ct);
        if (previa.JaBaixados.Count > 0)
            throw new GestaoException("DUPLICADO", $"Pedido já baixado: {string.Join(", ", previa.JaBaixados)}.", 409);
        if (previa.Pedidos.Count == 0 || previa.Consumo.Count == 0)
            throw Invalido("Nenhum pedido pode ser baixado: falta a ficha técnica dos produtos (Gestão e Inteligência).");
        // A mesma seleção de pedidos gera a mesma chave: clicar duas vezes não baixa duas vezes.
        var assinatura = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(",", previa.Pedidos.Order()))))[..32].ToLowerInvariant();
        var modalidades = pedidos.ToDictionary(p => p.PedidoId, p => p.Modalidade);
        var pedido = new PublicarAcontecimentoRequest
        {
            ChaveIdempotencia = $"estoque:baixa_vendas:{assinatura}", Tipo = TipoBaixa, Modulo = ModuloGestao.Estoque,
            DataFato = _relogio.GetUtcNow(), Descricao = $"Baixa por venda de {previa.Pedidos.Count} {(previa.Pedidos.Count == 1 ? "pedido" : "pedidos")}",
            Origem = new OrigemRef { Tipo = TipoBaixa, Id = assinatura },
            Dados = JsonSerializer.SerializeToElement(new { pedidos = previa.Pedidos.Select(p => new { pedidoId = p, modalidade = modalidades[p] }) }, Json),
            Movimentos = previa.Consumo.Select(i => new MovimentoRequest
            {
                ItemId = i.InsumoId, ItemNome = i.Nome, Unidade = i.Unidade, Tipo = TipoMovimento.Saida, Quantidade = -i.Quantidade,
            }).ToList(),
            ConfirmacaoHumana = req.ConfirmacaoHumana,
        };
        await Validar(loja, acesso, pedido, ct);

        await using var c = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        var r = await livro.Publicar(c, tx, loja, usuario, pedido, ct);
        if (!r.Repetido)
        {
            try
            {
                foreach (var p in previa.Pedidos)
                    await c.ExecuteAsync(@"
INSERT INTO gestao_baixas_pedido (estabelecimento_id, pedido_id, modalidade, acontecimento_id, criado_em)
VALUES (@loja, @p, @modalidade, @id, @agora)", new { loja, p, modalidade = modalidades[p], id = r.Id, agora = _relogio.GetUtcNow() }, tx);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                throw new GestaoException("DUPLICADO", "Um dos pedidos acabou de ser baixado por outra pessoa.", 409);
            }
        }
        await tx.CommitAsync(ct);
        return new BaixaVendasRegistrada(r.Acontecimento, previa);
    }

    /// <summary>Saldo de cada insumo ativo da loja = soma dos movimentos do livro (entrada, saída, perda, divergência, correções).</summary>
    private static async Task<Dictionary<Guid, SaldoInsumo>> SaldosPorInsumo(NpgsqlConnection c, Guid loja)
    {
        if (!await c.ExecuteScalarAsync<bool>("SELECT to_regclass('insumo') IS NOT NULL"))
            throw Invalido("Os insumos da loja ainda não existem: ative a Gestão e Inteligência.");
        return (await c.QueryAsync<SaldoInsumo>(@"
SELECT i.id AS InsumoId, i.nome, i.unidade,
       coalesce((SELECT sum(m.quantidade) FROM gestao_movimentos_estoque m
                  WHERE m.estabelecimento_id = i.estabelecimento_id AND m.item_id = i.id), 0) AS Saldo
  FROM insumo i WHERE i.estabelecimento_id = @loja AND i.ativo", new { loja })).ToDictionary(s => s.InsumoId);
    }

    private async Task Validar(Guid loja, GestaoPapeis.Acesso acesso, PublicarAcontecimentoRequest pedido, CancellationToken ct)
    {
        var hoje = AcontecimentoRegras.DiaLocal(_relogio.GetUtcNow(), await livro.Fuso(loja, ct));
        GestaoPapeis.ValidarPublicacao(acesso, pedido, hoje, hoje);
    }

    private static string Chave(string? chave) => string.IsNullOrWhiteSpace(chave)
        ? throw Invalido("Envie a chave de idempotência (gerada uma vez por envio, para não registrar duas vezes).")
        : chave.Trim();

    private static string Cortar(string s, int max) => s.Length <= max ? s : s[..max];

    private static GestaoException Invalido(string m) => new("CAMPO_INVALIDO", m, 422);
}
