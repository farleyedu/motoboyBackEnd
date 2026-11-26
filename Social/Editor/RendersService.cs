using APIBack.Social.Arquivos;
using APIBack.Social.Fila;
using APIBack.Social.Nucleo;
using Dapper;
using Npgsql;

namespace APIBack.Social.Editor;

/*
 * Zippy Social R13: registro dos vídeos gerados. Quem gera é o navegador (WebCodecs) ou a máquina com o zippy-render (R14);
 * aqui só fica o andamento (progresso, bytes, duração) e, quando a pessoa vai publicar, a URL pré-assinada para o MP4 subir
 * ao R2. O backend nunca recebe nem processa o vídeo.
 * Render de máquina (R14): criar já põe o trabalho na fila; o andamento vem da máquina (Social/Fila) e o painel só cancela.
 */

public sealed record NovoRenderRequest(long CorteId, int ReceitaVersao, string? Onde, long? MaquinaId, string? Destino, string? Qualidade,
    decimal? Duracao, string? NomeArquivo, string? Tema = null, OriginalDto? Original = null);

public sealed record AtualizarRenderRequest(string? Status, decimal? Progresso, long? Bytes, decimal? Duracao, decimal? GastoS, string? Erro,
    string? NomeArquivo);

public sealed record SubirRenderRequest(long Bytes);

public sealed record RenderDto(Guid Id, long CorteId, int ReceitaVersao, string Onde, long? MaquinaId, string Destino, string Qualidade,
    string Status, decimal Progresso, long? Bytes, decimal? Duracao, decimal? GastoS, string? Erro, string? NomeArquivo, bool NoR2,
    int? CriadoPor, DateTime CriadoEm, DateTime? ConcluidoEm);

public sealed record SubirRenderDto(Guid Id, string UrlEnviar, string Mime);

/// <summary>Regras puras dos renders (testáveis sem banco).</summary>
public static class RendersRegras
{
    public const string Mime = "video/mp4";
    /// <summary>Um corte final de até 3 min em 4K 60 qps passa pouco de 1 GB; acima disso é erro de quem chamou.</summary>
    public const long LimiteBytes = 2L * 1024 * 1024 * 1024;
    public static readonly TimeSpan ValidadeEnvio = TimeSpan.FromMinutes(30);
    public static readonly string[] Status = ["gerando", "pronto", "falhou", "cancelado"];
    private static readonly string[] Destinos = ["shorts", "reels", "tiktok", "feed", "quad", "yt"];
    private static readonly string[] Qualidades = ["720p", "1080p", "4K"];

    public static string Onde(string? onde, long? maquinaId)
    {
        var o = string.IsNullOrWhiteSpace(onde) ? "navegador" : onde.Trim().ToLowerInvariant();
        if (o is not ("navegador" or "maquina")) throw SocialException.Validacao("Onde gerar: navegador ou maquina.");
        if (o == "maquina" && maquinaId is null or <= 0) throw SocialException.Validacao("Informe a máquina que vai gerar.");
        return o;
    }

    public static string Destino(string? d)
    {
        var v = string.IsNullOrWhiteSpace(d) ? "shorts" : d.Trim().ToLowerInvariant();
        return Destinos.Contains(v) ? v : throw SocialException.Validacao($"Destino desconhecido: {d}.");
    }

    public static string Qualidade(string? q)
    {
        var v = string.IsNullOrWhiteSpace(q) ? "1080p" : q.Trim();
        return Qualidades.Contains(v) ? v : throw SocialException.Validacao($"Qualidade desconhecida: {q}.");
    }

    public static decimal Progresso(decimal p) => p < 0 ? 0 : p > 100 ? 100 : Math.Round(p, 2);

    /// <summary>
    /// Passagem de status permitida. Terminado (pronto, falhou, cancelado) não volta a gerar: gerar de novo é um render novo.
    /// </summary>
    public static string ProximoStatus(string atual, string? novo)
    {
        if (string.IsNullOrWhiteSpace(novo)) return atual;
        var n = novo.Trim().ToLowerInvariant();
        if (!Status.Contains(n)) throw SocialException.Validacao($"Status desconhecido: {novo}.");
        if (n == atual) return atual;
        if (atual != "gerando") throw new SocialException("RENDER_TERMINADO", "Este vídeo já terminou. Gere de novo para um novo.", 409);
        return n;
    }

    /// <summary>Render de máquina: quem informa o andamento é ela; pelo painel só dá para cancelar.</summary>
    public static void SoCancelar(AtualizarRenderRequest req)
    {
        if (!string.Equals(req.Status?.Trim(), "cancelado", StringComparison.OrdinalIgnoreCase) || req.Progresso is not null ||
            req.Bytes is not null || req.Duracao is not null || req.GastoS is not null || req.Erro is not null || req.NomeArquivo is not null)
            throw new SocialException("RENDER_DE_MAQUINA", "Quem atualiza este vídeo é a máquina. Aqui dá só para cancelar.", 409);
    }

    public static string NomeArquivo(string? nome)
    {
        if (string.IsNullOrWhiteSpace(nome)) return "";
        var n = nome.Trim();
        if (n.Length > 120 || n.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0)
            throw SocialException.Validacao("Nome de arquivo inválido.");
        return n;
    }

    public static IReadOnlyList<long> Cortes(string? ids) => EditorRegras.Ids(ids);

    public static string Chave(Guid loja, Guid renderId) =>
        $"{ArmazenamentoChaves.Prefixo(loja)}renders/{renderId:N}.mp4";
}

public interface ISocialRendersService
{
    Task<RenderDto> CriarAsync(Guid loja, int usuario, NovoRenderRequest req, CancellationToken ct);
    Task<RenderDto> AtualizarAsync(Guid loja, Guid id, AtualizarRenderRequest req, CancellationToken ct);
    Task<IReadOnlyList<RenderDto>> ListarAsync(Guid loja, IReadOnlyList<long> corteIds, CancellationToken ct);
    Task<SubirRenderDto> SubirAsync(Guid loja, Guid id, SubirRenderRequest req, CancellationToken ct);
}

public sealed class SocialRendersService(NpgsqlDataSource fonte, IArmazenamento armazenamento, ISocialTempoReal? tempoReal = null)
    : ISocialRendersService
{
    private sealed class Linha
    {
        public Guid Id { get; set; }
        public long CorteId { get; set; }
        public int ReceitaVersao { get; set; }
        public string Onde { get; set; } = "navegador";
        public long? MaquinaId { get; set; }
        public string Destino { get; set; } = "shorts";
        public string Qualidade { get; set; } = "1080p";
        public string Status { get; set; } = "gerando";
        public decimal Progresso { get; set; }
        public long? Bytes { get; set; }
        public decimal? Duracao { get; set; }
        public decimal? GastoS { get; set; }
        public string? Erro { get; set; }
        public string? NomeArquivo { get; set; }
        public string? R2Key { get; set; }
        public int? CriadoPor { get; set; }
        public DateTime CriadoEm { get; set; }
        public DateTime? ConcluidoEm { get; set; }

        public RenderDto Dto() => new(Id, CorteId, ReceitaVersao, Onde, MaquinaId, Destino, Qualidade, Status, Progresso, Bytes, Duracao,
            GastoS, Erro, string.IsNullOrEmpty(NomeArquivo) ? null : NomeArquivo, R2Key is not null, CriadoPor, CriadoEm, ConcluidoEm);
    }

    internal static async Task<RenderDto?> UmOuNada(NpgsqlConnection c, NpgsqlTransaction tx, string sql, object args,
        CancellationToken ct) =>
        (await c.QuerySingleOrDefaultAsync<Linha>(new CommandDefinition(sql, args, tx, cancellationToken: ct)))?.Dto();

    internal static async Task<IReadOnlyList<RenderDto>> Varios(NpgsqlConnection c, NpgsqlTransaction tx, string sql, object args,
        CancellationToken ct) =>
        (await c.QueryAsync<Linha>(new CommandDefinition(sql, args, tx, cancellationToken: ct))).Select(l => l.Dto()).ToList();

    internal const string Colunas = """
        id AS "Id", corte_id AS "CorteId", receita_versao AS "ReceitaVersao", onde AS "Onde", maquina_id AS "MaquinaId",
        destino AS "Destino", qualidade AS "Qualidade", status AS "Status", progresso AS "Progresso", bytes AS "Bytes",
        duracao AS "Duracao", gasto_s AS "GastoS", erro AS "Erro", nome_arquivo AS "NomeArquivo", r2_key AS "R2Key",
        criado_por AS "CriadoPor", criado_em AS "CriadoEm", concluido_em AS "ConcluidoEm"
        """;

    public async Task<RenderDto> CriarAsync(Guid loja, int usuario, NovoRenderRequest req, CancellationToken ct)
    {
        if (req.CorteId <= 0) throw SocialException.Validacao("Informe o corte.");
        if (req.ReceitaVersao < 0) throw SocialException.Validacao("Versão da receita inválida.");
        var onde = RendersRegras.Onde(req.Onde, req.MaquinaId);
        var destino = RendersRegras.Destino(req.Destino);
        var qualidade = RendersRegras.Qualidade(req.Qualidade);
        var nome = RendersRegras.NomeArquivo(req.NomeArquivo);
        var (criado, cancelados) = await SocialBanco.NaLoja(fonte, loja, ct, async (c, tx) =>
        {
            // na máquina, a trava do corte faz dois cliques juntos virarem um trabalho só
            var existe = await c.ExecuteScalarAsync<long?>(new CommandDefinition(
                $"SELECT id FROM social_cortes WHERE id = @id AND estabelecimento_id = @loja{(onde == "maquina" ? " FOR UPDATE" : "")}",
                new { id = req.CorteId, loja }, tx, cancellationToken: ct));
            if (existe is null) throw SocialException.NaoEncontrado("Esse corte não existe nesta loja.");
            if (onde == "maquina")
            {
                await RenderDaFila.ConferirMaquina(c, tx, loja, usuario, req.MaquinaId!.Value, ct);
                var igual = await UmOuNada(c, tx, $"""
                    SELECT {Colunas} FROM social_renders
                     WHERE estabelecimento_id = @loja AND corte_id = @corte AND status = 'gerando' AND onde = 'maquina'
                       AND receita_versao = @versao AND maquina_id = @maq AND destino = @destino AND qualidade = @qualidade
                     LIMIT 1
                    """, new { loja, corte = req.CorteId, versao = req.ReceitaVersao, maq = req.MaquinaId, destino, qualidade }, ct);
                if (igual is not null) return (igual, (IReadOnlyList<RenderDto>)[]);
            }
            if (onde == "maquina") await RenderDaFila.TravarTrabalhosDoCorte(c, tx, loja, req.CorteId, ct);
            // gerar de novo o mesmo corte encerra o que estava gerando (aba recarregada, por exemplo)
            var antigos = await Varios(c, tx, $"""
                UPDATE social_renders SET status = 'cancelado', atualizado_em = NOW(), concluido_em = NOW()
                 WHERE estabelecimento_id = @loja AND corte_id = @corte AND status = 'gerando' AND onde = @onde
                RETURNING {Colunas}
                """, new { loja, corte = req.CorteId, onde }, ct);
            if (onde == "maquina") await RenderDaFila.CancelarTrabalhos(c, tx, loja, antigos.Select(a => a.Id).ToArray(), ct);
            var linha = await c.QuerySingleAsync<Linha>(new CommandDefinition($"""
                INSERT INTO social_renders (estabelecimento_id, corte_id, receita_versao, onde, maquina_id, destino, qualidade,
                                            duracao, nome_arquivo, criado_por)
                VALUES (@loja, @corte, @versao, @onde, @maq, @destino, @qualidade, @duracao, NULLIF(@nome, ''), @usuario)
                RETURNING {Colunas}
                """, new
            {
                loja, corte = req.CorteId, versao = req.ReceitaVersao, onde, maq = onde == "maquina" ? req.MaquinaId : null, destino,
                qualidade, duracao = req.Duracao, nome, usuario
            }, tx, cancellationToken: ct));
            var dto = linha.Dto();
            if (onde == "maquina") await RenderDaFila.Enfileirar(c, tx, loja, usuario, dto, req, ct);
            return (dto, antigos);
        });
        if (onde == "maquina" && tempoReal is not null)
            foreach (var r in cancelados.Append(criado)) await tempoReal.RenderAtualizado(loja, r, ct);
        return criado;
    }

    public async Task<RenderDto> AtualizarAsync(Guid loja, Guid id, AtualizarRenderRequest req, CancellationToken ct)
    {
        var mudado = await SocialBanco.NaLoja(fonte, loja, ct, async (c, tx) =>
        {
            // o trabalho da máquina é travado antes do render, na mesma ordem das rotas do worker (render de navegador não tem)
            if (await TemTrabalhos(c, tx, ct))
                await c.ExecuteAsync(new CommandDefinition(
                    "SELECT 1 FROM social_trabalhos WHERE estabelecimento_id = @loja AND alvo_id = @id FOR UPDATE",
                    new { loja, id }, tx, cancellationToken: ct));
            var atual = await Ler(c, tx, loja, id, ct, paraMudar: true);
            if (atual.Onde == "maquina") RendersRegras.SoCancelar(req);
            var status = RendersRegras.ProximoStatus(atual.Status, req.Status);
            if (req.Bytes is <= 0 or > RendersRegras.LimiteBytes) throw SocialException.Validacao("Tamanho do vídeo inválido.");
            if (status == "pronto" && (req.Bytes ?? atual.Bytes) is null) throw SocialException.Validacao("Vídeo pronto precisa do tamanho.");
            var progresso = status == "pronto" ? 100m : RendersRegras.Progresso(req.Progresso ?? atual.Progresso);
            var nome = req.NomeArquivo is null ? atual.NomeArquivo : RendersRegras.NomeArquivo(req.NomeArquivo);
            var erro = status == "falhou" ? (req.Erro ?? atual.Erro ?? "Falhou sem motivo.") : null;
            if (erro is { Length: > 500 }) erro = erro[..500];
            var linha = await c.QuerySingleAsync<Linha>(new CommandDefinition($"""
                UPDATE social_renders
                   SET status = @status, progresso = @progresso, bytes = COALESCE(@bytes, bytes), duracao = COALESCE(@duracao, duracao),
                       gasto_s = COALESCE(@gasto, gasto_s), erro = @erro, nome_arquivo = NULLIF(@nome, ''), atualizado_em = NOW(),
                       concluido_em = CASE WHEN @status = 'gerando' THEN NULL ELSE COALESCE(concluido_em, NOW()) END
                 WHERE id = @id AND estabelecimento_id = @loja
                RETURNING {Colunas}
                """, new
            {
                id, loja, status, progresso, bytes = req.Bytes, duracao = req.Duracao, gasto = req.GastoS, erro, nome = nome ?? ""
            }, tx, cancellationToken: ct));
            if (atual.Onde == "maquina" && status == "cancelado") await RenderDaFila.CancelarTrabalhos(c, tx, loja, [id], ct);
            return linha.Dto();
        });
        if (mudado.Onde == "maquina" && tempoReal is not null) await tempoReal.RenderAtualizado(loja, mudado, ct);
        return mudado;
    }

    /// <summary>A fila chegou numa migration depois dos renders.</summary>
    private static Task<bool> TemTrabalhos(NpgsqlConnection c, NpgsqlTransaction tx, CancellationToken ct) =>
        c.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT to_regclass('social_trabalhos') IS NOT NULL", transaction: tx,
            cancellationToken: ct));

    public Task<IReadOnlyList<RenderDto>> ListarAsync(Guid loja, IReadOnlyList<long> corteIds, CancellationToken ct) =>
        SocialBanco.NaLoja(fonte, loja, ct, async (c, tx) =>
        {
            var linhas = await c.QueryAsync<Linha>(new CommandDefinition($"""
                SELECT {Colunas} FROM social_renders
                 WHERE estabelecimento_id = @loja AND corte_id = ANY(@ids)
                 ORDER BY criado_em DESC
                 LIMIT 500
                """, new { loja, ids = corteIds.ToArray() }, tx, cancellationToken: ct));
            return (IReadOnlyList<RenderDto>)linhas.Select(l => l.Dto()).ToList();
        });

    public async Task<SubirRenderDto> SubirAsync(Guid loja, Guid id, SubirRenderRequest req, CancellationToken ct)
    {
        if (!armazenamento.Configurado) throw SocialException.SemArmazenamento();
        if (req.Bytes is <= 0 or > RendersRegras.LimiteBytes) throw SocialException.Validacao("Tamanho do vídeo inválido.");
        var key = RendersRegras.Chave(loja, id);
        await SocialBanco.NaLoja(fonte, loja, ct, async (c, tx) =>
        {
            var atual = await Ler(c, tx, loja, id, ct, paraMudar: true);
            if (atual.Status != "pronto") throw new SocialException("RENDER_NAO_PRONTO", "Gere o vídeo antes de publicar.", 409);
            await c.ExecuteAsync(new CommandDefinition("""
                UPDATE social_renders SET r2_key = @key, bytes = @bytes, atualizado_em = NOW() WHERE id = @id AND estabelecimento_id = @loja
                """, new { key, bytes = req.Bytes, id, loja }, tx, cancellationToken: ct));
            return 0;
        });
        return new SubirRenderDto(id, armazenamento.UrlParaEnviar(key, RendersRegras.Mime, req.Bytes, RendersRegras.ValidadeEnvio), RendersRegras.Mime);
    }

    private static async Task<Linha> Ler(NpgsqlConnection c, NpgsqlTransaction tx, Guid loja, Guid id, CancellationToken ct, bool paraMudar)
    {
        var linha = await c.QuerySingleOrDefaultAsync<Linha>(new CommandDefinition(
            $"SELECT {Colunas} FROM social_renders WHERE id = @id AND estabelecimento_id = @loja{(paraMudar ? " FOR UPDATE" : "")}",
            new { id, loja }, tx, cancellationToken: ct));
        return linha ?? throw SocialException.NaoEncontrado("Esse vídeo gerado não existe nesta loja.");
    }
}
