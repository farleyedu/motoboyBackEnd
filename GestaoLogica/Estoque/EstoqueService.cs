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

/// <summary>Só os números dos pedidos: modalidade e elegibilidade vêm da venda no livro. <c>Assinatura</c> é a da prévia que a pessoa viu.</summary>
public sealed record BaixaVendasRequest(IReadOnlyList<int>? PedidoIds, bool ConfirmacaoHumana, string? Assinatura = null);

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
    Task<CandidatosBaixa> Candidatos(Guid loja, DateOnly dia, bool soHoje, CancellationToken ct);
    Task<BaixaVendasPrevia> PreviaBaixa(Guid loja, IReadOnlyList<int> pedidoIds, bool soHoje, CancellationToken ct);
    Task<BaixaVendasRegistrada> RegistrarBaixa(Guid loja, int usuario, GestaoPapeis.Acesso acesso, BaixaVendasRequest req, CancellationToken ct);
}

public sealed class EstoqueService(NpgsqlDataSource dataSource, IAcontecimentosService livro, IFichaTecnicaLeitor fichas,
    APIBack.GestaoLogica.Vendas.IItensVendidosLeitor itensLeitor, TimeProvider? relogio = null) : IEstoqueService
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

    public async Task<CandidatosBaixa> Candidatos(Guid loja, DateOnly dia, bool soHoje, CancellationToken ct)
    {
        var fuso = await livro.Fuso(loja, ct);
        var hoje = AcontecimentoRegras.DiaLocal(_relogio.GetUtcNow(), fuso);
        if (soHoje) dia = hoje;
        var ini = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(dia.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified), fuso);
        var fim = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(dia.AddDays(1).ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified), fuso);
        await using var c = await dataSource.OpenConnectionAsync(ct);
        var vendas = (await c.QueryAsync<VendaDoDia>(new CommandDefinition(@"
SELECT p.id AS PedidoId, (v.data_fato AT TIME ZONE 'UTC') AS DataFato, v.dados->>'modalidade' AS Modalidade,
       CASE WHEN jsonb_typeof(v.dados->'total') = 'number' THEN (v.dados->>'total')::numeric END AS Total
  FROM gestao_acontecimentos v
  JOIN pedido p ON p.id_estabelecimento = v.estabelecimento_id AND p.id::text = v.origem_id
 WHERE v.estabelecimento_id = @loja AND v.tipo = 'venda' AND v.origem_tipo = 'pedido' AND v.corrige_id IS NULL
   AND v.data_fato >= @ini AND v.data_fato < @fim AND coalesce(p.status_pedido::int, 0) <> 4
   AND NOT EXISTS (SELECT 1 FROM gestao_acontecimentos e WHERE e.estabelecimento_id = v.estabelecimento_id AND e.corrige_id = v.id AND e.correcao = 'estorno')
 ORDER BY v.data_fato, p.id LIMIT 501", new { loja, ini, fim }, cancellationToken: ct))).ToList();
        var truncado = vendas.Count > 500;
        if (truncado) vendas.RemoveAt(500);
        var ids = vendas.Select(v => v.PedidoId).ToArray();
        var baixados = ids.Length == 0 ? new HashSet<int>() : (await c.QueryAsync<int>(new CommandDefinition(
            "SELECT pedido_id FROM gestao_baixas_pedido WHERE estabelecimento_id = @loja AND pedido_id = ANY(@ids)", new { loja, ids }, cancellationToken: ct))).ToHashSet();
        var abertos = vendas.Where(v => !baixados.Contains(v.PedidoId) && Modalidade.Todas.Contains(v.Modalidade ?? "")).ToList();
        var itens = await itensLeitor.Ler(loja, abertos.Select(v => v.PedidoId).ToList(), ct);
        var lidas = await fichas.Ler(loja, itens.Where(i => i.ProdutoId != null).Select(i => i.ProdutoId!.Value).Distinct().ToList(), ct);
        var (_, _, semFicha) = EstoqueRegras.Consumo(abertos.Select(v => new PedidoBaixa(v.PedidoId, v.Modalidade!)).ToList(), itens, lidas);
        var faltas = semFicha.ToDictionary(s => s.PedidoId, s => s.Itens);
        var lista = vendas.Select(v =>
        {
            var doPedido = itens.Where(i => i.PedidoId == v.PedidoId).Select(i => $"{i.Quantidade}× {i.Nome}").ToList();
            var situacao = baixados.Contains(v.PedidoId) ? "baixado"
                : !Modalidade.Todas.Contains(v.Modalidade ?? "") || faltas.ContainsKey(v.PedidoId) ? "sem_ficha" : "pronto";
            return new CandidatoBaixa(v.PedidoId, v.Modalidade ?? "", new DateTimeOffset(DateTime.SpecifyKind(v.DataFato, DateTimeKind.Utc)), v.Total,
                situacao, doPedido, faltas.TryGetValue(v.PedidoId, out var f) ? f : Array.Empty<string>());
        }).ToList();
        return new CandidatosBaixa(dia, lista, truncado);
    }

    public async Task<BaixaVendasPrevia> PreviaBaixa(Guid loja, IReadOnlyList<int> pedidoIds, bool soHoje, CancellationToken ct)
    {
        EstoqueRegras.ValidarPedidos(pedidoIds);
        await using var c = await dataSource.OpenConnectionAsync(ct);
        return await Calcular(c, null, loja, pedidoIds, soHoje, ct);
    }

    public async Task<BaixaVendasRegistrada> RegistrarBaixa(Guid loja, int usuario, GestaoPapeis.Acesso acesso, BaixaVendasRequest req,
        CancellationToken ct)
    {
        var ids = req.PedidoIds ?? Array.Empty<int>();
        EstoqueRegras.ValidarPedidos(ids);
        await using var c = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        // Quem chegar depois espera aqui e já enxerga a baixa da outra pessoa: o mesmo pedido nunca baixa duas vezes.
        foreach (var id in ids.Order())
            await c.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtextextended(@k, 0))", new { k = $"baixa_vendas:{loja}:{id}" }, tx);
        // Tudo é recalculado agora, no servidor: a prévia que a pessoa viu não autoriza nada por si só.
        var previa = await Calcular(c, tx, loja, ids, !acesso.Historico, ct);
        if (previa.JaBaixados.Count > 0)
            throw new GestaoException("DUPLICADO", $"Pedido já baixado: {string.Join(", ", previa.JaBaixados)}.", 409);
        if (previa.Pedidos.Count == 0 || previa.Consumo.Count == 0)
            throw Invalido("Nenhum pedido pode ser baixado: falta a ficha técnica dos produtos (Gestão e Inteligência) ou a venda não está elegível.");
        if (!string.IsNullOrWhiteSpace(req.Assinatura) && req.Assinatura != previa.Assinatura)
            throw new GestaoException("PREVIA_DESATUALIZADA", "Os pedidos ou o consumo mudaram desde a conferência. Confira de novo antes de confirmar.", 409);
        // A mesma seleção de pedidos gera a mesma chave: clicar duas vezes não baixa duas vezes.
        var assinatura = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(",", previa.Pedidos.Order()))))[..32].ToLowerInvariant();
        var modalidades = previa.Modalidades;
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

    /// <summary>
    /// Elegibilidade e consumo, sempre a partir do servidor: só baixa o que tem venda no livro (pedido concluído), sem estorno e não
    /// cancelado; a modalidade é a gravada na venda. Pedido de outra loja é 404.
    /// </summary>
    private async Task<BaixaVendasPrevia> Calcular(NpgsqlConnection c, NpgsqlTransaction? tx, Guid loja, IReadOnlyList<int> pedidoIds, bool soHoje,
        CancellationToken ct)
    {
        var ids = pedidoIds.ToArray();
        var linhas = (await c.QueryAsync<VendaDoPedidoLinha>(new CommandDefinition(@"
SELECT p.id AS PedidoId, p.status_pedido::int AS Status, v.id AS VendaId, (v.data_fato AT TIME ZONE 'UTC') AS DataFato,
       v.dados->>'modalidade' AS Modalidade,
       EXISTS (SELECT 1 FROM gestao_acontecimentos e WHERE e.estabelecimento_id = p.id_estabelecimento AND e.corrige_id = v.id AND e.correcao = 'estorno') AS Estornada
  FROM pedido p
  LEFT JOIN gestao_acontecimentos v ON v.estabelecimento_id = p.id_estabelecimento AND v.tipo = 'venda' AND v.origem_tipo = 'pedido'
       AND v.origem_id = p.id::text AND v.corrige_id IS NULL
 WHERE p.id_estabelecimento = @loja AND p.id = ANY(@ids)", new { loja, ids }, tx, cancellationToken: ct))).ToDictionary(l => l.PedidoId);
        foreach (var fora in ids.Where(i => !linhas.ContainsKey(i)).Take(1))
            throw new GestaoException("NAO_ENCONTRADO", $"Pedido {fora} não encontrado nesta loja.", 404);
        var jaBaixados = (await c.QueryAsync<int>(new CommandDefinition(
            "SELECT pedido_id FROM gestao_baixas_pedido WHERE estabelecimento_id = @loja AND pedido_id = ANY(@ids)", new { loja, ids }, tx, cancellationToken: ct))).ToHashSet();
        var fuso = await livro.Fuso(loja, ct);
        var hoje = AcontecimentoRegras.DiaLocal(_relogio.GetUtcNow(), fuso);

        var elegiveis = new List<PedidoBaixa>();
        var ignorados = new List<PedidoIgnorado>();
        foreach (var id in ids)
        {
            var l = linhas[id];
            if (jaBaixados.Contains(id)) continue;
            if (l.VendaId == null)
                ignorados.Add(new PedidoIgnorado(id, "sem_venda", "O pedido ainda não foi concluído (ou a venda não entrou no livro)."));
            else if (l.Estornada || l.Status == 4)
                ignorados.Add(new PedidoIgnorado(id, "cancelado", "Pedido cancelado: não baixa estoque."));
            else if (!Modalidade.Todas.Contains(l.Modalidade ?? ""))
                ignorados.Add(new PedidoIgnorado(id, "modalidade_desconhecida", "A venda não tem modalidade confiável."));
            else if (soHoje && AcontecimentoRegras.DiaLocal(new DateTimeOffset(DateTime.SpecifyKind(l.DataFato!.Value, DateTimeKind.Utc)), fuso) != hoje)
                ignorados.Add(new PedidoIgnorado(id, "fora_do_dia", "Seu acesso baixa só as vendas de hoje."));
            else elegiveis.Add(new PedidoBaixa(id, l.Modalidade!));
        }

        var itens = await itensLeitor.Ler(loja, elegiveis.Select(p => p.PedidoId).ToList(), ct);
        var lidas = await fichas.Ler(loja, itens.Where(i => i.ProdutoId != null).Select(i => i.ProdutoId!.Value).Distinct().ToList(), ct);
        var (consumo, baixaveis, semFicha) = EstoqueRegras.Consumo(elegiveis, itens, lidas);
        var insumos = consumo.Select(x => x.InsumoId).ToArray();
        var categorias = insumos.Length == 0 ? new Dictionary<Guid, string?>() : (await c.QueryAsync<CategoriaInsumo>(new CommandDefinition(
            "SELECT id AS Id, categoria AS Categoria FROM insumo WHERE estabelecimento_id = @loja AND id = ANY(@insumos)", new { loja, insumos }, tx, cancellationToken: ct)))
            .ToDictionary(x => x.Id, x => x.Categoria);
        consumo = consumo.Select(x => x with { Embalagem = APIBack.GestaoLogica.Vendas.FichaRegras.Embalagem(categorias.GetValueOrDefault(x.InsumoId)) }).ToList();

        var modalidades = elegiveis.Where(p => baixaveis.Contains(p.PedidoId)).ToDictionary(p => p.PedidoId, p => p.Modalidade);
        var texto = string.Join(";", baixaveis.Order().Select(p => $"{p}:{modalidades[p]}")) + "|" +
            string.Join(";", consumo.OrderBy(x => x.InsumoId).Select(x => $"{x.InsumoId}:{x.Quantidade.ToString(System.Globalization.CultureInfo.InvariantCulture)}"));
        var assinatura = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(texto)))[..32].ToLowerInvariant();
        return new BaixaVendasPrevia(consumo, baixaveis, semFicha, jaBaixados.Order().ToList(), ignorados, modalidades, assinatura);
    }

    private sealed class CategoriaInsumo
    {
        public Guid Id { get; set; }
        public string? Categoria { get; set; }
    }

    private sealed class VendaDoDia
    {
        public int PedidoId { get; set; }
        public DateTime DataFato { get; set; }
        public string? Modalidade { get; set; }
        public decimal? Total { get; set; }
    }

    private sealed class VendaDoPedidoLinha
    {
        public int PedidoId { get; set; }
        public int? Status { get; set; }
        public Guid? VendaId { get; set; }
        public DateTime? DataFato { get; set; }
        public string? Modalidade { get; set; }
        public bool Estornada { get; set; }
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
