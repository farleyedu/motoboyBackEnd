using System.Text.RegularExpressions;
using APIBack.Social.Nucleo;
using Dapper;
using Npgsql;

namespace APIBack.Social.Arquivos;

public sealed record NovoArquivoRequest(string? Tipo, string? Nome, string? Mime, long Bytes, string? Hash,
    string? Origem = null, decimal? Duracao = null, int? Largura = null, int? Altura = null);

/// <summary><c>UrlEnviar</c> nulo = o arquivo já estava na loja (mesmo hash) e não precisa ser enviado de novo.</summary>
public sealed record NovoArquivoResposta(Guid ArquivoId, string? UrlEnviar, bool JaExistia);

public sealed record ArquivoDto(Guid Id, string Tipo, string Nome, string Mime, long Bytes, decimal? Duracao, int? Largura, int? Altura,
    string Origem, bool Enviado, DateTime CriadoEm, string? UrlLer);

/// <summary>Regras puras dos arquivos pequenos do editor (R01).</summary>
public static partial class ArquivosRegras
{
    public const long LimiteBytes = 25L * 1024 * 1024;
    public static readonly TimeSpan ValidadeEnvio = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan ValidadeLeitura = TimeSpan.FromMinutes(10);
    /// <summary>Envio que não foi confirmado nesse prazo é apagado pela limpeza.</summary>
    public static readonly TimeSpan PrazoConfirmar = TimeSpan.FromDays(1);

    private static readonly Dictionary<string, string> Extensoes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/png"] = ".png", ["image/jpeg"] = ".jpg", ["image/webp"] = ".webp", ["image/gif"] = ".gif",
        ["audio/mpeg"] = ".mp3", ["audio/wav"] = ".wav", ["audio/x-wav"] = ".wav", ["audio/ogg"] = ".ogg",
        ["audio/webm"] = ".webm", ["audio/mp4"] = ".m4a", ["audio/x-m4a"] = ".m4a", ["audio/aac"] = ".aac", ["audio/flac"] = ".flac",
    };

    private static readonly string[] Origens = { "upload", "voz_ia", "gravacao" };

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex HashValido();

    public sealed record Validado(string Tipo, string Nome, string Mime, string Extensao, string Hash, string Origem);

    public static Validado Validar(NovoArquivoRequest req)
    {
        var tipo = req.Tipo?.Trim().ToLowerInvariant();
        if (tipo is not ("imagem" or "audio")) throw SocialException.Validacao("Só dá para enviar imagem ou áudio.");
        var mime = req.Mime?.Trim().ToLowerInvariant() ?? "";
        if (!Extensoes.TryGetValue(mime, out var ext)) throw SocialException.Validacao("Formato de arquivo não aceito.");
        if ((tipo == "imagem") != mime.StartsWith("image/", StringComparison.Ordinal))
            throw SocialException.Validacao("O formato não combina com o tipo do arquivo.");
        if (req.Bytes <= 0) throw SocialException.Validacao("Arquivo vazio.");
        if (req.Bytes > LimiteBytes) throw SocialException.MuitoGrande("O arquivo passa de 25 MB.");
        var hash = req.Hash?.Trim().ToLowerInvariant() ?? "";
        if (!HashValido().IsMatch(hash)) throw SocialException.Validacao("Hash do arquivo inválido (SHA-256 em hexadecimal).");
        var nome = (req.Nome ?? "").Trim();
        if (nome.Length == 0) nome = "arquivo" + ext;
        if (nome.Length > 200) nome = nome[..200];
        var origem = string.IsNullOrWhiteSpace(req.Origem) ? "upload" : req.Origem.Trim().ToLowerInvariant();
        if (!Origens.Contains(origem)) throw SocialException.Validacao("Origem do arquivo inválida.");
        return new Validado(tipo, nome, mime, ext, hash, origem);
    }
}

public interface ISocialArquivosService
{
    Task<NovoArquivoResposta> CriarAsync(Guid loja, int usuario, NovoArquivoRequest req, CancellationToken ct);
    Task<ArquivoDto> ConfirmarAsync(Guid loja, Guid id, CancellationToken ct);
    Task<ArquivoDto> ObterAsync(Guid loja, Guid id, CancellationToken ct);
    Task ApagarAsync(Guid loja, int usuario, Guid id, CancellationToken ct);
}

public sealed class SocialArquivosService(NpgsqlDataSource fonte, IArmazenamento armazenamento) : ISocialArquivosService
{
    private sealed class Linha
    {
        public Guid Id { get; set; }
        public int DonoUsuarioId { get; set; }
        public string Tipo { get; set; } = "";
        public string Nome { get; set; } = "";
        public string Mime { get; set; } = "";
        public long Bytes { get; set; }
        public decimal? Duracao { get; set; }
        public int? Largura { get; set; }
        public int? Altura { get; set; }
        public string R2Key { get; set; } = "";
        public string Origem { get; set; } = "";
        public DateTime CriadoEm { get; set; }
        public DateTime? EnviadoEm { get; set; }
    }

    private const string Colunas = """
        id AS "Id", dono_usuario_id AS "DonoUsuarioId", tipo AS "Tipo", nome AS "Nome", mime AS "Mime", bytes AS "Bytes",
        duracao AS "Duracao", largura AS "Largura", altura AS "Altura", r2_key AS "R2Key", origem AS "Origem",
        criado_em AS "CriadoEm", enviado_em AS "EnviadoEm"
        """;

    public async Task<NovoArquivoResposta> CriarAsync(Guid loja, int usuario, NovoArquivoRequest req, CancellationToken ct)
    {
        var v = ArquivosRegras.Validar(req);
        if (!armazenamento.Configurado) throw SocialException.SemArmazenamento();
        return await SocialBanco.NaLoja(fonte, loja, ct, async (c, tx) =>
        {
            // Mesmo conteúdo já na loja: devolve o mesmo arquivo. Se o envio anterior não terminou, manda enviar de novo.
            var existente = await c.QueryFirstOrDefaultAsync<Linha>(new CommandDefinition($"""
                SELECT {Colunas} FROM social_arquivos
                 WHERE estabelecimento_id = @loja AND hash_sha256 = @hash AND apagar_em IS NULL
                 LIMIT 1
                """, new { loja, hash = v.Hash }, tx, cancellationToken: ct));
            existente ??= await c.QueryFirstOrDefaultAsync<Linha>(new CommandDefinition($"""
                SELECT {Colunas} FROM social_arquivos
                 WHERE estabelecimento_id = @loja AND hash_sha256 = @hash AND enviado_em IS NULL AND apagar_em > NOW()
                 ORDER BY criado_em DESC LIMIT 1
                """, new { loja, hash = v.Hash }, tx, cancellationToken: ct));
            if (existente is not null)
            {
                return existente.EnviadoEm is null
                    ? new NovoArquivoResposta(existente.Id, armazenamento.UrlParaEnviar(existente.R2Key, existente.Mime, existente.Bytes,
                        ArquivosRegras.ValidadeEnvio), false)
                    : new NovoArquivoResposta(existente.Id, null, true);
            }

            var id = Guid.NewGuid();
            var key = ArmazenamentoChaves.Arquivo(loja, id, v.Extensao);
            await c.ExecuteAsync(new CommandDefinition("""
                INSERT INTO social_arquivos (id, estabelecimento_id, dono_usuario_id, tipo, nome, mime, bytes, duracao, largura, altura,
                                             r2_key, origem, hash_sha256, apagar_em)
                VALUES (@id, @loja, @usuario, @Tipo, @Nome, @Mime, @bytes, @duracao, @largura, @altura, @key, @Origem, @Hash,
                        NOW() + @prazo)
                """, new
            {
                id, loja, usuario, v.Tipo, v.Nome, v.Mime, bytes = req.Bytes, duracao = req.Duracao, largura = req.Largura,
                altura = req.Altura, key, v.Origem, v.Hash, prazo = ArquivosRegras.PrazoConfirmar
            }, tx, cancellationToken: ct));
            return new NovoArquivoResposta(id, armazenamento.UrlParaEnviar(key, v.Mime, req.Bytes, ArquivosRegras.ValidadeEnvio), false);
        });
    }

    public async Task<ArquivoDto> ConfirmarAsync(Guid loja, Guid id, CancellationToken ct)
    {
        if (!armazenamento.Configurado) throw SocialException.SemArmazenamento();
        var linha = await Buscar(loja, id, ct, incluirPendente: true);
        if (linha.EnviadoEm is not null) return Dto(linha);
        var tamanho = await armazenamento.TamanhoAsync(linha.R2Key, ct);
        if (tamanho is null) throw SocialException.Validacao("O arquivo ainda não chegou. Envie de novo.");
        if (tamanho.Value != linha.Bytes) throw SocialException.Validacao("O arquivo chegou com tamanho diferente do informado.");
        await SocialBanco.NaLoja(fonte, loja, ct, async (c, tx) =>
        {
            try
            {
                // Pode bater no índice único: outro envio do mesmo conteúdo terminou antes.
                await c.ExecuteAsync(new CommandDefinition("""
                    UPDATE social_arquivos SET enviado_em = NOW(), apagar_em = NULL
                     WHERE id = @id AND estabelecimento_id = @loja AND enviado_em IS NULL
                    """, new { id, loja }, tx, cancellationToken: ct));
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                throw new SocialException("JA_EXISTE", "Este arquivo já foi enviado. Peça de novo para usar o que já está salvo.", 409);
            }
            return 0;
        });
        return Dto(await Buscar(loja, id, ct, incluirPendente: false));
    }

    public async Task<ArquivoDto> ObterAsync(Guid loja, Guid id, CancellationToken ct)
    {
        var linha = await Buscar(loja, id, ct, incluirPendente: true);
        return Dto(linha);
    }

    public async Task ApagarAsync(Guid loja, int usuario, Guid id, CancellationToken ct)
    {
        var linha = await Buscar(loja, id, ct, incluirPendente: true);
        if (linha.DonoUsuarioId != usuario) throw SocialException.SemPermissao("Só quem enviou pode apagar este arquivo.");
        await SocialBanco.NaLoja(fonte, loja, ct, (c, tx) => c.ExecuteAsync(new CommandDefinition("""
            UPDATE social_arquivos SET apagar_em = NOW() WHERE id = @id AND estabelecimento_id = @loja
            """, new { id, loja }, tx, cancellationToken: ct)));
    }

    private async Task<Linha> Buscar(Guid loja, Guid id, CancellationToken ct, bool incluirPendente)
    {
        var linha = await SocialBanco.NaLoja(fonte, loja, ct, (c, tx) => c.QueryFirstOrDefaultAsync<Linha>(new CommandDefinition($"""
            SELECT {Colunas} FROM social_arquivos
             WHERE id = @id AND estabelecimento_id = @loja
               AND (apagar_em IS NULL OR (@incluirPendente AND enviado_em IS NULL AND apagar_em > NOW()))
            """, new { id, loja, incluirPendente }, tx, cancellationToken: ct)));
        return linha ?? throw SocialException.NaoEncontrado("Arquivo não encontrado.");
    }

    private ArquivoDto Dto(Linha l) => new(l.Id, l.Tipo, l.Nome, l.Mime, l.Bytes, l.Duracao, l.Largura, l.Altura, l.Origem,
        l.EnviadoEm is not null, l.CriadoEm,
        l.EnviadoEm is not null && armazenamento.Configurado ? armazenamento.UrlParaLer(l.R2Key, ArquivosRegras.ValidadeLeitura) : null);
}
