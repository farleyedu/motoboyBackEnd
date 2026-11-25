using System.Text;
using System.Text.Json;
using APIBack.Social.Arquivos;
using APIBack.Social.Nucleo;
using Dapper;
using Npgsql;

namespace APIBack.Social.Editor;

public sealed record CorteDto(long Id, long VideoId, string VideoTitulo, string Titulo, decimal Ini, decimal Fim, decimal Duracao,
    int? Nota, string? Decisao, decimal? MomentoForteS, int Ordem, bool Exemplo, bool EhGta, string? MiniaturaUrl, int? ReceitaVersao,
    JsonElement Dados);

public sealed record ReceitaDto(int Versao, JsonElement Receita);
public sealed record SalvarReceitaRequest(int VersaoBase, JsonElement Receita);
public sealed record VersaoHistoricoDto(int Versao, int? Quem, string? QuemNome, DateTime Em);
public sealed record ReceitaHistoricoDto(int Versao, int? Quem, DateTime Em, JsonElement Receita);
public sealed record VideoExemploDto(long VideoId, IReadOnlyList<long> CorteIds, bool JaExistia);

/// <summary>Regras puras do editor (R01): limites e envelope da receita. O conteúdo da receita é do front (motor, R02).</summary>
public static class EditorRegras
{
    public const int LimiteReceitaBytes = 512 * 1024;
    public const int LimiteDadosBytes = 1024 * 1024;
    public const int MaxCortesPorPedido = 50;
    public const int VersoesGuardadas = 30;
    public static readonly TimeSpan ValidadeMiniatura = TimeSpan.FromMinutes(10);

    public static IReadOnlyList<long> Ids(string? ids)
    {
        if (string.IsNullOrWhiteSpace(ids)) throw SocialException.Validacao("Informe os cortes (ids=1,2,3).");
        var lista = new List<long>();
        foreach (var parte in ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!long.TryParse(parte, out var id) || id <= 0) throw SocialException.Validacao($"Corte inválido: {parte}.");
            if (!lista.Contains(id)) lista.Add(id);
        }
        if (lista.Count == 0) throw SocialException.Validacao("Informe os cortes (ids=1,2,3).");
        if (lista.Count > MaxCortesPorPedido) throw SocialException.Validacao($"No máximo {MaxCortesPorPedido} cortes por vez.");
        return lista;
    }

    /// <summary>Texto JSON que vai para o banco. Recusa o que não é objeto ou passa do limite.</summary>
    public static string Dados(JsonElement dados)
    {
        if (dados.ValueKind != JsonValueKind.Object) throw SocialException.Validacao("Os dados do corte precisam ser um objeto.");
        var texto = dados.GetRawText();
        if (Encoding.UTF8.GetByteCount(texto) > LimiteDadosBytes) throw SocialException.MuitoGrande("Os dados do corte passam de 1 MB.");
        return texto;
    }

    /// <summary>Confere só o envelope: objeto, <c>v</c> inteiro ≥ 1 e <c>corteId</c> igual ao da rota (número ou texto).</summary>
    public static string Receita(long corteId, JsonElement receita)
    {
        if (receita.ValueKind != JsonValueKind.Object) throw SocialException.Validacao("A receita precisa ser um objeto.");
        var texto = receita.GetRawText();
        if (Encoding.UTF8.GetByteCount(texto) > LimiteReceitaBytes) throw SocialException.MuitoGrande("A receita passa de 512 KB.");
        if (!receita.TryGetProperty("v", out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var versao) || versao < 1)
            throw SocialException.Validacao("A receita precisa do campo v (versão do formato).");
        if (!receita.TryGetProperty("corteId", out var id)) throw SocialException.Validacao("A receita precisa do campo corteId.");
        var mesmo = id.ValueKind switch
        {
            JsonValueKind.Number => id.TryGetInt64(out var n) && n == corteId,
            JsonValueKind.String => id.GetString() == corteId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => false
        };
        if (!mesmo) throw SocialException.Validacao("O corteId da receita não é deste corte.");
        return texto;
    }

    public static JsonElement Json(string? texto)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(texto) ? "{}" : texto);
        return doc.RootElement.Clone();
    }

    /// <summary>Os 6 cortes do contrato antigo (CONTRATO.md, "Dados fixos"): início em s, duração em s, nota, eventos.</summary>
    public static readonly (string Titulo, int Ini, int Dur, int Nota, string[] Eventos)[] Exemplo =
    {
        ("Explosão + risada forte", 12 * 60 + 15, 40, 92, new[] { "explosao", "risada", "grito" }),
        ("Wasted no meio da ponte", 26 * 60 + 40, 41, 85, new[] { "tiros", "wasted", "risada" }),
        ("Fuga com 4 estrelas", 41 * 60, 47, 78, new[] { "perseguicao", "tiros", "grito" }),
        ("\"Vocês precisam ver isso\"", 55 * 60 + 35, 32, 71, new[] { "frase_forte", "risada" }),
        ("Mission passed no limite", 67 * 60 + 50, 33, 64, new[] { "missao", "grito" }),
        ("Susto na cutscene", 81 * 60 + 30, 25, 57, new[] { "susto", "risada" }),
    };

    public const string ExemploTitulo = "Assalto ao Fleeca com a galera";
    public const int ExemploDuracao = 1 * 3600 + 42 * 60 + 10;
}

public interface ISocialEditorService
{
    Task<IReadOnlyList<CorteDto>> CortesAsync(Guid loja, IReadOnlyList<long> ids, CancellationToken ct);
    Task SalvarDadosAsync(Guid loja, long corteId, JsonElement dados, CancellationToken ct);
    Task<ReceitaDto> ReceitaAsync(Guid loja, long corteId, CancellationToken ct);
    Task<ReceitaDto> SalvarReceitaAsync(Guid loja, int usuario, long corteId, SalvarReceitaRequest req, CancellationToken ct);
    Task<IReadOnlyList<VersaoHistoricoDto>> HistoricoAsync(Guid loja, long corteId, CancellationToken ct);
    Task<ReceitaHistoricoDto> VersaoAsync(Guid loja, long corteId, int versao, CancellationToken ct);
    Task<VideoExemploDto> CriarExemploAsync(Guid loja, int usuario, CancellationToken ct);
    Task ApagarVideoAsync(Guid loja, long videoId, CancellationToken ct);
}

/// <summary>Cortes, receitas versionadas e o vídeo de exemplo (R01). Conflito de versão = 409 com a receita atual.</summary>
public sealed class SocialEditorService(NpgsqlDataSource fonte, IArmazenamento armazenamento) : ISocialEditorService
{
    private sealed class CorteLinha
    {
        public long Id { get; set; }
        public long VideoId { get; set; }
        public string VideoTitulo { get; set; } = "";
        public string Titulo { get; set; } = "";
        public decimal Ini { get; set; }
        public decimal Fim { get; set; }
        public int? Nota { get; set; }
        public string? Decisao { get; set; }
        public decimal? MomentoForteS { get; set; }
        public int Ordem { get; set; }
        public bool Exemplo { get; set; }
        public bool EhGta { get; set; }
        public string? MiniaturaKey { get; set; }
        public int? ReceitaVersao { get; set; }
        public string Dados { get; set; } = "{}";
    }

    private sealed class ReceitaLinha
    {
        public int Versao { get; set; }
        public string Receita { get; set; } = "{}";
        public int? Quem { get; set; }
        public DateTime Em { get; set; }
    }

    public async Task<IReadOnlyList<CorteDto>> CortesAsync(Guid loja, IReadOnlyList<long> ids, CancellationToken ct)
    {
        var linhas = await SocialBanco.NaLoja(fonte, loja, ct, (c, tx) => c.QueryAsync<CorteLinha>(new CommandDefinition("""
            SELECT co.id AS "Id", co.video_id AS "VideoId", v.titulo AS "VideoTitulo", co.titulo AS "Titulo", co.ini AS "Ini",
                   co.fim AS "Fim", co.nota_regras AS "Nota", co.decisao AS "Decisao", co.momento_forte_s AS "MomentoForteS",
                   co.ordem AS "Ordem", v.exemplo AS "Exemplo", v.eh_gta AS "EhGta",
                   COALESCE(co.miniatura_key, v.miniatura_key) AS "MiniaturaKey", r.versao AS "ReceitaVersao", co.dados::text AS "Dados"
              FROM social_cortes co
              JOIN social_videos v ON v.id = co.video_id AND v.estabelecimento_id = co.estabelecimento_id
              LEFT JOIN social_receitas r ON r.corte_id = co.id AND r.estabelecimento_id = co.estabelecimento_id
             WHERE co.estabelecimento_id = @loja AND co.id = ANY(@ids)
            """, new { loja, ids = ids.ToArray() }, tx, cancellationToken: ct)));
        var porId = linhas.ToDictionary(l => l.Id);
        return ids.Where(porId.ContainsKey).Select(id =>
        {
            var l = porId[id];
            var miniatura = l.MiniaturaKey is not null && armazenamento.Configurado && ArmazenamentoChaves.DaLoja(loja, l.MiniaturaKey)
                ? armazenamento.UrlParaLer(l.MiniaturaKey, EditorRegras.ValidadeMiniatura) : null;
            return new CorteDto(l.Id, l.VideoId, l.VideoTitulo, l.Titulo, l.Ini, l.Fim, l.Fim - l.Ini, l.Nota, l.Decisao, l.MomentoForteS,
                l.Ordem, l.Exemplo, l.EhGta, miniatura, l.ReceitaVersao, EditorRegras.Json(l.Dados));
        }).ToList();
    }

    public async Task SalvarDadosAsync(Guid loja, long corteId, JsonElement dados, CancellationToken ct)
    {
        var texto = EditorRegras.Dados(dados);
        var n = await SocialBanco.NaLoja(fonte, loja, ct, (c, tx) => c.ExecuteAsync(new CommandDefinition("""
            UPDATE social_cortes SET dados = CAST(@texto AS jsonb), atualizado_em = NOW()
             WHERE id = @corteId AND estabelecimento_id = @loja
            """, new { texto, corteId, loja }, tx, cancellationToken: ct)));
        if (n == 0) throw SocialException.NaoEncontrado("Corte não encontrado.");
    }

    public async Task<ReceitaDto> ReceitaAsync(Guid loja, long corteId, CancellationToken ct)
    {
        var linha = await SocialBanco.NaLoja(fonte, loja, ct, (c, tx) => c.QueryFirstOrDefaultAsync<ReceitaLinha>(
            new CommandDefinition("""
                SELECT versao AS "Versao", receita::text AS "Receita" FROM social_receitas WHERE corte_id = @corteId AND estabelecimento_id = @loja
                """, new { corteId, loja }, tx, cancellationToken: ct)));
        if (linha is null) throw new SocialException("SEM_RECEITA", "Este corte ainda não tem receita salva.", 404);
        return new ReceitaDto(linha.Versao, EditorRegras.Json(linha.Receita));
    }

    public async Task<ReceitaDto> SalvarReceitaAsync(Guid loja, int usuario, long corteId, SalvarReceitaRequest req, CancellationToken ct)
    {
        var texto = EditorRegras.Receita(corteId, req.Receita);
        if (req.VersaoBase < 0) throw SocialException.Validacao("versaoBase inválida.");
        return await SocialBanco.NaLoja(fonte, loja, ct, async (c, tx) =>
        {
            // Trava o corte: duas gravações simultâneas da primeira versão não podem passar as duas.
            var existe = await c.ExecuteScalarAsync<long?>(new CommandDefinition("""
                SELECT id FROM social_cortes WHERE id = @corteId AND estabelecimento_id = @loja FOR UPDATE
                """, new { corteId, loja }, tx, cancellationToken: ct));
            if (existe is null) throw SocialException.NaoEncontrado("Corte não encontrado.");

            var atual = await c.QueryFirstOrDefaultAsync<ReceitaLinha>(new CommandDefinition("""
                SELECT versao AS "Versao", receita::text AS "Receita" FROM social_receitas WHERE corte_id = @corteId AND estabelecimento_id = @loja
                """, new { corteId, loja }, tx, cancellationToken: ct));
            var versaoAtual = atual?.Versao ?? 0;
            if (req.VersaoBase != versaoAtual)
            {
                throw new SocialException("CONFLITO_VERSAO", "A receita mudou em outro lugar. Junte as mudanças e salve de novo.", 409)
                {
                    Detalhes = atual is null
                        ? new { versao = 0, receita = (JsonElement?)null }
                        : new { versao = versaoAtual, receita = (JsonElement?)EditorRegras.Json(atual.Receita) }
                };
            }

            var nova = versaoAtual + 1;
            await c.ExecuteAsync(new CommandDefinition("""
                INSERT INTO social_receitas (corte_id, estabelecimento_id, versao, receita, atualizado_por, atualizado_em)
                VALUES (@corteId, @loja, @nova, CAST(@texto AS jsonb), @usuario, NOW())
                ON CONFLICT (corte_id) DO UPDATE
                   SET versao = EXCLUDED.versao, receita = EXCLUDED.receita, atualizado_por = EXCLUDED.atualizado_por,
                       atualizado_em = EXCLUDED.atualizado_em;
                INSERT INTO social_receitas_historico (corte_id, estabelecimento_id, versao, receita, quem, em)
                VALUES (@corteId, @loja, @nova, CAST(@texto AS jsonb), @usuario, NOW());
                DELETE FROM social_receitas_historico
                 WHERE corte_id = @corteId AND estabelecimento_id = @loja AND versao <= @nova - @guardar;
                """, new { corteId, loja, nova, texto, usuario, guardar = EditorRegras.VersoesGuardadas }, tx, cancellationToken: ct));
            return new ReceitaDto(nova, EditorRegras.Json(texto));
        });
    }

    public async Task<IReadOnlyList<VersaoHistoricoDto>> HistoricoAsync(Guid loja, long corteId, CancellationToken ct)
    {
        await GarantirCorte(loja, corteId, ct);
        var lista = await SocialBanco.NaLoja(fonte, loja, ct, (c, tx) => c.QueryAsync<VersaoHistoricoDto>(new CommandDefinition("""
            SELECT h.versao AS "Versao", h.quem AS "Quem", u.nome::text AS "QuemNome", h.em AS "Em"
              FROM social_receitas_historico h
              LEFT JOIN usuario u ON u.id = h.quem
             WHERE h.corte_id = @corteId AND h.estabelecimento_id = @loja
             ORDER BY h.versao DESC LIMIT @limite
            """, new { corteId, loja, limite = EditorRegras.VersoesGuardadas }, tx, cancellationToken: ct)));
        return lista.ToList();
    }

    public async Task<ReceitaHistoricoDto> VersaoAsync(Guid loja, long corteId, int versao, CancellationToken ct)
    {
        var linha = await SocialBanco.NaLoja(fonte, loja, ct, (c, tx) => c.QueryFirstOrDefaultAsync<ReceitaLinha>(
            new CommandDefinition("""
                SELECT versao AS "Versao", quem AS "Quem", em AS "Em", receita::text AS "Receita" FROM social_receitas_historico
                 WHERE corte_id = @corteId AND estabelecimento_id = @loja AND versao = @versao
                """, new { corteId, loja, versao }, tx, cancellationToken: ct)));
        if (linha is null) throw SocialException.NaoEncontrado("Versão não encontrada.");
        return new ReceitaHistoricoDto(linha.Versao, linha.Quem, linha.Em, EditorRegras.Json(linha.Receita));
    }

    public async Task<VideoExemploDto> CriarExemploAsync(Guid loja, int usuario, CancellationToken ct) =>
        await SocialBanco.NaLoja(fonte, loja, ct, async (c, tx) =>
        {
            // Um exemplo por loja: pedir de novo devolve o mesmo.
            await c.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(hashtext('social_exemplo:' || @l))",
                new { l = loja.ToString() }, tx, cancellationToken: ct));
            var existente = await c.ExecuteScalarAsync<long?>(new CommandDefinition("""
                SELECT id FROM social_videos WHERE estabelecimento_id = @loja AND exemplo ORDER BY id LIMIT 1
                """, new { loja }, tx, cancellationToken: ct));
            if (existente is long videoExistente)
            {
                var ids = await c.QueryAsync<long>(new CommandDefinition("""
                    SELECT id FROM social_cortes WHERE video_id = @videoExistente AND estabelecimento_id = @loja ORDER BY ordem, id
                    """, new { videoExistente, loja }, tx, cancellationToken: ct));
                return new VideoExemploDto(videoExistente, ids.ToList(), true);
            }

            var videoId = await c.ExecuteScalarAsync<long>(new CommandDefinition("""
                INSERT INTO social_videos (estabelecimento_id, titulo, duracao, resolucao, eh_gta, exemplo, criado_por)
                VALUES (@loja, @titulo, @duracao, '1080p', TRUE, TRUE, @usuario) RETURNING id
                """, new { loja, titulo = EditorRegras.ExemploTitulo, duracao = EditorRegras.ExemploDuracao, usuario }, tx,
                cancellationToken: ct));
            var corteIds = new List<long>();
            var ordem = 0;
            foreach (var (titulo, ini, dur, nota, eventos) in EditorRegras.Exemplo)
            {
                var forte = ini + Math.Round(dur * 0.4m, 2);
                var dados = JsonSerializer.Serialize(new { exemplo = true, eventos });
                corteIds.Add(await c.ExecuteScalarAsync<long>(new CommandDefinition("""
                    INSERT INTO social_cortes (estabelecimento_id, video_id, ini, fim, titulo, nota_regras, momento_forte_s, decisao, ordem, dados)
                    VALUES (@loja, @videoId, @ini, @fim, @titulo, @nota, @forte, 'usar', @ordem, CAST(@dados AS jsonb)) RETURNING id
                    """, new { loja, videoId, ini = (decimal)ini, fim = (decimal)(ini + dur), titulo, nota, forte, ordem = ordem++, dados }, tx,
                    cancellationToken: ct)));
            }
            return new VideoExemploDto(videoId, corteIds, false);
        });

    public async Task ApagarVideoAsync(Guid loja, long videoId, CancellationToken ct)
    {
        var n = await SocialBanco.NaLoja(fonte, loja, ct, (c, tx) => c.ExecuteAsync(new CommandDefinition(
            "DELETE FROM social_videos WHERE id = @videoId AND estabelecimento_id = @loja", new { videoId, loja }, tx, cancellationToken: ct)));
        if (n == 0) throw SocialException.NaoEncontrado("Vídeo não encontrado.");
    }

    private async Task GarantirCorte(Guid loja, long corteId, CancellationToken ct)
    {
        var existe = await SocialBanco.NaLoja(fonte, loja, ct, (c, tx) => c.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM social_cortes WHERE id = @corteId AND estabelecimento_id = @loja)",
            new { corteId, loja }, tx, cancellationToken: ct)));
        if (!existe) throw SocialException.NaoEncontrado("Corte não encontrado.");
    }
}
