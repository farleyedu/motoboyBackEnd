using APIBack.Acontecimentos;
using APIBack.GestaoLogica.Estoque;
using APIBack.Integrations.Ifood.Orders;
using APIBack.Repository;
using APIBack.Service;
using Dapper;
using Npgsql;

namespace APIBack.GestaoLogica.Vendas;

public interface IVendasCatalogoService
{
    Task<IReadOnlyList<ProdutoVendidoDto>> ProdutosVendidos(Guid loja, CancellationToken ct);
    Task<IReadOnlyList<VinculoVendaDto>> DeParaVendas(Guid loja, CancellationToken ct);
    Task<VinculoVendaDto> VincularVenda(Guid loja, int usuario, string? vinculoId, Guid? produtoId, CancellationToken ct);
    Task<FichaDto?> Ficha(Guid loja, Guid produtoId, CancellationToken ct);
    Task<FichaDto> SalvarFicha(Guid loja, int usuario, Guid produtoId, IReadOnlyList<ComponenteRequest>? componentes, CancellationToken ct);
}

/// <summary>
/// Produtos vendidos (cardapio_produto), de-para de vendas (iFood em ifood_produto_vinculo; cardápio em gestao_venda_vinculo) e ficha
/// técnica por produto. Nada aqui move estoque nem grava acontecimento: é só cadastro. A loja vem sempre do token.
/// </summary>
public sealed class VendasCatalogoService(NpgsqlDataSource dataSource, IfoodProdutoVinculoRepository ifood) : IVendasCatalogoService
{
    private const string Ifood = "ifood", Cardapio = "cardapio";

    private sealed class ProdutoRow { public Guid Id { get; set; } public string Nome { get; set; } = ""; public string? Foto { get; set; } public decimal Preco { get; set; } public bool TemFicha { get; set; } }

    public async Task<IReadOnlyList<ProdutoVendidoDto>> ProdutosVendidos(Guid loja, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        var temFicha = await Existe(c, "gestao_ficha_componente", ct);
        var linhas = await c.QueryAsync<ProdutoRow>(new CommandDefinition($"""
SELECT p.id AS Id, p.nome AS Nome, p.imagem_url AS Foto, p.preco_base AS Preco,
       {(temFicha ? "EXISTS (SELECT 1 FROM gestao_ficha_componente f WHERE f.estabelecimento_id = p.id_estabelecimento AND f.produto_id = p.id)" : "FALSE")} AS TemFicha
  FROM cardapio_produto p WHERE p.id_estabelecimento = @loja AND p.deleted_at IS NULL ORDER BY lower(p.nome), p.id
""", new { loja }, cancellationToken: ct));
        return linhas.Select(p => new ProdutoVendidoDto(p.Id, p.Nome, string.IsNullOrWhiteSpace(p.Foto) ? null : p.Foto, p.TemFicha ? p.Id : null, p.Preco)).ToList();
    }

    // ------------------------------------------------------------------------------------------------ de-para de vendas

    private sealed class IfoodRow
    {
        public Guid Id { get; set; } public string ChaveId { get; set; } = ""; public string Nome { get; set; } = ""; public Guid? ProdutoId { get; set; }
        public decimal? Preco { get; set; } public decimal? PrecoProduto { get; set; } public DateTimeOffset Visto { get; set; }
        public DateTimeOffset? ConfirmadoEm { get; set; } public string? ConfirmadoPor { get; set; }
    }
    private sealed class MesRow { public string Chave { get; set; } = ""; public string Mes { get; set; } = ""; public int Qtd { get; set; } }
    private sealed class AvulsoRow { public string Nome { get; set; } = ""; public string Mes { get; set; } = ""; public int Qtd { get; set; } public decimal Preco { get; set; } }
    private sealed class MapaRow
    {
        public string NomeChave { get; set; } = ""; public string Nome { get; set; } = ""; public Guid? ProdutoId { get; set; }
        public decimal? PrecoProduto { get; set; } public DateTimeOffset ConfirmadoEm { get; set; } public string? ConfirmadoPor { get; set; }
    }

    public async Task<IReadOnlyList<VinculoVendaDto>> DeParaVendas(Guid loja, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        var desde = DateTimeOffset.UtcNow.AddDays(-100);
        var fuso = await Fuso(c, loja, ct);
        var saida = new List<VinculoVendaDto>();
        if (await TemIfood(c, ct))
        {
            var linhas = (await c.QueryAsync<IfoodRow>(new CommandDefinition("""
SELECT v.id AS Id, v.chave_id AS ChaveId, v.nome_ifood AS Nome, p.id AS ProdutoId, p.preco_base AS PrecoProduto,
       v.visto_primeira_em AS Visto, v.confirmado_em AS ConfirmadoEm, (SELECT u.nome::text FROM usuario u WHERE u.id = v.confirmado_por) AS ConfirmadoPor,
       -- Preço unitário do item no pedido mais recente que o trouxe (pode ter promoção; só orienta a comparação).
       (SELECT (i->>'unitPrice')::numeric FROM ifood_order_snapshots s
         CROSS JOIN LATERAL jsonb_array_elements(CASE WHEN jsonb_typeof(s.payload->'items') = 'array' THEN s.payload->'items' ELSE '[]'::jsonb END) i
         WHERE s.estabelecimento_id = v.estabelecimento_id AND s.merchant_id = v.merchant_id AND i->>'id' = v.chave_id
           AND i->>'unitPrice' ~ '^[0-9]+(\.[0-9]+)?$' ORDER BY s.updated_at_utc DESC LIMIT 1) AS Preco
  FROM ifood_produto_vinculo v
  LEFT JOIN cardapio_produto p ON p.id = v.produto_id AND p.id_estabelecimento = v.estabelecimento_id AND p.deleted_at IS NULL AND v.estado = 'confirmado'
 WHERE v.estabelecimento_id = @loja AND v.tipo = 'item' ORDER BY v.visto_ultima_em DESC, v.id
""", new { loja }, cancellationToken: ct))).ToList();
            var meses = (await c.QueryAsync<MesRow>(new CommandDefinition("""
SELECT t.item->>'id' AS Chave, to_char(l.created_at_utc AT TIME ZONE @fuso, 'YYYY-MM') AS Mes,
       sum(CASE WHEN t.item->>'quantity' ~ '^[0-9]+$' THEN (t.item->>'quantity')::int ELSE 0 END)::int AS Qtd
  FROM ifood_order_links l
  JOIN ifood_order_snapshots s ON s.merchant_id = l.merchant_id AND s.external_order_id = l.external_order_id AND s.estabelecimento_id = l.estabelecimento_id
  CROSS JOIN LATERAL jsonb_array_elements(CASE WHEN jsonb_typeof(s.payload->'items') = 'array' THEN s.payload->'items' ELSE '[]'::jsonb END) AS t(item)
 WHERE l.estabelecimento_id = @loja AND l.created_at_utc >= @desde GROUP BY 1, 2
""", new { loja, desde, fuso }, cancellationToken: ct)))
                .GroupBy(m => m.Chave).ToDictionary(g => g.Key, g => (IReadOnlyDictionary<string, int>)g.GroupBy(m => m.Mes).ToDictionary(x => x.Key, x => x.Sum(m => m.Qtd)));
            foreach (var l in linhas)
            {
                var id = $"{Ifood}:{l.Id}";
                saida.Add(new VinculoVendaDto(id, Ifood, l.Nome, l.ProdutoId, l.Preco ?? l.PrecoProduto ?? 0,
                    meses.TryGetValue(l.ChaveId, out var m) ? m : new Dictionary<string, int>(),
                    l.ProdutoId != null && l.Preco != null && l.PrecoProduto != null && Math.Abs(l.Preco.Value - l.PrecoProduto.Value) > 0.009m,
                    Historico(id, l.ProdutoId != null, l.ConfirmadoEm ?? l.Visto, l.ConfirmadoPor, l.Nome)));
            }
        }
        saida.AddRange(await AvulsosDoCardapio(c, loja, desde, fuso, ct));
        return saida;
    }

    /// <summary>Linhas avulsas do cardápio (sem produto) agrupadas por nome, mais os nomes que a Gestão já ligou a um produto.</summary>
    private static async Task<IEnumerable<VinculoVendaDto>> AvulsosDoCardapio(NpgsqlConnection c, Guid loja, DateTimeOffset desde, string fuso, CancellationToken ct)
    {
        var avulsos = (await c.QueryAsync<AvulsoRow>(new CommandDefinition("""
SELECT pi.nome AS Nome, to_char(p.created_at AT TIME ZONE @fuso, 'YYYY-MM') AS Mes, pi.quantidade AS Qtd, pi.preco_unitario AS Preco
  FROM pedido_item pi JOIN pedido p ON p.id = pi.pedido_id
 WHERE p.id_estabelecimento = @loja AND pi.produto_id IS NULL AND p.created_at >= @desde
 ORDER BY p.created_at DESC, pi.id DESC
""", new { loja, desde, fuso }, cancellationToken: ct))).ToList();
        var mapa = await Existe(c, "gestao_venda_vinculo", ct) ? (await c.QueryAsync<MapaRow>(new CommandDefinition("""
SELECT v.nome_chave AS NomeChave, v.nome AS Nome, p.id AS ProdutoId, p.preco_base AS PrecoProduto, v.confirmado_em AS ConfirmadoEm,
       (SELECT u.nome::text FROM usuario u WHERE u.id = v.confirmado_por) AS ConfirmadoPor
  FROM gestao_venda_vinculo v LEFT JOIN cardapio_produto p ON p.id = v.produto_id AND p.id_estabelecimento = v.estabelecimento_id AND p.deleted_at IS NULL
 WHERE v.estabelecimento_id = @loja AND v.canal = 'cardapio'
""", new { loja }, cancellationToken: ct))).ToDictionary(m => m.NomeChave) : new Dictionary<string, MapaRow>();

        var porNome = avulsos.GroupBy(a => IfoodItemLinkRules.NormalizeName(a.Nome)).Where(g => g.Key.Length > 0).ToDictionary(g => g.Key);
        var saida = new List<VinculoVendaDto>();
        foreach (var chave in porNome.Keys.Union(mapa.Keys).OrderBy(k => k, StringComparer.Ordinal))
        {
            porNome.TryGetValue(chave, out var vendas);
            mapa.TryGetValue(chave, out var ligado);
            var nome = vendas?.First().Nome ?? ligado!.Nome;
            var preco = vendas?.First().Preco ?? 0;
            var id = $"{Cardapio}:{chave}";
            saida.Add(new VinculoVendaDto(id, Cardapio, nome, ligado?.ProdutoId, preco,
                vendas?.GroupBy(v => v.Mes).ToDictionary(g => g.Key, g => g.Sum(v => v.Qtd)) ?? new Dictionary<string, int>(),
                ligado?.PrecoProduto != null && vendas != null && Math.Abs(preco - ligado.PrecoProduto.Value) > 0.009m,
                Historico(id, ligado?.ProdutoId != null, ligado?.ConfirmadoEm ?? DateTimeOffset.UtcNow, ligado?.ConfirmadoPor, nome)));
        }
        return saida;
    }

    private static IReadOnlyList<EventoVendaDto> Historico(string id, bool ligado, DateTimeOffset quando, string? quem, string nome) => new[]
    {
        new EventoVendaDto($"h-{id}", quando, ligado ? "Vinculado ao produto vendido" : "Lido pela primeira vez no canal", ligado ? nome : null, ligado ? quem : null),
    };

    public async Task<VinculoVendaDto> VincularVenda(Guid loja, int usuario, string? vinculoId, Guid? produtoId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(vinculoId)) throw Invalido("Informe o item do canal.");
        if (produtoId is not { } produto || produto == Guid.Empty) throw Invalido("Informe o produto vendido.");
        var partes = vinculoId.Split(':', 2);
        if (partes.Length != 2 || partes[1].Length == 0) throw NaoEncontrado("Item do canal");
        if (partes[0] == Ifood && Guid.TryParse(partes[1], out var vinculo))
        {
            try { await ifood.VincularAsync(loja, usuario, produto, vinculo, ct); }
            catch (DeliveryDomainException ex) { throw new GestaoException(ex.Code, ex.Message, ex.StatusCode); }
        }
        else if (partes[0] == Cardapio) await VincularCardapio(loja, usuario, partes[1], produto, ct);
        else throw NaoEncontrado("Item do canal");
        return (await DeParaVendas(loja, ct)).FirstOrDefault(v => v.Id == vinculoId) ?? throw NaoEncontrado("Item do canal");
    }

    private static async Task VincularCardapio(NpgsqlDataSource dataSource, Guid loja, int usuario, string nomeChave, Guid produto, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        if (!await Existe(c, "gestao_venda_vinculo", ct)) throw new GestaoException("INDISPONIVEL", "O de-para de vendas do cardápio ainda não está ativo nesta loja.", 409);
        await ProdutoDaLoja(c, null, loja, produto, ct);
        // O item precisa ser uma linha avulsa real desta loja, ou um nome já ligado antes.
        var existe = await c.ExecuteScalarAsync<bool>(new CommandDefinition("""
SELECT EXISTS (SELECT 1 FROM pedido_item pi JOIN pedido p ON p.id = pi.pedido_id
                WHERE p.id_estabelecimento = @loja AND pi.produto_id IS NULL AND lower(btrim(regexp_replace(pi.nome, '\s+', ' ', 'g'))) = @nomeChave)
    OR EXISTS (SELECT 1 FROM gestao_venda_vinculo WHERE estabelecimento_id = @loja AND canal = 'cardapio' AND nome_chave = @nomeChave)
""", new { loja, nomeChave }, cancellationToken: ct));
        if (!existe) throw NaoEncontrado("Item do canal");
        await c.ExecuteAsync(new CommandDefinition("""
INSERT INTO gestao_venda_vinculo (estabelecimento_id, canal, nome_chave, nome, produto_id, confirmado_por)
SELECT @loja, 'cardapio', @nomeChave, coalesce((SELECT pi.nome FROM pedido_item pi JOIN pedido p ON p.id = pi.pedido_id
        WHERE p.id_estabelecimento = @loja AND pi.produto_id IS NULL AND lower(btrim(regexp_replace(pi.nome, '\s+', ' ', 'g'))) = @nomeChave
        ORDER BY pi.id DESC LIMIT 1), (SELECT nome FROM gestao_venda_vinculo WHERE estabelecimento_id = @loja AND canal = 'cardapio' AND nome_chave = @nomeChave)), @produto, @usuario
ON CONFLICT (estabelecimento_id, canal, nome_chave) DO UPDATE SET produto_id = EXCLUDED.produto_id, confirmado_por = EXCLUDED.confirmado_por, confirmado_em = now()
""", new { loja, nomeChave, produto, usuario }, cancellationToken: ct));
    }

    private Task VincularCardapio(Guid loja, int usuario, string nomeChave, Guid produto, CancellationToken ct) =>
        VincularCardapio(dataSource, loja, usuario, nomeChave, produto, ct);

    // ------------------------------------------------------------------------------------------------ ficha técnica

    private sealed class ComponenteRow
    {
        public Guid InsumoId { get; set; } public string Nome { get; set; } = ""; public decimal Quantidade { get; set; } public string Unidade { get; set; } = "";
        public string[]? Modalidades { get; set; } public string? Categoria { get; set; } public decimal? CustoBase { get; set; } public DateTimeOffset Em { get; set; }
    }

    private sealed class InsumoRow { public Guid Id { get; set; } public string Nome { get; set; } = ""; public string Unidade { get; set; } = ""; public string? Categoria { get; set; } public bool Ativo { get; set; } }

    public async Task<FichaDto?> Ficha(Guid loja, Guid produtoId, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        await ProdutoDaLoja(c, null, loja, produtoId, ct);
        return await LerFicha(c, null, loja, produtoId, ct);
    }

    private static async Task<FichaDto?> LerFicha(NpgsqlConnection c, NpgsqlTransaction? tx, Guid loja, Guid produtoId, CancellationToken ct)
    {
        if (!await Existe(c, "gestao_ficha_componente", ct, tx)) return null;
        var linhas = (await c.QueryAsync<ComponenteRow>(new CommandDefinition("""
SELECT f.insumo_id AS InsumoId, i.nome AS Nome, f.quantidade AS Quantidade, f.unidade AS Unidade, f.modalidades AS Modalidades,
       i.categoria AS Categoria, f.atualizado_em AS Em,
       (SELECT k.custo_base FROM insumo_custo k WHERE k.estabelecimento_id = f.estabelecimento_id AND k.insumo_id = f.insumo_id AND k.vigencia_inicio <= now()
         ORDER BY k.vigencia_inicio DESC, k.criado_em DESC LIMIT 1) AS CustoBase
  FROM gestao_ficha_componente f JOIN insumo i ON i.estabelecimento_id = f.estabelecimento_id AND i.id = f.insumo_id
 WHERE f.estabelecimento_id = @loja AND f.produto_id = @produtoId ORDER BY f.ordem, f.id
""", new { loja, produtoId }, tx, cancellationToken: ct))).ToList();
        if (linhas.Count == 0) return null;
        var componentes = linhas.Select(l => new ComponenteFichaDto(l.InsumoId, l.Nome, l.Quantidade, l.Unidade, l.Modalidades, FichaRegras.Embalagem(l.Categoria),
            l.CustoBase is { } b ? Math.Round(b * l.Quantidade, 4) : null)).ToList();
        var porModalidade = Modalidade.Todas.ToDictionary(m => m, m =>
        {
            var doGrupo = componentes.Where(x => FichaRegras.Alcance(x.Modalidades).Contains(m))
                .Select(x => (x.Embalagem, Linha: new LinhaFichaDto(x.InsumoId, x.Nome, x.Quantidade, x.Unidade, x.Custo))).ToList();
            return new ModalidadeFichaDto(doGrupo.Where(x => !x.Embalagem).Select(x => x.Linha).ToList(), doGrupo.Where(x => x.Embalagem).Select(x => x.Linha).ToList());
        });
        return new FichaDto(produtoId, produtoId, linhas.Max(l => l.Em), componentes, porModalidade);
    }

    public async Task<FichaDto> SalvarFicha(Guid loja, int usuario, Guid produtoId, IReadOnlyList<ComponenteRequest>? componentes, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        if (!await Existe(c, "gestao_ficha_componente", ct)) throw new GestaoException("INDISPONIVEL", "A ficha técnica ainda não está ativa nesta loja.", 409);
        await using var tx = await c.BeginTransactionAsync(ct);
        await ProdutoDaLoja(c, tx, loja, produtoId, ct);
        var ids = (componentes ?? Array.Empty<ComponenteRequest>()).Select(x => x.InsumoId).Distinct().ToArray();
        var insumos = (await c.QueryAsync<InsumoRow>(new CommandDefinition(
            "SELECT id AS Id, nome AS Nome, unidade AS Unidade, categoria AS Categoria, ativo AS Ativo FROM insumo WHERE estabelecimento_id = @loja AND id = ANY(@ids)",
            new { loja, ids }, tx, cancellationToken: ct))).ToDictionary(i => i.Id, i => new InsumoFicha(i.Nome, i.Unidade, i.Categoria, i.Ativo));
        var validos = FichaRegras.Validar(componentes, insumos);
        // A ficha inteira troca de uma vez: o produto nunca fica com metade antiga e metade nova.
        await c.ExecuteAsync(new CommandDefinition("DELETE FROM gestao_ficha_componente WHERE estabelecimento_id = @loja AND produto_id = @produtoId",
            new { loja, produtoId }, tx, cancellationToken: ct));
        var ordem = 0;
        foreach (var v in validos)
            await c.ExecuteAsync(new CommandDefinition("""
INSERT INTO gestao_ficha_componente (estabelecimento_id, produto_id, insumo_id, quantidade, unidade, modalidades, ordem, atualizado_por)
VALUES (@loja, @produtoId, @InsumoId, @Quantidade, @Unidade, @Modalidades, @ordem, @usuario)
""", new { loja, produtoId, v.InsumoId, v.Quantidade, v.Unidade, Modalidades = v.Modalidades?.ToArray(), ordem = ordem++, usuario }, tx, cancellationToken: ct));
        var ficha = await LerFicha(c, tx, loja, produtoId, ct);
        await tx.CommitAsync(ct);
        return ficha ?? new FichaDto(produtoId, produtoId, null, Array.Empty<ComponenteFichaDto>(),
            Modalidade.Todas.ToDictionary(m => m, _ => new ModalidadeFichaDto(Array.Empty<LinhaFichaDto>(), Array.Empty<LinhaFichaDto>())));
    }

    // ------------------------------------------------------------------------------------------------ apoio

    private static async Task ProdutoDaLoja(NpgsqlConnection c, NpgsqlTransaction? tx, Guid loja, Guid produtoId, CancellationToken ct)
    {
        if (!await c.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS (SELECT 1 FROM cardapio_produto WHERE id = @produtoId AND id_estabelecimento = @loja AND deleted_at IS NULL)", new { produtoId, loja }, tx, cancellationToken: ct)))
            throw NaoEncontrado("Produto vendido");
    }

    private static Task<bool> Existe(NpgsqlConnection c, string tabela, CancellationToken ct, NpgsqlTransaction? tx = null) =>
        c.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT to_regclass(@tabela) IS NOT NULL", new { tabela }, tx, cancellationToken: ct));

    private static async Task<bool> TemIfood(NpgsqlConnection c, CancellationToken ct) =>
        await Existe(c, "ifood_produto_vinculo", ct) && await Existe(c, "ifood_order_links", ct) && await Existe(c, "ifood_order_snapshots", ct);

    private static Task<string> Fuso(NpgsqlConnection c, Guid loja, CancellationToken ct) => c.ExecuteScalarAsync<string>(new CommandDefinition(
        "SELECT coalesce((SELECT timezone_iana FROM estabelecimentos WHERE id = @loja), 'America/Sao_Paulo')", new { loja }, cancellationToken: ct))!;

    private static GestaoException Invalido(string mensagem) => new("CAMPO_INVALIDO", mensagem, 422);
    private static GestaoException NaoEncontrado(string o) => new("NAO_ENCONTRADO", $"{o} não encontrado nesta loja.", 404);
}
