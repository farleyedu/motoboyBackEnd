using System.Data;
using APIBack.Acontecimentos;
using APIBack.GestaoLogica.Documentos;
using APIBack.GestaoLogica.Vinculos;
using Dapper;
using Npgsql;

namespace APIBack.GestaoLogica.Catalogo;

/// <summary>
/// Catálogo da loja e global (Integração 6B). O global é consulta: o item vira insumo da loja ao ser importado ou ao ter vínculo
/// confirmado numa compra (regra 6). Nada aqui mexe no estoque nem no financeiro. Curadoria é só leitura daqui: "sugerir ao
/// Zippy" cria/aponta o candidato, quem decide é a curadoria (GestaoCuradoriaController).
/// </summary>
public interface ICatalogoService
{
    Task<IReadOnlyList<ProdutoLojaDto>> Produtos(Guid loja, CancellationToken ct);
    Task<GlobalResultadoDto> Global(Guid loja, string? busca, CancellationToken ct);
    Task<ProdutoLojaDto> Importar(Guid loja, int usuario, Guid globalId, IReadOnlyList<string>? finalidades, CancellationToken ct);
    Task<KitImportadoDto> ImportarKit(Guid loja, int usuario, Guid kitId, CancellationToken ct);
    Task<ProdutoLojaDto> Criar(Guid loja, int usuario, CriarProdutoRequest req, CancellationToken ct);
    Task<ProdutoLojaDto> Finalidades(Guid loja, int usuario, Guid insumoId, IReadOnlyList<string>? finalidades, CancellationToken ct);
    Task<IReadOnlyList<DeParaCompraDto>> DeParaCompras(Guid loja, CancellationToken ct);
    Task<DeParaCompraDto> NaoControlar(Guid loja, int usuario, NaoControlarRequest req, CancellationToken ct);
    Task<DeParaCompraDto> Sugerir(Guid loja, int usuario, Guid vinculoId, CancellationToken ct);
    Task<DeParaCompraDto> Vincular(Guid loja, int usuario, VincularDeParaRequest req, CancellationToken ct);
    Task<BuscaCatalogoDto> Buscar(Guid loja, string? texto, CancellationToken ct);
}

public sealed class CatalogoService(NpgsqlDataSource dataSource, IVinculosService vinculos, TimeProvider? relogio = null) : ICatalogoService
{
    private readonly TimeProvider _relogio = relogio ?? TimeProvider.System;

    /// <summary>De-para olha as compras dos últimos 12 meses (usos por mês e última compra).</summary>
    private const int MesesDeUso = 12;
    private const int LimiteGlobal = 60, LimiteBusca = 8;

    private sealed class ProdutoRow
    {
        public Guid Id { get; set; }
        public string Nome { get; set; } = "";
        public string Unidade { get; set; } = "";
        public string? CategoriaInsumo { get; set; }
        public DateTime CriadoEm { get; set; }
        public string? OrigemLoja { get; set; }
        public string[]? Finalidades { get; set; }
        public Guid? GlobalId { get; set; }
        public string? CategoriaGlobal { get; set; }
        public string? StatusGlobal { get; set; }
        public string? Ilustracao { get; set; }
        public decimal Saldo { get; set; }
    }

    private sealed class EventoRow
    {
        public Guid Id { get; set; }
        public Guid? InsumoId { get; set; }
        public string? NomeChave { get; set; }
        public string Tipo { get; set; } = "";
        public string? Detalhe { get; set; }
        public string? Antes { get; set; }
        public string? Depois { get; set; }
        public string? PorNome { get; set; }
        public DateTime Em { get; set; }
    }

    private sealed class VinculoRow
    {
        public Guid Id { get; set; }
        public string FornecedorCnpj { get; set; } = "";
        public string NomeChave { get; set; } = "";
        public string Descricao { get; set; } = "";
        public string UnidadeCupom { get; set; } = "";
        public Guid InsumoId { get; set; }
        public string? InsumoNome { get; set; }
        public Guid? GlobalId { get; set; }
        public string? StatusGlobal { get; set; }
        public string? OrigemLoja { get; set; }
        public string UnidadeBase { get; set; } = "";
        public decimal Fator { get; set; }
        public DateTime ConfirmadoEm { get; set; }
        public string? ConfirmadoPorNome { get; set; }
    }

    private sealed class GlobalRow
    {
        public Guid Id { get; set; }
        public string Nome { get; set; } = "";
        public string? Categoria { get; set; }
        public string Unidade { get; set; } = "";
        public string? Ilustracao { get; set; }
        public int Lojas { get; set; }
        public Guid? InsumoId { get; set; }
    }

    // ------------------------------------------------------------------ produtos da loja

    public async Task<IReadOnlyList<ProdutoLojaDto>> Produtos(Guid loja, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        if (!await TemInsumos(c, null)) return [];
        return await Produtos(c, null, loja, null);
    }

    private static async Task<IReadOnlyList<ProdutoLojaDto>> Produtos(NpgsqlConnection c, NpgsqlTransaction? tx, Guid loja, Guid[]? ids)
    {
        // Item global do insumo: o do catálogo da loja ou, sem ele, o do vínculo mais recente.
        var linhas = (await c.QueryAsync<ProdutoRow>(@"
SELECT i.id AS Id, i.nome AS Nome, i.unidade AS Unidade, i.categoria AS CategoriaInsumo, i.criado_em AS CriadoEm,
       cl.origem AS OrigemLoja, cl.finalidades AS Finalidades, g.id AS GlobalId, g.categoria AS CategoriaGlobal,
       g.status AS StatusGlobal, g.ilustracao AS Ilustracao, coalesce(s.saldo, 0) AS Saldo
  FROM insumo i
  LEFT JOIN gestao_catalogo_loja cl ON cl.estabelecimento_id = i.estabelecimento_id AND cl.insumo_id = i.id
  LEFT JOIN LATERAL (SELECT v.global_id FROM gestao_vinculos v
                      WHERE v.estabelecimento_id = i.estabelecimento_id AND v.insumo_id = i.id AND v.global_id IS NOT NULL
                      ORDER BY v.confirmado_em DESC LIMIT 1) vg ON true
  LEFT JOIN gestao_catalogo_global g ON g.id = coalesce(cl.global_id, vg.global_id)
  LEFT JOIN (SELECT item_id, sum(quantidade) AS saldo FROM gestao_movimentos_estoque
              WHERE estabelecimento_id = @loja GROUP BY item_id) s ON s.item_id = i.id
 WHERE i.estabelecimento_id = @loja AND i.ativo AND (@ids::uuid[] IS NULL OR i.id = ANY(@ids))
 ORDER BY i.nome", new { loja, ids }, tx)).ToList();
        if (linhas.Count == 0) return [];
        var insumos = linhas.Select(l => l.Id).ToArray();

        var nomes = (await c.QueryAsync<(Guid InsumoId, string Descricao, DateTime Em, string? Por)>(@"
SELECT v.insumo_id, v.descricao, v.confirmado_em, (SELECT u.nome::text FROM usuario u WHERE u.id = v.confirmado_por)
  FROM gestao_vinculos v WHERE v.estabelecimento_id = @loja AND v.estado = 'confirmado' AND v.insumo_id = ANY(@insumos)
 ORDER BY v.confirmado_em", new { loja, insumos }, tx)).ToLookup(n => n.InsumoId);
        var eventos = (await LerEventos(c, tx, loja, insumos, null)).ToLookup(e => e.InsumoId);

        return linhas.Select(l =>
        {
            var historico = eventos[l.Id].Select(Evento)
                .Concat(nomes[l.Id].Select(n => new EventoCatalogoDto(Guid.Empty, Utc(n.Em), "Vínculo confirmado", n.Descricao, n.Por)))
                .OrderByDescending(e => e.Quando).Take(30).ToList();
            if (historico.Count == 0)
                historico.Add(new EventoCatalogoDto(l.Id, Utc(l.CriadoEm), "Cadastrado na Gestão e Inteligência", null, null));
            var validado = l.StatusGlobal is "validado" or "mesclado";
            var origem = l.OrigemLoja ?? (validado ? OrigemCatalogo.Global : OrigemCatalogo.Loja);
            return new ProdutoLojaDto(l.Id, l.Nome,
                l.CategoriaGlobal ?? l.CategoriaInsumo ?? "Sem categoria",
                l.StatusGlobal == "candidato" && l.CategoriaGlobal is not null && l.CategoriaGlobal != l.CategoriaInsumo,
                l.Finalidades ?? [], l.Unidade, validado ? l.Ilustracao : null, origem, CuradoriaLoja.De(l.StatusGlobal), l.GlobalId,
                nomes[l.Id].Select(n => n.Descricao).Distinct().ToList(), l.Saldo, historico, Utc(l.CriadoEm));
        }).ToList();
    }

    // ------------------------------------------------------------------ catálogo global

    public async Task<GlobalResultadoDto> Global(Guid loja, string? busca, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        var termo = string.IsNullOrWhiteSpace(busca) ? null : "%" + DocumentoRegras.NomeChave(busca) + "%";
        var temInsumos = await TemInsumos(c, null);
        var produtos = (await c.QueryAsync<GlobalRow>($@"
SELECT g.id AS Id, g.nome AS Nome, g.categoria AS Categoria, g.unidade AS Unidade, g.ilustracao AS Ilustracao,
       (SELECT count(DISTINCT x.loja) FROM (
            SELECT v.estabelecimento_id AS loja FROM gestao_vinculos v WHERE v.global_id = g.id AND v.estado = 'confirmado'
            UNION SELECT cl.estabelecimento_id FROM gestao_catalogo_loja cl WHERE cl.global_id = g.id) x)::int AS Lojas,
       {InsumoDoGlobal(temInsumos)} AS InsumoId
  FROM gestao_catalogo_global g
 WHERE g.status = 'validado'
   AND (@termo::text IS NULL OR g.nome_chave LIKE @termo OR upper(coalesce(g.categoria, '')) LIKE @termo)
 ORDER BY g.nome LIMIT {LimiteGlobal}", new { loja, termo })).ToList();
        var total = await c.ExecuteScalarAsync<int>(@"
SELECT count(*)::int FROM gestao_catalogo_global g WHERE g.status = 'validado'
   AND (@termo::text IS NULL OR g.nome_chave LIKE @termo OR upper(coalesce(g.categoria, '')) LIKE @termo)", new { termo });
        var kits = (await c.QueryAsync<(Guid Id, string Nome, string? Descricao, Guid? GlobalId)>(@"
SELECT k.id, k.nome, k.descricao, i.global_id FROM gestao_catalogo_kits k
  LEFT JOIN gestao_catalogo_kit_itens i ON i.kit_id = k.id
 WHERE k.ativo ORDER BY k.nome, i.ordem")).GroupBy(k => (k.Id, k.Nome, k.Descricao))
            .Select(g => new KitDto(g.Key.Id, g.Key.Nome, g.Key.Descricao, g.Where(x => x.GlobalId is not null).Select(x => x.GlobalId!.Value).ToList()))
            .ToList();
        return new GlobalResultadoDto(produtos.Select(p => new ProdutoGlobalDto(p.Id, p.Nome, p.Categoria, p.Unidade, p.Ilustracao, p.Lojas,
            p.InsumoId is not null, p.InsumoId)).ToList(), total, kits);
    }

    public async Task<ProdutoLojaDto> Importar(Guid loja, int usuario, Guid globalId, IReadOnlyList<string>? finalidades, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await ExigirInsumos(c, tx);
        var g = await c.QuerySingleOrDefaultAsync<GlobalRow>(@"
SELECT id AS Id, nome AS Nome, categoria AS Categoria, unidade AS Unidade, ilustracao AS Ilustracao
  FROM gestao_catalogo_global WHERE id = @globalId AND status = 'validado'", new { globalId }, tx)
            ?? throw new GestaoException("NAO_ENCONTRADO", "Produto do catálogo Zippy não encontrado.", 404);
        var id = await ImportarInterno(c, tx, loja, usuario, g, CatalogoRegras.NormalizarFinalidades(finalidades))
            ?? throw new GestaoException("DUPLICADO", $"{g.Nome} já está na sua loja.", 409);
        await tx.CommitAsync(ct);
        return (await Produtos(c, null, loja, [id])).Single();
    }

    public async Task<KitImportadoDto> ImportarKit(Guid loja, int usuario, Guid kitId, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await ExigirInsumos(c, tx);
        if (!await c.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM gestao_catalogo_kits WHERE id = @kitId AND ativo)", new { kitId }, tx))
            throw new GestaoException("NAO_ENCONTRADO", "Kit não encontrado.", 404);
        var itens = (await c.QueryAsync<GlobalRow>(@"
SELECT g.id AS Id, g.nome AS Nome, g.categoria AS Categoria, g.unidade AS Unidade, g.ilustracao AS Ilustracao
  FROM gestao_catalogo_kit_itens i JOIN gestao_catalogo_global g ON g.id = i.global_id AND g.status = 'validado'
 WHERE i.kit_id = @kitId ORDER BY i.ordem", new { kitId }, tx)).ToList();
        var novos = new List<Guid>();
        var jaExistiam = new List<string>();
        foreach (var g in itens)
        {
            if (await ImportarInterno(c, tx, loja, usuario, g, []) is { } id) novos.Add(id);
            else jaExistiam.Add(g.Nome);
        }
        await tx.CommitAsync(ct);
        return new KitImportadoDto(novos.Count == 0 ? [] : await Produtos(c, null, loja, novos.ToArray()), jaExistiam);
    }

    /// <summary>Cria o insumo a partir do item global. Null = a loja já tem esse item (importar de novo não duplica).</summary>
    private async Task<Guid?> ImportarInterno(NpgsqlConnection c, NpgsqlTransaction tx, Guid loja, int usuario, GlobalRow g,
        IReadOnlyList<string> finalidades)
    {
        var existente = await c.ExecuteScalarAsync<Guid?>($@"
SELECT coalesce(
    (SELECT cl.insumo_id FROM gestao_catalogo_loja cl JOIN insumo i ON i.estabelecimento_id = cl.estabelecimento_id AND i.id = cl.insumo_id AND i.ativo
      WHERE cl.estabelecimento_id = @loja AND cl.global_id = @Id),
    {InsumoDoGlobal(true, "@Id")})", new { loja, g.Id }, tx);
        if (existente is not null) return null;
        var agora = _relogio.GetUtcNow();
        var id = Guid.NewGuid();
        await c.ExecuteAsync(@"
INSERT INTO insumo (id, estabelecimento_id, nome, unidade, categoria, criado_por, criado_em, atualizado_em)
VALUES (@id, @loja, @Nome, @Unidade, @Categoria, @usuario, @agora, @agora)", new { id, loja, g.Nome, g.Unidade, g.Categoria, usuario, agora }, tx);
        try
        {
            await c.ExecuteAsync(@"
INSERT INTO gestao_catalogo_loja (estabelecimento_id, insumo_id, origem, global_id, finalidades, criado_por, criado_em, atualizado_em)
VALUES (@loja, @id, 'global', @globalId, @finalidades, @usuario, @agora, @agora)",
                new { loja, id, globalId = g.Id, finalidades = Finalidades(finalidades), usuario, agora }, tx);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new GestaoException("DUPLICADO", $"{g.Nome} acabou de entrar na sua loja.", 409);
        }
        await Registrar(c, tx, loja, usuario, id, null, null, "importado", "Entrou no catálogo da sua loja; o estoque não mudou", null, null, agora);
        return id;
    }

    // ------------------------------------------------------------------ criar e finalidades

    public async Task<ProdutoLojaDto> Criar(Guid loja, int usuario, CriarProdutoRequest req, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await ExigirInsumos(c, tx);
        var id = await CriarInterno(c, tx, loja, usuario, req);
        await tx.CommitAsync(ct);
        return (await Produtos(c, null, loja, [id])).Single();
    }

    private async Task<Guid> CriarInterno(NpgsqlConnection c, NpgsqlTransaction tx, Guid loja, int usuario, CriarProdutoRequest req)
    {
        var nome = (req.Nome ?? "").Trim();
        if (nome.Length < 3) throw Invalido("Escreva o nome do produto (mínimo 3 letras).");
        var unidade = CatalogoRegras.UnidadeBaseDe(req.Unidade) ?? throw Invalido("A unidade deve ser un, kg, g, L ou ml.");
        var nova = string.IsNullOrWhiteSpace(req.NovaCategoria) ? null : req.NovaCategoria.Trim();
        var categoria = nova ?? (string.IsNullOrWhiteSpace(req.Categoria) ? null : req.Categoria.Trim())
            ?? throw Invalido("Escolha uma categoria ou sugira uma nova.");
        var finalidades = CatalogoRegras.NormalizarFinalidades(req.Finalidades);
        if (finalidades.Count == 0) throw Invalido("Diga para que a sua loja usa (ao menos uma finalidade).");
        var chave = DocumentoRegras.NomeChave(nome);
        var nomes = await c.QueryAsync<string>("SELECT nome FROM insumo WHERE estabelecimento_id = @loja AND ativo", new { loja }, tx);
        if (nomes.Any(n => DocumentoRegras.NomeChave(n) == chave)) throw new GestaoException("DUPLICADO", "Este produto já está na sua loja.", 409);

        var agora = _relogio.GetUtcNow();
        var id = Guid.NewGuid();
        await c.ExecuteAsync(@"
INSERT INTO insumo (id, estabelecimento_id, nome, unidade, categoria, criado_por, criado_em, atualizado_em)
VALUES (@id, @loja, @nome, @unidade, @categoria, @usuario, @agora, @agora)",
            new { id, loja, nome = Cortar(nome, 120), unidade, categoria = Cortar(categoria, 60), usuario, agora }, tx);
        // Produto novo vira candidato na curadoria (ou se liga ao validado de mesmo nome e unidade). Na loja vale na hora.
        var globalId = await GlobalPara(c, tx, loja, usuario, nome, unidade, categoria, agora);
        await c.ExecuteAsync(@"
INSERT INTO gestao_catalogo_loja (estabelecimento_id, insumo_id, origem, global_id, finalidades, criado_por, criado_em, atualizado_em)
VALUES (@loja, @id, 'novo', @globalId, @finalidades, @usuario, @agora, @agora)
ON CONFLICT DO NOTHING", new { loja, id, globalId, finalidades = Finalidades(finalidades), usuario, agora }, tx);
        await Registrar(c, tx, loja, usuario, id, null, null, "criado",
            nova is null ? "Criado pela sua loja" : $"Categoria \"{nova}\" sugerida à curadoria", null, null, agora);
        return id;
    }

    public async Task<ProdutoLojaDto> Finalidades(Guid loja, int usuario, Guid insumoId, IReadOnlyList<string>? finalidades, CancellationToken ct)
    {
        var novas = CatalogoRegras.NormalizarFinalidades(finalidades);
        if (novas.Count == 0) throw Invalido("Deixe ao menos uma finalidade.");
        await using var c = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await ExigirInsumos(c, tx);
        if (!await c.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM insumo WHERE estabelecimento_id = @loja AND id = @insumoId AND ativo)",
                new { loja, insumoId }, tx))
            throw new GestaoException("NAO_ENCONTRADO", "Produto não encontrado nesta loja.", 404);
        var antes = await c.ExecuteScalarAsync<string[]?>(@"
SELECT finalidades FROM gestao_catalogo_loja WHERE estabelecimento_id = @loja AND insumo_id = @insumoId FOR UPDATE", new { loja, insumoId }, tx);
        if (antes is not null && antes.SequenceEqual(novas)) { await tx.CommitAsync(ct); return (await Produtos(c, null, loja, [insumoId])).Single(); }
        var agora = _relogio.GetUtcNow();
        // A finalidade é da loja: muda aqui, e o antes/depois fica no histórico (o insumo e a categoria não mudam).
        await c.ExecuteAsync(@"
INSERT INTO gestao_catalogo_loja (estabelecimento_id, insumo_id, origem, global_id, finalidades, criado_por, criado_em, atualizado_em)
VALUES (@loja, @insumoId, 'loja', NULL, @finalidades, @usuario, @agora, @agora)
ON CONFLICT (estabelecimento_id, insumo_id) DO UPDATE SET finalidades = excluded.finalidades, atualizado_em = excluded.atualizado_em",
            new { loja, insumoId, finalidades = Finalidades(novas), usuario, agora }, tx);
        var de = antes is { Length: > 0 } ? string.Join(", ", antes) : "sem finalidade";
        await Registrar(c, tx, loja, usuario, insumoId, null, null, "finalidade", "Só na sua loja", de, string.Join(", ", novas), agora);
        await tx.CommitAsync(ct);
        return (await Produtos(c, null, loja, [insumoId])).Single();
    }

    // ------------------------------------------------------------------ de-para de compras

    public async Task<IReadOnlyList<DeParaCompraDto>> DeParaCompras(Guid loja, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        return await DePara(c, loja);
    }

    private async Task<IReadOnlyList<DeParaCompraDto>> DePara(NpgsqlConnection c, Guid loja)
    {
        var temInsumos = await TemInsumos(c, null);
        var nomeInsumo = temInsumos ? "(SELECT i.nome FROM insumo i WHERE i.estabelecimento_id = v.estabelecimento_id AND i.id = v.insumo_id)" : "NULL";
        var vinculosLidos = (await c.QueryAsync<VinculoRow>($@"
SELECT v.id AS Id, v.fornecedor_cnpj AS FornecedorCnpj, v.nome_chave AS NomeChave, v.descricao AS Descricao,
       v.unidade_cupom AS UnidadeCupom, v.insumo_id AS InsumoId, {nomeInsumo} AS InsumoNome,
       g.id AS GlobalId, g.status AS StatusGlobal, cl.origem AS OrigemLoja, v.unidade_base AS UnidadeBase, v.fator AS Fator,
       v.confirmado_em AS ConfirmadoEm, (SELECT u.nome::text FROM usuario u WHERE u.id = v.confirmado_por) AS ConfirmadoPorNome
  FROM gestao_vinculos v
  LEFT JOIN gestao_catalogo_sugestoes s ON s.vinculo_id = v.id
  LEFT JOIN gestao_catalogo_global g ON g.id = coalesce(v.global_id, s.global_id)
  LEFT JOIN gestao_catalogo_loja cl ON cl.estabelecimento_id = v.estabelecimento_id AND cl.insumo_id = v.insumo_id
 WHERE v.estabelecimento_id = @loja AND v.estado = 'confirmado'
 ORDER BY v.confirmado_em DESC LIMIT 1000", new { loja })).ToList();

        // Cada linha de documento que virou compra (não estornada) nos últimos meses é um uso do nome do cupom.
        var desde = _relogio.GetUtcNow().AddMonths(-MesesDeUso);
        var linhas = (await c.QueryAsync<(Guid DocumentoId, int Linha, DateTime Quando, string? Cnpj, string? Fornecedor, string Descricao)>(@"
SELECT d.id, i.linha, coalesce(d.data_emissao, d.criado_em), d.fornecedor_cnpj, d.fornecedor_nome, i.descricao
  FROM gestao_documentos d
 CROSS JOIN LATERAL jsonb_to_recordset(d.leitura -> 'itens') AS i(linha int, descricao text)
 WHERE d.estabelecimento_id = @loja AND coalesce(d.data_emissao, d.criado_em) >= @desde AND i.descricao IS NOT NULL
   AND EXISTS (SELECT 1 FROM gestao_acontecimentos a
                WHERE a.estabelecimento_id = d.estabelecimento_id AND a.tipo = 'compra' AND a.origem_tipo = 'compra'
                  AND a.origem_id = d.id::text AND a.corrige_id IS NULL
                  AND NOT EXISTS (SELECT 1 FROM gestao_acontecimentos e
                                   WHERE e.estabelecimento_id = a.estabelecimento_id AND e.corrige_id = a.id AND e.correcao = 'estorno'))",
            new { loja, desde })).Select(l => new CatalogoRegras.LinhaLida(l.DocumentoId, l.Linha, Utc(l.Quando), l.Cnpj ?? "", l.Fornecedor,
            l.Descricao, Chave(l.Descricao))).ToList();

        var naoControlar = (await c.QueryAsync<(string Cnpj, string NomeChave, string Descricao, DateTime Em, string? Por)>(@"
SELECT n.fornecedor_cnpj, n.nome_chave, n.descricao, n.marcado_em, (SELECT u.nome::text FROM usuario u WHERE u.id = n.marcado_por)
  FROM gestao_catalogo_nao_controlar n WHERE n.estabelecimento_id = @loja AND n.estado = 'ativo'", new { loja }))
            .Select(n => new CatalogoRegras.NaoControlarLido(n.Cnpj, n.NomeChave, n.Descricao, Utc(n.Em), n.Por)).ToList();

        var chaves = vinculosLidos.Select(v => v.NomeChave).Concat(linhas.Select(l => l.NomeChave)).Concat(naoControlar.Select(n => n.NomeChave))
            .Distinct().ToArray();
        var eventos = (await LerEventos(c, null, loja, null, chaves)).Where(e => e.NomeChave is not null)
            .GroupBy(e => e.NomeChave!).ToDictionary(g => g.Key, g => (IReadOnlyList<EventoCatalogoDto>)g.Select(Evento).ToList());

        return CatalogoRegras.MontarDePara(vinculosLidos.Select(v => new CatalogoRegras.VinculoLido(v.Id, v.FornecedorCnpj, v.NomeChave,
            v.Descricao, v.UnidadeCupom, v.InsumoId, v.InsumoNome, v.GlobalId, v.StatusGlobal, v.OrigemLoja, v.UnidadeBase, v.Fator,
            Utc(v.ConfirmadoEm), v.ConfirmadoPorNome)).ToList(), linhas, naoControlar, eventos);
    }

    public async Task<DeParaCompraDto> NaoControlar(Guid loja, int usuario, NaoControlarRequest req, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        var agora = _relogio.GetUtcNow();
        string chave, descricao, cnpj;
        await using (var tx = await c.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct))
        {
            if (req.VinculoId is { } vinculoId)
            {
                var v = await c.QuerySingleOrDefaultAsync<(string NomeChave, string Descricao, string Cnpj)>(@"
SELECT nome_chave, descricao, fornecedor_cnpj FROM gestao_vinculos WHERE estabelecimento_id = @loja AND id = @vinculoId",
                    new { loja, vinculoId }, tx);
                if (v.NomeChave is null) throw new GestaoException("NAO_ENCONTRADO", "Vínculo não encontrado.", 404);
                (chave, descricao, cnpj) = v;
                // O vínculo deixa de valer (nada se apaga: fica 'desfeito' com o motivo).
                await c.ExecuteAsync(@"
UPDATE gestao_vinculos SET estado = 'desfeito', desfeito_por = @usuario, desfeito_em = @agora,
       motivo_desfeito = 'Marcado como não controlar no Catálogo.'
 WHERE estabelecimento_id = @loja AND id = @vinculoId AND estado = 'confirmado'", new { loja, vinculoId, usuario, agora }, tx);
            }
            else
            {
                descricao = (req.Descricao ?? "").Trim();
                chave = Chave(descricao);
                if (chave.Length == 0) throw Invalido("Diga qual nome do cupom não controlar.");
                cnpj = DocumentoRegras.SoDigitos(req.FornecedorCnpj) ?? "";
            }
            if (req.Controlar)
                await c.ExecuteAsync(@"
UPDATE gestao_catalogo_nao_controlar SET estado = 'desfeito', desfeito_por = @usuario, desfeito_em = @agora
 WHERE estabelecimento_id = @loja AND fornecedor_cnpj = @cnpj AND nome_chave = @chave AND estado = 'ativo'",
                    new { loja, cnpj, chave, usuario, agora }, tx);
            else
                await c.ExecuteAsync(@"
INSERT INTO gestao_catalogo_nao_controlar (id, estabelecimento_id, fornecedor_cnpj, nome_chave, descricao, marcado_por, marcado_em)
VALUES (@id, @loja, @cnpj, @chave, @descricao, @usuario, @agora)
ON CONFLICT (estabelecimento_id, fornecedor_cnpj, nome_chave) WHERE estado = 'ativo' DO NOTHING",
                    new { id = Guid.NewGuid(), loja, cnpj, chave, descricao = Cortar(descricao, 200), usuario, agora }, tx);
            await Registrar(c, tx, loja, usuario, null, req.VinculoId, chave, req.Controlar ? "controlar" : "nao_controlar",
                req.Controlar ? "Volta a ser controlado no estoque" : "A compra segue no financeiro; nada entra no estoque", null, null, agora);
            await tx.CommitAsync(ct);
        }
        var lista = await DePara(c, loja);
        return lista.FirstOrDefault(d => d.NomeChave == chave && d.VinculoId is null && (d.FornecedorCnpj == cnpj || cnpj == ""))
            ?? lista.FirstOrDefault(d => d.NomeChave == chave)
            ?? new DeParaCompraDto(CatalogoRegras.IdDeNome(cnpj, chave), null, descricao, chave, cnpj, null, null, null,
                OrigemCatalogo.Desconhecido, CuradoriaLoja.NaoEnviado, null, new Dictionary<string, int>(), 0, null, null, null, null,
                !req.Controlar, []);
    }

    public async Task<DeParaCompraDto> Sugerir(Guid loja, int usuario, Guid vinculoId, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        await using (var tx = await c.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct))
        {
            var v = await c.QuerySingleOrDefaultAsync<(Guid Id, string NomeChave, Guid InsumoId, Guid? GlobalId, string Unidade, string Descricao)>(@"
SELECT id, nome_chave, insumo_id, global_id, unidade_base, descricao FROM gestao_vinculos
 WHERE estabelecimento_id = @loja AND id = @vinculoId AND estado = 'confirmado'", new { loja, vinculoId }, tx);
            if (v.Id == Guid.Empty) throw new GestaoException("NAO_ENCONTRADO", "Vínculo não encontrado (ou já desfeito).", 404);
            var status = v.GlobalId is { } gid
                ? await c.ExecuteScalarAsync<string?>("SELECT status FROM gestao_catalogo_global WHERE id = @gid", new { gid }, tx) : null;
            if (status is "validado" or "mesclado") throw new GestaoException("REGRA", "Este vínculo já vale para todas as lojas.", 409);
            if (status == "rejeitado") throw new GestaoException("REGRA", "A curadoria já recusou este item: troque o vínculo antes de sugerir de novo.", 409);
            var agora = _relogio.GetUtcNow();
            var globalId = v.GlobalId;
            if (globalId is null)
            {
                var insumo = await TemInsumos(c, tx)
                    ? await c.QuerySingleOrDefaultAsync<(string Nome, string? Categoria)>(
                        "SELECT nome, categoria FROM insumo WHERE estabelecimento_id = @loja AND id = @InsumoId", new { loja, v.InsumoId }, tx)
                    : default;
                globalId = await GlobalPara(c, tx, loja, usuario, insumo.Nome ?? v.Descricao, v.Unidade, insumo.Categoria, agora);
            }
            var inseridas = await c.ExecuteAsync(@"
INSERT INTO gestao_catalogo_sugestoes (id, estabelecimento_id, vinculo_id, global_id, sugerido_por, sugerido_em)
VALUES (@id, @loja, @vinculoId, @globalId, @usuario, @agora) ON CONFLICT (vinculo_id) DO NOTHING",
                new { id = Guid.NewGuid(), loja, vinculoId, globalId, usuario, agora }, tx);
            if (inseridas > 0)
                await Registrar(c, tx, loja, usuario, v.InsumoId, vinculoId, v.NomeChave, "sugerido", "Sugerido à curadoria Zippy · na sua loja nada muda",
                    null, null, agora);
            await tx.CommitAsync(ct);
        }
        return (await DePara(c, loja)).First(d => d.VinculoId == vinculoId);
    }

    public async Task<DeParaCompraDto> Vincular(Guid loja, int usuario, VincularDeParaRequest req, CancellationToken ct)
    {
        string unidadeAlvo;
        await using (var c = await dataSource.OpenConnectionAsync(ct))
        {
            unidadeAlvo = req.InsumoId is { } i
                ? await c.ExecuteScalarAsync<string?>("SELECT unidade FROM insumo WHERE estabelecimento_id = @loja AND id = @i", new { loja, i })
                    ?? throw Invalido("Insumo não encontrado nesta loja.")
                : req.GlobalId is { } g
                    ? await c.ExecuteScalarAsync<string?>("SELECT unidade FROM gestao_catalogo_global WHERE id = @g", new { g })
                        ?? throw Invalido("Item do catálogo não encontrado.")
                    : CatalogoRegras.UnidadeBaseDe(req.Novo?.Unidade) ?? throw Invalido("A unidade deve ser un, kg, g, L ou ml.");
        }
        var fator = req.Fator is > 0 ? req.Fator.Value
            : CatalogoRegras.FatorDaConversao(req.Conversao, unidadeAlvo)
              ?? (string.IsNullOrWhiteSpace(req.Conversao) ? 1m
                  : throw Invalido($"Não entendi a conversão \"{req.Conversao}\". Escreva como \"1 PC = 2 kg\" (a unidade do estoque é {unidadeAlvo})."));
        var novo = req.Novo is { } n ? new NovoInsumoRequest((n.Nome ?? "").Trim(), unidadeAlvo, n.NovaCategoria ?? n.Categoria) : null;
        var confirmado = await vinculos.Confirmar(loja, usuario, req.DocumentoId, req.Linha,
            new ConfirmarVinculoRequest(req.InsumoId, req.GlobalId, novo, fator), ct);

        // Produto que entrou na loja pelo vínculo: registra a origem e as finalidades (o vínculo já vale na hora).
        if (confirmado.InsumoCriado)
        {
            await using var c = await dataSource.OpenConnectionAsync(ct);
            await using var tx = await c.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            var agora = _relogio.GetUtcNow();
            var origem = req.GlobalId is not null ? OrigemCatalogo.Global : OrigemCatalogo.Novo;
            await c.ExecuteAsync(@"
INSERT INTO gestao_catalogo_loja (estabelecimento_id, insumo_id, origem, global_id, finalidades, criado_por, criado_em, atualizado_em)
VALUES (@loja, @insumoId, @origem, @globalId, @finalidades, @usuario, @agora, @agora) ON CONFLICT DO NOTHING", new
            {
                loja, insumoId = confirmado.Vinculo.InsumoId, origem, globalId = confirmado.Vinculo.GlobalId,
                finalidades = Finalidades(CatalogoRegras.NormalizarFinalidades(req.Novo?.Finalidades)), usuario, agora,
            }, tx);
            await Registrar(c, tx, loja, usuario, confirmado.Vinculo.InsumoId, confirmado.Vinculo.Id, confirmado.Vinculo.NomeChave,
                origem == OrigemCatalogo.Global ? "importado" : "criado", $"Entrou na loja por vínculo numa compra: {confirmado.Vinculo.Descricao}",
                null, null, agora);
            await tx.CommitAsync(ct);
        }
        await using var leitura = await dataSource.OpenConnectionAsync(ct);
        return (await DePara(leitura, loja)).First(d => d.VinculoId == confirmado.Vinculo.Id);
    }

    // ------------------------------------------------------------------ busca para a conferência

    public async Task<BuscaCatalogoDto> Buscar(Guid loja, string? texto, CancellationToken ct)
    {
        var termo = DocumentoRegras.NomeChave(texto);
        if (termo.Length < 2) return new BuscaCatalogoDto([], []);
        await using var c = await dataSource.OpenConnectionAsync(ct);
        var temInsumos = await TemInsumos(c, null);
        // Pré-filtro por palavra no banco; a ordem final é pela pontuação do nome.
        var palavras = termo.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(p => p.Length >= 2).Select(p => "%" + p[..Math.Min(p.Length, 4)] + "%").ToArray();
        var insumos = temInsumos
            ? (await c.QueryAsync<(Guid Id, string Nome, string Unidade, string? Categoria, Guid? GlobalId)>(@"
SELECT i.id, i.nome, i.unidade, i.categoria,
       coalesce((SELECT cl.global_id FROM gestao_catalogo_loja cl WHERE cl.estabelecimento_id = i.estabelecimento_id AND cl.insumo_id = i.id),
                (SELECT v.global_id FROM gestao_vinculos v WHERE v.estabelecimento_id = i.estabelecimento_id AND v.insumo_id = i.id
                    AND v.global_id IS NOT NULL ORDER BY v.confirmado_em DESC LIMIT 1))
  FROM insumo i WHERE i.estabelecimento_id = @loja AND i.ativo", new { loja })).ToList()
            : [];
        var globais = (await c.QueryAsync<GlobalRow>($@"
SELECT g.id AS Id, g.nome AS Nome, g.categoria AS Categoria, g.unidade AS Unidade, {InsumoDoGlobal(temInsumos)} AS InsumoId
  FROM gestao_catalogo_global g WHERE g.status = 'validado' AND g.nome_chave LIKE ANY(@palavras) LIMIT 300", new { loja, palavras })).ToList();

        var daLoja = insumos.Select(i => new BuscaInsumoDto(i.Id, i.Nome, i.Unidade, i.Categoria, i.GlobalId, CatalogoRegras.Pontuar(termo, i.Nome)))
            .Where(i => i.Confianca >= VinculoRegras.SemelhancaMinima).OrderByDescending(i => i.Confianca).ThenBy(i => i.Nome).Take(LimiteBusca).ToList();
        var doGlobal = globais.Select(g => new BuscaGlobalDto(g.Id, g.Nome, g.Unidade, g.Categoria, g.InsumoId, CatalogoRegras.Pontuar(termo, g.Nome)))
            .Where(g => g.Confianca >= VinculoRegras.SemelhancaMinima).OrderByDescending(g => g.Confianca).ThenBy(g => g.Nome).Take(LimiteBusca).ToList();
        return new BuscaCatalogoDto(daLoja, doGlobal);
    }

    // ------------------------------------------------------------------ apoio

    /// <summary>Insumo ativo da loja que já veio deste item global (pelo catálogo da loja ou por vínculo).</summary>
    private static string InsumoDoGlobal(bool temInsumos, string global = "g.id") => !temInsumos ? "NULL::uuid" : $@"coalesce(
        (SELECT cl.insumo_id FROM gestao_catalogo_loja cl JOIN insumo ii ON ii.estabelecimento_id = cl.estabelecimento_id AND ii.id = cl.insumo_id AND ii.ativo
          WHERE cl.estabelecimento_id = @loja AND cl.global_id = {global} LIMIT 1),
        (SELECT v.insumo_id FROM gestao_vinculos v JOIN insumo ii ON ii.estabelecimento_id = v.estabelecimento_id AND ii.id = v.insumo_id AND ii.ativo
          WHERE v.estabelecimento_id = @loja AND v.global_id = {global} ORDER BY v.confirmado_em DESC LIMIT 1))";

    /// <summary>Item global para um insumo novo: o validado de mesmo nome e unidade, ou um candidato novo para a curadoria.</summary>
    private static async Task<Guid> GlobalPara(NpgsqlConnection c, NpgsqlTransaction tx, Guid loja, int usuario, string nome, string unidade,
        string? categoria, DateTimeOffset agora)
    {
        var nomeChave = Chave(nome);
        if (await c.ExecuteScalarAsync<Guid?>(@"
SELECT id FROM gestao_catalogo_global WHERE status IN ('validado', 'candidato') AND nome_chave = @nomeChave AND unidade = @unidade
 ORDER BY (status = 'validado') DESC, criado_em LIMIT 1", new { nomeChave, unidade }, tx) is { } existente)
            return existente;
        var id = Guid.NewGuid();
        await c.ExecuteAsync(@"
INSERT INTO gestao_catalogo_global (id, nome, nome_chave, unidade, categoria, criado_por_loja, criado_por, criado_em)
VALUES (@id, @nome, @nomeChave, @unidade, @categoria, @loja, @usuario, @agora)",
            new { id, nome = Cortar(nome, 120), nomeChave, unidade, categoria = Cortar(categoria, 60), loja, usuario, agora }, tx);
        return id;
    }

    private static async Task<List<EventoRow>> LerEventos(NpgsqlConnection c, NpgsqlTransaction? tx, Guid loja, Guid[]? insumos, string[]? chaves) =>
        (await c.QueryAsync<EventoRow>(@"
SELECT e.id AS Id, e.insumo_id AS InsumoId, e.nome_chave AS NomeChave, e.tipo AS Tipo, e.detalhe AS Detalhe, e.antes AS Antes,
       e.depois AS Depois, (SELECT u.nome::text FROM usuario u WHERE u.id = e.por) AS PorNome, e.em AS Em
  FROM gestao_catalogo_eventos e
 WHERE e.estabelecimento_id = @loja AND ((@insumos::uuid[] IS NOT NULL AND e.insumo_id = ANY(@insumos))
                                         OR (@chaves::text[] IS NOT NULL AND e.nome_chave = ANY(@chaves)))
 ORDER BY e.em DESC LIMIT 2000", new { loja, insumos, chaves }, tx)).ToList();

    private static EventoCatalogoDto Evento(EventoRow e) => new(e.Id, Utc(e.Em), e.Tipo switch
    {
        "importado" => "Importado do catálogo Zippy",
        "criado" => "Criado pela sua loja",
        "finalidade" => "Finalidade alterada",
        "nao_controlar" => "Marcado como \"não controlar\"",
        "controlar" => "Voltou a ser controlado",
        "sugerido" => "Sugerido à curadoria Zippy",
        _ => e.Tipo,
    }, e.Antes is not null || e.Depois is not null ? $"{e.Antes} → {e.Depois}{(e.Detalhe is null ? "" : $" ({e.Detalhe})")}" : e.Detalhe, e.PorNome);

    private static Task Registrar(NpgsqlConnection c, NpgsqlTransaction tx, Guid loja, int usuario, Guid? insumoId, Guid? vinculoId,
        string? nomeChave, string tipo, string? detalhe, string? antes, string? depois, DateTimeOffset agora) =>
        c.ExecuteAsync(@"
INSERT INTO gestao_catalogo_eventos (id, estabelecimento_id, insumo_id, vinculo_id, nome_chave, tipo, detalhe, antes, depois, por, em)
VALUES (@id, @loja, @insumoId, @vinculoId, @nomeChave, @tipo, @detalhe, @antes, @depois, @usuario, @agora)", new
        {
            id = Guid.NewGuid(), loja, insumoId, vinculoId, nomeChave, tipo, detalhe = Cortar(detalhe, 300), antes = Cortar(antes, 300),
            depois = Cortar(depois, 300), usuario, agora,
        }, tx);

    private static async Task ExigirInsumos(NpgsqlConnection c, NpgsqlTransaction tx)
    {
        if (!await TemInsumos(c, tx)) throw Invalido("Os insumos da loja ainda não existem: ative a Gestão e Inteligência.");
    }

    private static async Task<bool> TemInsumos(NpgsqlConnection c, NpgsqlTransaction? tx) =>
        await c.ExecuteScalarAsync<bool>("SELECT to_regclass('insumo') IS NOT NULL", transaction: tx);

    private static string[] Finalidades(IReadOnlyList<string> lista) => lista.ToArray();

    private static DateTimeOffset Utc(DateTime d) => new(DateTime.SpecifyKind(d, DateTimeKind.Utc));

    private static string Chave(string texto) => Cortar(DocumentoRegras.NomeChave(texto), 160)!;

    private static string? Cortar(string? s, int max) => s is null || s.Length <= max ? s : s[..max];

    private static GestaoException Invalido(string m) => new("CAMPO_INVALIDO", m, 422);
}
