using System.Text.Json;
using APIBack.Acontecimentos;
using APIBack.GestaoLogica.Documentos;
using Dapper;
using Npgsql;

namespace APIBack.GestaoLogica.Vinculos;

/// <summary>
/// Curadoria Zippy com fila e decisões (rotas /api/curadoria, só super admin). Os casos são gerados na hora dos sinais das
/// lojas; a decisão é um registro imutável (gestao_curadoria_decisoes) e o estado do caso sai da última decisão.
/// Efeitos ficam no catálogo global: aprovar valida (o vínculo da loja passa a valer para todas), corrigir aponta ou mescla
/// no produto escolhido, rejeitar recusa o candidato. O insumo que cada loja usa nunca muda (regra 6).
/// Reabrir grava uma decisão nova e desfaz no catálogo o que a decisão reaberta fez.
/// </summary>
public interface ICuradoriaDecisoesService
{
    Task<IReadOnlyList<CasoCuradoria>> Casos(CancellationToken ct);
    Task<IReadOnlyList<RegistroDecisao>> Decisoes(CancellationToken ct);
    Task<RegistroDecisao> Decidir(int usuario, string responsavel, DecidirCuradoriaRequest req, CancellationToken ct);
    Task<ResultadoDoLote> DecidirEmLote(int usuario, string responsavel, DecidirEmLoteRequest req, CancellationToken ct);
    Task<RegistroDecisao> Reabrir(int usuario, string responsavel, Guid registroId, ReabrirDecisaoRequest req, CancellationToken ct);
    Task<IReadOnlyList<ProdutoGlobalCuradoria>> ProdutosGlobais(string? busca, CancellationToken ct);
}

public sealed class CuradoriaDecisoesService(NpgsqlDataSource dataSource, TimeProvider? relogio = null) : ICuradoriaDecisoesService
{
    private readonly TimeProvider _relogio = relogio ?? TimeProvider.System;
    private const int MaximoItensGlobais = 3000;

    public async Task<IReadOnlyList<CasoCuradoria>> Casos(CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        return await GerarCasos(c, null);
    }

    public async Task<IReadOnlyList<RegistroDecisao>> Decisoes(CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        // Correções do catálogo global têm histórico próprio (/api/curadoria/global/historico): não são casos da fila.
        return (await LerDecisoes(c, null, null)).Where(l => l.CasoTipo != CuradoriaGlobalRegras.TipoGlobal).Select(l => l.Registro()).ToList();
    }

    public async Task<RegistroDecisao> Decidir(int usuario, string responsavel, DecidirCuradoriaRequest req, CancellationToken ct)
    {
        var casoId = req.CasoId?.Trim();
        if (string.IsNullOrEmpty(casoId)) throw Invalido("Informe o caso.");
        var nome = Responsavel(responsavel, req.Responsavel);
        await using var c = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await Travar(c, tx, new[] { casoId });
        var caso = (await GerarCasos(c, tx)).FirstOrDefault(x => x.Id == casoId)
            ?? throw new GestaoException("NAO_ENCONTRADO", "Caso não encontrado.", 404);
        var produto = await ProdutoEscolhido(c, tx, req.ProdutoGlobal);
        var criterios = await CuradoriaGlobalService.Vigentes(c, tx);
        var registro = await DecidirNoCaso(c, tx, usuario, nome, caso, req.Decisao, req.Motivo, produto, null, _relogio.GetUtcNow(), criterios);
        await tx.CommitAsync(ct);
        return registro;
    }

    public async Task<ResultadoDoLote> DecidirEmLote(int usuario, string responsavel, DecidirEmLoteRequest req, CancellationToken ct)
    {
        if (req.Decisao is not (DecisaoCuradoria.Aprovar or DecisaoCuradoria.Observar))
            throw Invalido("Em lote só dá para aprovar ou manter em observação.");
        var ids = (req.CasoIds ?? Array.Empty<string>()).Select(i => i?.Trim()).OfType<string>().Where(i => i.Length > 0).Distinct().ToList();
        if (ids.Count == 0) throw Invalido("Escolha os casos do lote.");
        var nome = Responsavel(responsavel, req.Responsavel);
        await using var c = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await Travar(c, tx, ids);
        var casos = (await GerarCasos(c, tx)).ToDictionary(x => x.Id);
        var decisoes = await LerDecisoes(c, tx, ids);
        var criterios = await CuradoriaGlobalService.Vigentes(c, tx);
        var incluidos = new List<CasoCuradoria>();
        var excluidos = new List<ExcluidoDoLote>();
        foreach (var id in ids)
        {
            if (!casos.TryGetValue(id, out var caso)) continue;
            var estado = CuradoriaDecisoesRegras.EstadoDoCaso(id, decisoes.Select(d => d.Registro()));
            if (CuradoriaDecisoesRegras.MotivoForaDoLote(caso, req.Decisao, estado, criterios) is { } motivo) excluidos.Add(new(caso, motivo));
            else incluidos.Add(caso);
        }
        if (incluidos.Count == 0) throw new GestaoException("SEM_CASOS", "Nenhum dos casos escolhidos pode entrar neste lote.", 422);
        var lote = Guid.NewGuid();
        var agora = _relogio.GetUtcNow();
        var registros = new List<RegistroDecisao>();
        foreach (var caso in incluidos)
            registros.Add(await DecidirNoCaso(c, tx, usuario, nome, caso, req.Decisao, req.Motivo, null, lote, agora, criterios));
        await tx.CommitAsync(ct);
        return new ResultadoDoLote(registros, new PlanoDoLote(incluidos, excluidos));
    }

    public async Task<RegistroDecisao> Reabrir(int usuario, string responsavel, Guid registroId, ReabrirDecisaoRequest req, CancellationToken ct)
    {
        var nome = Responsavel(responsavel, req.Responsavel);
        await using var c = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        var casoId = await c.ExecuteScalarAsync<string?>("SELECT caso_id FROM gestao_curadoria_decisoes WHERE id = @registroId",
            new { registroId }, tx) ?? throw new GestaoException("NAO_ENCONTRADO", "Decisão não encontrada.", 404);
        await Travar(c, tx, new[] { casoId });
        var doCaso = await LerDecisoes(c, tx, new[] { casoId });
        var alvo = doCaso.Single(d => d.Id == registroId);
        if (alvo.CasoTipo == CuradoriaGlobalRegras.TipoGlobal)
            throw new GestaoException("CAMPO_INVALIDO", "Correção do catálogo global não se reabre: corrija de novo.", 422);
        CuradoriaDecisoesRegras.ValidarReabertura(alvo.Registro(), doCaso.Select(d => d.Registro()), req.Motivo);
        await Desfazer(c, tx, Efeito.Ler(alvo.Efeito));
        var registro = new RegistroDecisao(Guid.NewGuid(), alvo.CasoId, alvo.CasoNome, alvo.CasoTipo, DecisaoCuradoria.Reabrir, nome,
            Cortar(req.Motivo!.Trim(), 500), _relogio.GetUtcNow(), CuradoriaDecisoesRegras.RotuloDaDecisao(alvo.Decisao), "Aberto",
            null, alvo.Id, null, null, null);
        try { await Gravar(c, tx, usuario, registro, new Efeito()); }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new GestaoException("REGISTRO_JA_REABERTO", "Esta decisão já foi reaberta.", 409);
        }
        await tx.CommitAsync(ct);
        return registro;
    }

    public async Task<IReadOnlyList<ProdutoGlobalCuradoria>> ProdutosGlobais(string? busca, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        var itens = await c.QueryAsync<(Guid Id, string Nome, string Unidade, string? Categoria)>(
            "SELECT id, nome, unidade, categoria FROM gestao_catalogo_global WHERE status = 'validado' ORDER BY nome LIMIT @max",
            new { max = MaximoItensGlobais });
        var palavras = DocumentoRegras.NomeChave(busca).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return itens.Where(i => palavras.All(p => DocumentoRegras.NomeChave($"{i.Nome} {i.Categoria}").Contains(p, StringComparison.Ordinal)))
            .Take(30).Select(i => new ProdutoGlobalCuradoria(i.Id, i.Nome, $"{i.Categoria ?? "Sem categoria"} · {i.Unidade}")).ToList();
    }

    // ------------------------------------------------------------ decisão

    private async Task<RegistroDecisao> DecidirNoCaso(NpgsqlConnection c, NpgsqlTransaction tx, int usuario, string responsavel,
        CasoCuradoria caso, string? decisao, string? motivo, (Guid Id, string Nome)? produto, Guid? lote, DateTimeOffset agora,
        CriteriosCuradoria criterios)
    {
        var anteriores = (await LerDecisoes(c, tx, new[] { caso.Id })).Select(d => d.Registro());
        var estado = CuradoriaDecisoesRegras.EstadoDoCaso(caso.Id, anteriores);
        var nova = CuradoriaDecisoesRegras.Montar(caso, estado, decisao, motivo, produto?.Nome, criterios);
        var efeito = await Aplicar(c, tx, usuario, caso, nova, produto?.Id);
        var registro = new RegistroDecisao(Guid.NewGuid(), caso.Id, Cortar(caso.Nome, 300), caso.Tipo, nova.Decisao, responsavel,
            nova.Motivo, agora, nova.Anterior, nova.Novo, lote, null, nova.Excecao ? true : null, nova.ConfirmacoesNoRegistro,
            efeito.ProdutoGlobalId);
        await Gravar(c, tx, usuario, registro, efeito);
        return registro;
    }

    /// <summary>O que a decisão faz no catálogo global. Nunca troca o insumo de uma loja.</summary>
    private static async Task<Efeito> Aplicar(NpgsqlConnection c, NpgsqlTransaction tx, int usuario, CasoCuradoria caso,
        CuradoriaDecisoesRegras.NovaDecisao nova, Guid? produtoId)
    {
        var efeito = new Efeito();
        var d = nova.Decisao;
        if (d == DecisaoCuradoria.Observar || caso.Tipo == TipoCaso.Categoria) return efeito;
        switch (caso.Tipo)
        {
            case TipoCaso.Produto:
            {
                var id = Guid.Parse(caso.Id["prod:".Length..]);
                if (d == DecisaoCuradoria.Aprovar) await MudarStatus(c, tx, usuario, id, "candidato", "validado", null, efeito);
                else if (d == DecisaoCuradoria.Rejeitar) await MudarStatus(c, tx, usuario, id, "candidato", "rejeitado", nova.Motivo, efeito);
                else
                {
                    var alvo = produtoId!.Value;
                    if (alvo == id) throw Invalido("Escolha outro produto: este é o próprio candidato.");
                    await Mesclar(c, tx, usuario, id, alvo, efeito);
                }
                break;
            }
            case TipoCaso.DePara or TipoCaso.Contra:
            {
                if (d == DecisaoCuradoria.Rejeitar) break;
                var alvo = d == DecisaoCuradoria.Corrigir ? produtoId!.Value
                    : CuradoriaDecisoesRegras.OpcaoMaisAceita(caso)?.GlobalId ?? throw Invalido("Caso sem produto para aprovar.");
                // O texto passa a valer para todas as lojas apontando para o alvo; se ele ainda era candidato, é validado junto.
                await MudarStatus(c, tx, usuario, alvo, "candidato", "validado", null, efeito);
                efeito.ProdutoGlobalId = alvo;
                break;
            }
            case TipoCaso.Duplicidade:
            {
                if (d == DecisaoCuradoria.Rejeitar) break; // "não são o mesmo produto": nada muda.
                var partes = caso.Id.Split(':');
                var (antigo, novo) = (Guid.Parse(partes[1]), Guid.Parse(partes[2]));
                var fica = d == DecisaoCuradoria.Corrigir ? produtoId!.Value : antigo;
                if (fica != antigo && fica != novo) throw Invalido("Escolha um dos dois produtos para ficar.");
                await Mesclar(c, tx, usuario, fica == antigo ? novo : antigo, fica, efeito);
                break;
            }
        }
        return efeito;
    }

    private static async Task MudarStatus(NpgsqlConnection c, NpgsqlTransaction tx, int usuario, Guid id, string de, string para,
        string? motivo, Efeito efeito)
    {
        try
        {
            var n = await c.ExecuteAsync(@"
UPDATE gestao_catalogo_global SET status = @para, curado_por = @usuario, curado_em = now(),
       motivo_curadoria = coalesce(@motivo, motivo_curadoria)
 WHERE id = @id AND status = @de", new { id, de, para, usuario, motivo = motivo is null ? null : Cortar(motivo, 300) }, tx);
            if (n == 1) efeito.Catalogo.Add(new MudancaStatus(id, de, para));
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new GestaoException("DUPLICADO", "Já existe um item validado com esse nome e unidade: corrija para ele.", 409);
        }
    }

    /// <summary>
    /// Mescla <paramref name="origem"/> em <paramref name="destino"/>: os vínculos das lojas passam a contar para o destino
    /// (o insumo de cada loja continua o mesmo). Guarda o que moveu para a reabertura desfazer.
    /// </summary>
    private static async Task Mesclar(NpgsqlConnection c, NpgsqlTransaction tx, int usuario, Guid origem, Guid destino, Efeito efeito)
    {
        var itens = (await c.QueryAsync<(Guid Id, string Unidade, string Status)>(
            "SELECT id, unidade, status FROM gestao_catalogo_global WHERE id IN (@origem, @destino) FOR UPDATE", new { origem, destino }, tx))
            .ToDictionary(i => i.Id);
        if (!itens.TryGetValue(destino, out var alvo) || alvo.Status is not ("validado" or "candidato"))
            throw Invalido("Produto global não encontrado.");
        if (!itens.TryGetValue(origem, out var atual) || atual.Status is not ("validado" or "candidato"))
            throw new GestaoException("CASO_JA_DECIDIDO", "Este produto já foi mesclado ou rejeitado.", 409);
        if (alvo.Unidade != atual.Unidade) throw Invalido("Os dois produtos precisam ter a mesma unidade.");
        await MudarStatus(c, tx, usuario, destino, "candidato", "validado", null, efeito);
        await c.ExecuteAsync(@"
UPDATE gestao_catalogo_global SET status = 'mesclado', mesclado_em = @destino, curado_por = @usuario, curado_em = now(),
       motivo_curadoria = 'Mesclado pela curadoria.' WHERE id = @origem", new { origem, destino, usuario }, tx);
        efeito.Catalogo.Add(new MudancaStatus(origem, atual.Status, "mesclado"));
        var vinculos = (await c.QueryAsync<Guid>(
            "UPDATE gestao_vinculos SET global_id = @destino WHERE global_id = @origem RETURNING id", new { origem, destino }, tx)).ToArray();
        var remesclados = (await c.QueryAsync<Guid>(
            "UPDATE gestao_catalogo_global SET mesclado_em = @destino WHERE mesclado_em = @origem AND id <> @origem RETURNING id",
            new { origem, destino }, tx)).ToArray();
        efeito.Mescla = new Mescla(origem, destino, vinculos, remesclados);
        efeito.ProdutoGlobalId = destino;
    }

    /// <summary>Desfaz no catálogo o que uma decisão fez (na ordem inversa). Só mexe no que ainda está como ela deixou.</summary>
    private static async Task Desfazer(NpgsqlConnection c, NpgsqlTransaction tx, Efeito efeito)
    {
        if (efeito.Mescla is { } m)
        {
            await c.ExecuteAsync("UPDATE gestao_vinculos SET global_id = @Origem WHERE id = ANY(@Vinculos) AND global_id = @Destino", m, tx);
            await c.ExecuteAsync("UPDATE gestao_catalogo_global SET mesclado_em = @Origem WHERE id = ANY(@Remesclados) AND mesclado_em = @Destino", m, tx);
        }
        try
        {
            foreach (var s in Enumerable.Reverse(efeito.Catalogo))
                await c.ExecuteAsync(@"
UPDATE gestao_catalogo_global SET status = @De, mesclado_em = CASE WHEN @De = 'mesclado' THEN mesclado_em ELSE NULL END
 WHERE id = @Id AND status = @Para", s, tx);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new GestaoException("DUPLICADO", "Outro item com o mesmo nome foi validado depois: não dá para reabrir esta decisão.", 409);
        }
    }

    // ------------------------------------------------------------ casos

    private static async Task<IReadOnlyList<CasoCuradoria>> GerarCasos(NpgsqlConnection c, NpgsqlTransaction? tx)
    {
        var casos = new List<CasoCuradoria>();

        var sinais = (await c.QueryAsync<SinalLinha>(@"
WITH chaves AS (
    SELECT nome_chave FROM gestao_vinculos WHERE estado = 'confirmado' AND global_id IS NOT NULL
     GROUP BY nome_chave HAVING count(DISTINCT estabelecimento_id) >= @minimo),
     precos AS (SELECT vinculo_id, count(*)::int AS n FROM gestao_vinculo_precos GROUP BY vinculo_id)
SELECT v.nome_chave AS NomeChave, v.descricao AS Descricao, v.estabelecimento_id AS Loja, e.nome_fantasia AS LojaNome,
       e.cidade AS Cidade, g.id AS GlobalId, g.nome AS GlobalNome, g.status AS GlobalStatus, v.confirmado_em AS ConfirmadoEm,
       greatest(1, coalesce(p.n, 0)) AS Confirmacoes
  FROM gestao_vinculos v
  JOIN chaves k ON k.nome_chave = v.nome_chave
  JOIN gestao_catalogo_global g0 ON g0.id = v.global_id
  JOIN gestao_catalogo_global g ON g.id = CASE WHEN g0.status = 'mesclado' THEN g0.mesclado_em ELSE g0.id END
  LEFT JOIN precos p ON p.vinculo_id = v.id
  LEFT JOIN estabelecimentos e ON e.id = v.estabelecimento_id
 WHERE v.estado = 'confirmado' AND g.status IN ('candidato', 'validado')",
            new { minimo = CuradoriaDecisoesRegras.MinimoLojasTexto }, tx)).Select(s => s.Sinal()).ToList();
        var chaves = sinais.Select(s => s.NomeChave).Distinct().ToArray();
        var desfeitos = chaves.Length == 0 ? new Dictionary<string, int>() : (await c.QueryAsync<(string Chave, int N)>(@"
SELECT nome_chave, count(*)::int FROM gestao_vinculos WHERE estado = 'desfeito' AND nome_chave = ANY(@chaves) GROUP BY nome_chave",
            new { chaves }, tx)).ToDictionary(x => x.Chave, x => x.N);
        foreach (var g in sinais.GroupBy(s => s.NomeChave))
            if (CuradoriaDecisoesRegras.CasoDeTexto(g.Key, g.ToList(), desfeitos.GetValueOrDefault(g.Key)) is { } caso) casos.Add(caso);

        var globais = (await c.QueryAsync<GlobalLinha>(@"
WITH precos AS (SELECT vinculo_id, count(*)::int AS n FROM gestao_vinculo_precos GROUP BY vinculo_id)
SELECT g.id AS Id, g.nome AS Nome, g.unidade AS Unidade, g.categoria AS Categoria, g.status AS Status, g.criado_em AS CriadoEm,
       e0.nome_fantasia AS LojaQueCriou,
       count(DISTINCT v.estabelecimento_id)::int AS Lojas,
       coalesce(sum(greatest(1, coalesce(p.n, 0))) FILTER (WHERE v.id IS NOT NULL), 0)::int AS Confirmacoes,
       count(DISTINCT nullif(e.cidade, ''))::int AS Cidades,
       array_remove(array_agg(DISTINCT v.descricao), NULL) AS Variacoes
  FROM gestao_catalogo_global g
  LEFT JOIN gestao_vinculos v ON v.global_id = g.id AND v.estado = 'confirmado'
  LEFT JOIN precos p ON p.vinculo_id = v.id
  LEFT JOIN estabelecimentos e ON e.id = v.estabelecimento_id
  LEFT JOIN estabelecimentos e0 ON e0.id = g.criado_por_loja
 WHERE g.status IN ('candidato', 'validado')
 GROUP BY g.id, e0.nome_fantasia
 ORDER BY g.criado_em LIMIT @max", new { max = MaximoItensGlobais }, tx)).ToList();
        var validados = globais.Where(g => g.Status == "validado").Select(g => g.Item()).ToList();
        foreach (var g in globais.Where(g => g.Status == "candidato"))
            casos.Add(CuradoriaDecisoesRegras.CasoDeProduto(g.Item(), g.Variacoes ?? Array.Empty<string>(), validados, g.LojaQueCriou));
        casos.AddRange(CuradoriaDecisoesRegras.CasosDeDuplicidade(validados));

        // Categoria local: só quando os insumos (Inteligência) existem neste banco.
        if (await c.ExecuteScalarAsync<bool>("SELECT to_regclass('insumo') IS NOT NULL", transaction: tx))
        {
            var usos = (await c.QueryAsync<(string Categoria, Guid Loja, DateTime Em)>(
                "SELECT categoria, estabelecimento_id, criado_em FROM insumo WHERE ativo AND categoria IS NOT NULL AND length(trim(categoria)) > 0",
                transaction: tx)).Select(u => (u.Categoria, u.Loja, Utc(u.Em)));
            casos.AddRange(CuradoriaDecisoesRegras.CasosDeCategoria(usos, validados.Select(v => v.Categoria)));
        }
        return casos.OrderBy(x => x.AbertoEm).ThenBy(x => x.Id, StringComparer.Ordinal).ToList();
    }

    // ------------------------------------------------------------ registros

    private static async Task<IReadOnlyList<DecisaoLinha>> LerDecisoes(NpgsqlConnection c, NpgsqlTransaction? tx, IReadOnlyList<string>? casos) =>
        (await c.QueryAsync<DecisaoLinha>($@"
SELECT id AS Id, caso_id AS CasoId, caso_nome AS CasoNome, caso_tipo AS CasoTipo, decisao AS Decisao, responsavel_nome AS Responsavel,
       motivo AS Motivo, em AS Em, anterior AS Anterior, novo AS Novo, lote AS Lote, corrige_id AS CorrigeId, excecao AS Excecao,
       confirmacoes_no_registro AS ConfirmacoesNoRegistro, produto_global_id AS ProdutoGlobalId, efeito::text AS Efeito
  FROM gestao_curadoria_decisoes {(casos is null ? "" : "WHERE caso_id = ANY(@casos)")}
 ORDER BY em, id LIMIT 10000", new { casos = casos?.ToArray() }, tx)).ToList();

    private static Task Gravar(NpgsqlConnection c, NpgsqlTransaction tx, int usuario, RegistroDecisao r, Efeito efeito) =>
        c.ExecuteAsync(@"
INSERT INTO gestao_curadoria_decisoes (id, caso_id, caso_nome, caso_tipo, decisao, responsavel_id, responsavel_nome, motivo, em,
       anterior, novo, lote, corrige_id, excecao, confirmacoes_no_registro, produto_global_id, efeito)
VALUES (@Id, @CasoId, @CasoNome, @CasoTipo, @Decisao, @usuario, @Responsavel, @Motivo, @Em, @Anterior, @Novo, @Lote, @CorrigeId,
        coalesce(@Excecao, false), @ConfirmacoesNoRegistro, @ProdutoGlobalId, @efeito::jsonb)", new
        {
            r.Id, r.CasoId, r.CasoNome, r.CasoTipo, r.Decisao, usuario, r.Responsavel, r.Motivo, Em = r.Em.UtcDateTime, r.Anterior,
            r.Novo, r.Lote, r.CorrigeId, r.Excecao, r.ConfirmacoesNoRegistro, r.ProdutoGlobalId, efeito = efeito.Json(),
        }, tx);

    /// <summary>Serializa decisões do mesmo caso (duas pessoas decidindo ao mesmo tempo).</summary>
    private static async Task Travar(NpgsqlConnection c, NpgsqlTransaction tx, IEnumerable<string> casos)
    {
        foreach (var caso in casos.Distinct().OrderBy(x => x, StringComparer.Ordinal))
            await c.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtext('curadoria:' || @caso))", new { caso }, tx);
    }

    private static async Task<(Guid Id, string Nome)?> ProdutoEscolhido(NpgsqlConnection c, NpgsqlTransaction tx, ProdutoGlobalEscolhido? p)
    {
        if (p?.Id is null) return null;
        if (!Guid.TryParse(p.Id, out var id)) throw Invalido("Produto global não encontrado.");
        var nome = await c.ExecuteScalarAsync<string?>(
            "SELECT nome FROM gestao_catalogo_global WHERE id = @id AND status IN ('validado', 'candidato')", new { id }, tx);
        return nome is null ? throw Invalido("Produto global não encontrado.") : (id, nome);
    }

    private static string Responsavel(string doLogin, string? doPedido)
    {
        var nome = string.IsNullOrWhiteSpace(doLogin) ? doPedido?.Trim() : doLogin.Trim();
        if (string.IsNullOrWhiteSpace(nome))
            throw new GestaoException("RESPONSAVEL_OBRIGATORIO", "Toda decisão precisa de um responsável.", 422);
        return Cortar(nome, 120);
    }

    private static DateTimeOffset Utc(DateTime d) => new(DateTime.SpecifyKind(d, DateTimeKind.Utc));
    private static string Cortar(string s, int max) => s.Length <= max ? s : s[..max];
    private static GestaoException Invalido(string m) => new("CAMPO_INVALIDO", m, 422);

    // ------------------------------------------------------------ linhas do banco

    private sealed record MudancaStatus(Guid Id, string De, string Para);

    private sealed record Mescla(Guid Origem, Guid Destino, Guid[] Vinculos, Guid[] Remesclados);

    private sealed class Efeito
    {
        private static readonly JsonSerializerOptions Opcoes = new(JsonSerializerDefaults.Web);
        public List<MudancaStatus> Catalogo { get; set; } = new();
        public Mescla? Mescla { get; set; }
        public Guid? ProdutoGlobalId { get; set; }
        public string Json() => JsonSerializer.Serialize(this, Opcoes);
        public static Efeito Ler(string? json) =>
            string.IsNullOrWhiteSpace(json) ? new Efeito() : JsonSerializer.Deserialize<Efeito>(json, Opcoes) ?? new Efeito();
    }

    private sealed class SinalLinha
    {
        public string NomeChave { get; set; } = "";
        public string Descricao { get; set; } = "";
        public Guid Loja { get; set; }
        public string? LojaNome { get; set; }
        public string? Cidade { get; set; }
        public Guid GlobalId { get; set; }
        public string GlobalNome { get; set; } = "";
        public string GlobalStatus { get; set; } = "";
        public DateTime ConfirmadoEm { get; set; }
        public int Confirmacoes { get; set; }
        public SinalDeVinculo Sinal() => new(NomeChave, Descricao, Loja, LojaNome, Cidade, GlobalId, GlobalNome, GlobalStatus,
            Utc(ConfirmadoEm), Confirmacoes);
    }

    private sealed class GlobalLinha
    {
        public Guid Id { get; set; }
        public string Nome { get; set; } = "";
        public string Unidade { get; set; } = "";
        public string? Categoria { get; set; }
        public string Status { get; set; } = "";
        public DateTime CriadoEm { get; set; }
        public string? LojaQueCriou { get; set; }
        public int Lojas { get; set; }
        public int Confirmacoes { get; set; }
        public int Cidades { get; set; }
        public string[]? Variacoes { get; set; }
        public ItemGlobal Item() => new(Id, Nome, Unidade, Categoria, Status, Utc(CriadoEm), Lojas, Confirmacoes, Cidades);
    }

    private sealed class DecisaoLinha
    {
        public Guid Id { get; set; }
        public string CasoId { get; set; } = "";
        public string CasoNome { get; set; } = "";
        public string CasoTipo { get; set; } = "";
        public string Decisao { get; set; } = "";
        public string Responsavel { get; set; } = "";
        public string Motivo { get; set; } = "";
        public DateTime Em { get; set; }
        public string Anterior { get; set; } = "";
        public string Novo { get; set; } = "";
        public Guid? Lote { get; set; }
        public Guid? CorrigeId { get; set; }
        public bool Excecao { get; set; }
        public int? ConfirmacoesNoRegistro { get; set; }
        public Guid? ProdutoGlobalId { get; set; }
        public string? Efeito { get; set; }
        public RegistroDecisao Registro() => new(Id, CasoId, CasoNome, CasoTipo, Decisao, Responsavel, Motivo, Utc(Em), Anterior, Novo,
            Lote, CorrigeId, Excecao ? true : null, ConfirmacoesNoRegistro, ProdutoGlobalId);
    }
}
