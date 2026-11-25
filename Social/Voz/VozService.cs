using System.Security.Cryptography;
using System.Text.Json;
using APIBack.Social.Arquivos;
using APIBack.Social.Ia;
using APIBack.Social.Nucleo;
using Dapper;
using Npgsql;

namespace APIBack.Social.Voz;

public sealed record FalaEstimativaRequest(string? Texto, decimal? Vel);
public sealed record EstimativaVozRequest(List<FalaEstimativaRequest>? Falas, string? Voz = null);
public sealed record EstimativaVozDto(decimal Segundos, int Creditos, string Origem, int Restam, bool Basta);

public sealed record FalaGerarRequest(string? FalaId, string? Texto, string? Voz, decimal? Vel);
public sealed record GerarVozRequest(long CorteId, List<FalaGerarRequest>? Falas, string? Idioma = null);
public sealed record FalaGeradaDto(string FalaId, Guid ArquivoId, decimal Duracao, IReadOnlyList<PalavraTempo> Palavras,
    bool PalavrasEstimadas, bool DoCache, string? UrlLer);
public sealed record GerarVozDto(IReadOnlyList<FalaGeradaDto> Falas, int Creditos, int Restam, string? Origem, string? Aviso);

public sealed record ClonarVozRequest(byte[] Bytes, string Mime, string NomeArquivo, bool Aceite, string? TextoLido,
    decimal? DuracaoInformada, string? Ip, string? UserAgent);
public sealed record VozCloneDto(bool Ativa, string? Provedor, DateTimeOffset? CriadaEm, int Creditos, int? Restam, string? Aviso,
    string TermoVersao, string Termo);

/// <summary>
/// Termo da voz clonada. <b>Rascunho</b>: o dono precisa revisar com advogado antes de vender. A versão vai junto da prova.
/// Só a voz da própria pessoa: nunca de outra pessoa, personagem ou dublador.
/// </summary>
public static class TermoVozClonada
{
    public const string Versao = "2026-11-25-rascunho";

    public const string Texto =
        "Autorizo a Zippy a criar uma cópia digital da minha própria voz a partir desta gravação, para narrar vídeos desta conta. " +
        "Declaro que a voz gravada é minha e não de outra pessoa, personagem ou dublador, e que não vou usar a cópia para me passar " +
        "por outra pessoa. A gravação é enviada ao provedor de voz para criar a cópia. Posso apagar a cópia quando quiser; " +
        "a prova desta autorização (quem, quando, de onde e o resumo da gravação) fica guardada.";
}

/// <summary>Regras puras da voz (validação, vozes do protótipo, limites).</summary>
public static class VozRegras
{
    /// <summary>As 6 vozes do protótipo (<c>VOZES</c> no editor.html). "clone" = a voz clonada de quem pede.</summary>
    public static readonly string[] Vozes = { "masc", "fem", "empolgada", "calma", "tiktok", "doc" };
    public const string Clone = "clone";

    public const int MaxFalas = 40, MaxCaracteres = 1000, MaxFalaId = 64;
    public const decimal VelMin = 0.5m, VelMax = 2m;
    public const decimal CloneMinS = 60, CloneMaxS = 90;
    public const long CloneMaxBytes = 20L * 1024 * 1024;

    public static decimal Vel(decimal? vel)
    {
        var v = vel is null or 0 ? 1m : vel.Value;
        if (v < VelMin || v > VelMax) throw SocialException.Validacao("A velocidade da fala vai de 0,5 a 2.");
        return Math.Round(v, 2);
    }

    public static string Texto(string? texto)
    {
        var t = (texto ?? "").Trim();
        if (t.Length == 0) throw SocialException.Validacao("Tem fala sem texto.");
        if (t.Length > MaxCaracteres) throw SocialException.Validacao($"Cada fala pode ter até {MaxCaracteres} caracteres.");
        return t;
    }

    public static IReadOnlyList<(string Texto, decimal Vel)> Estimativa(EstimativaVozRequest req)
    {
        var falas = req.Falas ?? new();
        if (falas.Count == 0) throw SocialException.Validacao("Mande pelo menos uma fala.");
        if (falas.Count > MaxFalas) throw SocialException.Validacao($"No máximo {MaxFalas} falas por vez.");
        return falas.Select(f => (Texto(f.Texto), Vel(f.Vel))).ToList();
    }

    public sealed record Fala(string FalaId, string Texto, string Voz, decimal Vel);

    public static (IReadOnlyList<Fala> Falas, bool Clone) Gerar(GerarVozRequest req)
    {
        if (req.CorteId <= 0) throw SocialException.Validacao("Informe o corte.");
        var falas = req.Falas ?? new();
        if (falas.Count == 0) throw SocialException.Validacao("Mande pelo menos uma fala.");
        if (falas.Count > MaxFalas) throw SocialException.Validacao($"No máximo {MaxFalas} falas por vez.");
        var lista = new List<Fala>();
        foreach (var f in falas)
        {
            var id = (f.FalaId ?? "").Trim();
            if (id.Length is 0 or > MaxFalaId) throw SocialException.Validacao("Cada fala precisa do falaId.");
            var voz = (f.Voz ?? "").Trim().ToLowerInvariant();
            if (voz != Clone && !Vozes.Contains(voz)) throw SocialException.Validacao("Voz desconhecida.");
            lista.Add(new Fala(id, Texto(f.Texto), voz, Vel(f.Vel)));
        }
        if (lista.Select(f => f.FalaId).Distinct().Count() != lista.Count) throw SocialException.Validacao("falaId repetido.");
        var clones = lista.Count(f => f.Voz == Clone);
        if (clones != 0 && clones != lista.Count)
            throw SocialException.Validacao("Gere a voz clonada e a voz da IA em pedidos separados.");
        return (lista, clones > 0);
    }

    public static decimal DuracaoClone(ClonarVozRequest req)
    {
        if (!req.Aceite)
            throw new SocialException("CONSENTIMENTO_OBRIGATORIO", "Para criar a sua voz, aceite o termo de autorização.", 400);
        if ((req.TextoLido ?? "").Trim().Length < 20)
            throw new SocialException("CONSENTIMENTO_OBRIGATORIO", "Mande o texto que você leu na gravação.", 400);
        if (req.Bytes.Length == 0) throw SocialException.Validacao("Gravação vazia.");
        if (req.Bytes.Length > CloneMaxBytes) throw SocialException.MuitoGrande("A gravação passa de 20 MB.");
        if (!req.Mime.StartsWith("audio/", StringComparison.Ordinal)) throw SocialException.Validacao("Mande um arquivo de áudio.");
        var duracao = AudioInfo.Duracao(req.Bytes, req.Mime) ?? req.DuracaoInformada
            ?? throw SocialException.Validacao("Não deu para saber a duração da gravação.");
        if (duracao < CloneMinS || duracao > CloneMaxS) throw SocialException.Validacao("A gravação precisa ter de 60 a 90 segundos.");
        return duracao;
    }

    public static string Extensao(string mime) => mime switch
    {
        "audio/wav" or "audio/x-wav" => ".wav",
        "audio/ogg" => ".ogg",
        "audio/webm" => ".webm",
        _ => ".mp3",
    };
}

public interface ISocialVozService
{
    Task<EstimativaVozDto> EstimativaAsync(Guid loja, EstimativaVozRequest req, CancellationToken ct);
    Task<GerarVozDto> GerarAsync(Guid loja, int usuario, GerarVozRequest req, CancellationToken ct);
    Task<VozCloneDto> CloneAsync(Guid loja, int usuario, CancellationToken ct);
    Task<VozCloneDto> ClonarAsync(Guid loja, int usuario, ClonarVozRequest req, CancellationToken ct);
    Task<VozCloneDto> ApagarCloneAsync(Guid loja, int usuario, CancellationToken ct);
}

/// <summary>
/// Serviço de voz do Social (R10): fala da IA (ou a própria voz pela IA), com cache, créditos e registro numa transação.
/// O áudio sai do provedor, vai para o R2 e vira um <c>social_arquivos</c> com origem <c>voz_ia</c>; o navegador só lê.
/// </summary>
public sealed class SocialVozService(NpgsqlDataSource fonte, SocialIa ia, IFabricaVoz fabrica, IArmazenamento armazenamento,
    ILogger<SocialVozService> logger) : ISocialVozService
{
    private sealed class CacheLinha
    {
        public string Hash { get; set; } = "";
        public Guid ArquivoId { get; set; }
        public decimal Duracao { get; set; }
        public string Palavras { get; set; } = "[]";
        public bool PalavrasEstimadas { get; set; }
        public string R2Key { get; set; } = "";
    }

    private sealed class CloneLinha
    {
        public long Id { get; set; }
        public string ProvedorId { get; set; } = "";
        public string? VozExternaId { get; set; }
        public string Status { get; set; } = "";
        public DateTime AceitoEm { get; set; }
    }

    private sealed record Gerada(string Hash, AudioVoz Audio, bool Estimadas);

    public async Task<EstimativaVozDto> EstimativaAsync(Guid loja, EstimativaVozRequest req, CancellationToken ct)
    {
        var falas = VozRegras.Estimativa(req);
        var segundos = falas.Sum(f => CreditosRegras.SegundosEstimados(f.Texto, f.Vel));
        var uso = req.Voz?.Trim().ToLowerInvariant() == VozRegras.Clone ? UsosIa.VozClone : UsosIa.Voz;
        var origem = await ia.OrigemAsync(loja, uso, ct);
        var creditos = origem == OrigensIa.Zippy ? CreditosRegras.Voz(segundos, ia.Op.VozSegundosPorCredito) : 0;
        var saldo = await ia.SaldoAsync(loja, ct);
        return new EstimativaVozDto(Math.Round(segundos, 2), creditos, origem, saldo.Restam, saldo.Restam >= creditos);
    }

    public async Task<GerarVozDto> GerarAsync(Guid loja, int usuario, GerarVozRequest req, CancellationToken ct)
    {
        var (falas, clone) = VozRegras.Gerar(req);
        if (!armazenamento.Configurado) throw SocialException.SemArmazenamento();
        await GarantirCorteAsync(loja, req.CorteId, ct);
        var idioma = string.IsNullOrWhiteSpace(req.Idioma) ? "pt-BR" : req.Idioma.Trim();
        var uso = clone ? UsosIa.VozClone : UsosIa.Voz;

        string? provedorFixo = null, vozClonada = null;
        if (clone)
        {
            var ativa = await CloneAtivoAsync(loja, usuario, ct)
                ?? throw new SocialException("SEM_VOZ_CLONADA", "Você ainda não criou a sua voz. Grave a amostra primeiro.", 409);
            provedorFixo = ativa.ProvedorId;
            vozClonada = ativa.VozExternaId;
        }

        // Custo mostrado antes: só o que não está no cache do primeiro provedor que vai atender.
        var primeiro = (await ia.OpcoesAsync(loja, uso, provedorFixo, ct))[0];
        var cachePrevio = await CacheAsync(loja, falas.Select(f => Hash(f, primeiro, vozClonada)).ToArray(), ct);
        var faltam = falas.Where(f => !cachePrevio.ContainsKey(Hash(f, primeiro, vozClonada))).ToList();
        if (faltam.Count == 0)
        {
            var saldo = await ia.SaldoAsync(loja, ct);
            return new GerarVozDto(falas.Select(f => Dto(f.FalaId, cachePrevio[Hash(f, primeiro, vozClonada)])).ToList(), 0, saldo.Restam,
                null, null);
        }
        var previstos = CreditosRegras.Voz(faltam.Sum(f => CreditosRegras.SegundosEstimados(f.Texto, f.Vel)), ia.Op.VozSegundosPorCredito);

        var resultado = await ia.ExecutarAsync(loja, uso, previstos, async (p, c) =>
        {
            var hashes = falas.ToDictionary(f => f.FalaId, f => Hash(f, p, vozClonada));
            var cache = await CacheAsync(loja, hashes.Values.Distinct().ToArray(), c);
            var voz = fabrica.Criar(p);
            var geradas = new Dictionary<string, Gerada>();
            foreach (var f in falas)
            {
                var h = hashes[f.FalaId];
                if (cache.ContainsKey(h) || geradas.ContainsKey(h)) continue;
                var vozId = clone ? vozClonada! : p.VozDoProvedor(f.Voz);
                var audio = await voz.SintetizarAsync(new PedidoVoz(f.Texto, vozId, f.Vel, idioma, p.ModeloDe("tts")), c);
                if (audio.Bytes.Length == 0) throw new FalhaIaException(p.ProvedorId, "áudio vazio", TipoFalhaIa.ForaDoAr);
                var palavras = audio.Palavras is { Count: > 0 } ? audio.Palavras : Alinhamento.Estimar(f.Texto, audio.DuracaoS);
                geradas[h] = new Gerada(h, audio with { Palavras = palavras }, audio.Palavras is not { Count: > 0 });
            }
            return (hashes, cache, geradas);
        }, ct, provedorFixo);

        var (porFala, emCache, novas) = resultado.Valor;
        var p = resultado.Provedor;

        // Envia ao R2 fora da transação; se a cobrança falhar, apaga o que subiu.
        var enviados = new Dictionary<string, (Guid Id, string Key, string Sha)>();
        var gravou = false;
        try
        {
            foreach (var g in novas.Values)
            {
                var id = Guid.NewGuid();
                var key = ArmazenamentoChaves.Arquivo(loja, id, VozRegras.Extensao(g.Audio.Mime));
                await armazenamento.EnviarAsync(key, g.Audio.Bytes, g.Audio.Mime, ct);
                enviados[g.Hash] = (id, key, Convert.ToHexString(SHA256.HashData(g.Audio.Bytes)).ToLowerInvariant());
            }

            var segundos = novas.Values.Sum(g => g.Audio.DuracaoS);
            var caracteres = falas.Where(f => novas.ContainsKey(porFala[f.FalaId])).DistinctBy(f => porFala[f.FalaId]).Sum(f => f.Texto.Length);
            var creditos = CreditosRegras.Voz(segundos, ia.Op.VozSegundosPorCredito);
            var custo = Math.Round(caracteres / 1000m * p.Preco("voz_por_1000_caracteres"), 6);
            var duplicados = new List<string>();

            var (linhas, saldo) = await SocialBanco.NaLoja(fonte, loja, ct, async (c, tx) =>
            {
                var linhas = new Dictionary<string, CacheLinha>(emCache);
                foreach (var g in novas.Values)
                {
                    var (id, key, sha) = enviados[g.Hash];
                    var falaNome = falas.First(f => porFala[f.FalaId] == g.Hash).FalaId;
                    var gravado = await c.QueryFirstOrDefaultAsync<Guid?>(new CommandDefinition("""
                        INSERT INTO social_arquivos (id, estabelecimento_id, dono_usuario_id, tipo, nome, mime, bytes, duracao, r2_key, origem,
                                                     hash_sha256, enviado_em)
                        VALUES (@id, @loja, @usuario, 'audio', @nome, @mime, @bytes, @duracao, @key, 'voz_ia', @sha, NOW())
                        ON CONFLICT (estabelecimento_id, hash_sha256) WHERE apagar_em IS NULL DO NOTHING
                        RETURNING id
                        """, new
                    {
                        id, loja, usuario, nome = $"fala-{falaNome}{VozRegras.Extensao(g.Audio.Mime)}", mime = g.Audio.Mime,
                        bytes = (long)g.Audio.Bytes.Length, duracao = g.Audio.DuracaoS, key, sha,
                    }, tx, cancellationToken: ct));
                    var arquivoId = gravado ?? await c.QuerySingleAsync<Guid>(new CommandDefinition("""
                        SELECT id FROM social_arquivos WHERE estabelecimento_id = @loja AND hash_sha256 = @sha AND apagar_em IS NULL
                        """, new { loja, sha }, tx, cancellationToken: ct));
                    var r2Key = key;
                    if (gravado is null)
                    {
                        duplicados.Add(key);
                        r2Key = await c.QuerySingleAsync<string>(new CommandDefinition(
                            "SELECT r2_key FROM social_arquivos WHERE id = @arquivoId", new { arquivoId }, tx, cancellationToken: ct));
                    }
                    var palavras = JsonSerializer.Serialize(g.Audio.Palavras, JsonWeb);
                    await c.ExecuteAsync(new CommandDefinition("""
                        INSERT INTO social_voz_cache (estabelecimento_id, hash, arquivo_id, duracao, palavras, palavras_estimadas, provedor_id)
                        VALUES (@loja, @hash, @arquivoId, @duracao, CAST(@palavras AS jsonb), @estimadas, @provedor)
                        ON CONFLICT (estabelecimento_id, hash) DO UPDATE
                           SET arquivo_id = EXCLUDED.arquivo_id, duracao = EXCLUDED.duracao, palavras = EXCLUDED.palavras,
                               palavras_estimadas = EXCLUDED.palavras_estimadas, criado_em = NOW()
                        """, new { loja, hash = g.Hash, arquivoId, duracao = g.Audio.DuracaoS, palavras, estimadas = g.Estimadas, provedor = p.ProvedorId },
                        tx, cancellationToken: ct));
                    linhas[g.Hash] = new CacheLinha
                    {
                        Hash = g.Hash, ArquivoId = arquivoId, Duracao = g.Audio.DuracaoS, Palavras = palavras, PalavrasEstimadas = g.Estimadas,
                        R2Key = r2Key,
                    };
                }
                var saldo = await ia.CobrarAsync(c, tx, loja, usuario, p, new UsoIa(uso, p.ProvedorId, p.ModeloDe("tts"), p.Origem,
                    Math.Round(segundos, 2), "segundos", creditos, custo, req.CorteId,
                    new { falas = novas.Count, doCache = falas.Count - novas.Count, caracteres }), ct);
                return (linhas, saldo);
            });
            gravou = true;

            foreach (var key in duplicados) await ApagarSemFalharAsync(key);
            var lista = falas.Select(f =>
            {
                var h = porFala[f.FalaId];
                return Dto(f.FalaId, linhas[h]) with { DoCache = !novas.ContainsKey(h) };
            }).ToList();
            return new GerarVozDto(lista, p.Origem == OrigensIa.Zippy ? creditos : 0, saldo.Restam, p.Origem, resultado.Aviso);
        }
        catch when (!gravou)
        {
            foreach (var (_, key, _) in enviados.Values) await ApagarSemFalharAsync(key);
            throw;
        }
    }

    public async Task<VozCloneDto> CloneAsync(Guid loja, int usuario, CancellationToken ct)
    {
        var ativa = await CloneAtivoAsync(loja, usuario, ct);
        return new VozCloneDto(ativa is not null, ativa?.ProvedorId, ativa is null ? null : new DateTimeOffset(ativa.AceitoEm, TimeSpan.Zero),
            ia.Op.CreditosVozClone, null, null, TermoVozClonada.Versao, TermoVozClonada.Texto);
    }

    public async Task<VozCloneDto> ClonarAsync(Guid loja, int usuario, ClonarVozRequest req, CancellationToken ct)
    {
        var duracao = VozRegras.DuracaoClone(req);
        if (await CloneAtivoAsync(loja, usuario, ct) is not null)
            throw new SocialException("VOZ_JA_EXISTE", "Você já tem uma voz criada. Apague a atual para gravar outra.", 409);
        var sha = Convert.ToHexString(SHA256.HashData(req.Bytes)).ToLowerInvariant();
        var creditos = ia.Op.CreditosVozClone;

        var resultado = await ia.ExecutarAsync(loja, UsosIa.VozClone, creditos, async (p, c) =>
        {
            var voz = fabrica.Criar(p);
            if (!voz.Capacidades.Clonar) throw new FalhaIaException(p.ProvedorId, "não clona voz", TipoFalhaIa.Pedido);
            // A prova fica gravada antes de a gravação sair para o provedor.
            var (consentimentoId, aceitoEm) = await SocialBanco.NaLoja(fonte, loja, c, async (cx, tx) =>
            {
                // Pedido anterior que caiu no meio (servidor reiniciou) não prende a pessoa para sempre.
                await cx.ExecuteAsync(new CommandDefinition("""
                    UPDATE social_voz_consentimentos SET status = 'falhou', erro = 'não terminou'
                     WHERE estabelecimento_id = @loja AND usuario_id = @usuario AND status = 'pendente'
                       AND aceito_em < NOW() - INTERVAL '10 minutes'
                    """, new { loja, usuario }, tx, cancellationToken: c));
                try
                {
                    return await cx.QuerySingleAsync<(long, DateTime)>(new CommandDefinition("""
                        INSERT INTO social_voz_consentimentos (estabelecimento_id, usuario_id, provedor_id, termo_versao, termo_texto, texto_lido,
                                                               ip, user_agent, audio_sha256, audio_bytes, audio_duracao)
                        VALUES (@loja, @usuario, @provedor, @versao, @termo, @lido, @ip, @ua, @sha, @bytes, @duracao)
                        RETURNING id, aceito_em
                        """, new
                    {
                        loja, usuario, provedor = p.ProvedorId, versao = TermoVozClonada.Versao, termo = TermoVozClonada.Texto,
                        lido = req.TextoLido!.Trim(), ip = req.Ip, ua = Curto(req.UserAgent, 400), sha, bytes = (long)req.Bytes.Length, duracao,
                    }, tx, cancellationToken: c));
                }
                catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
                {
                    throw new SocialException("VOZ_JA_EXISTE", "Você já tem uma voz criada. Apague a atual para gravar outra.", 409);
                }
            });
            try
            {
                var vozId = await voz.CriarVozClonadaAsync(new AmostraVoz(req.Bytes, req.Mime, $"amostra{VozRegras.Extensao(req.Mime)}"),
                    new ConsentimentoVoz(consentimentoId, loja, usuario, TermoVozClonada.Versao, new DateTimeOffset(aceitoEm, TimeSpan.Zero)), c);
                return (consentimentoId, vozId, voz);
            }
            catch (Exception ex)
            {
                await MarcarFalhaCloneAsync(loja, consentimentoId, ex is FalhaIaException f ? f.Message : "erro", CancellationToken.None);
                throw;
            }
        }, ct);

        var (id, vozExterna, adaptador) = resultado.Valor;
        var p = resultado.Provedor;
        try
        {
            var saldo = await SocialBanco.NaLoja(fonte, loja, ct, async (c, tx) =>
            {
                await c.ExecuteAsync(new CommandDefinition("""
                    UPDATE social_voz_consentimentos SET status = 'ativa', voz_externa_id = @vozExterna, provedor_id = @provedor
                     WHERE id = @id AND estabelecimento_id = @loja
                    """, new { id, loja, vozExterna, provedor = p.ProvedorId }, tx, cancellationToken: ct));
                return await ia.CobrarAsync(c, tx, loja, usuario, p, new UsoIa(UsosIa.VozClone, p.ProvedorId, null, p.Origem, duracao,
                    "segundos", creditos, p.Preco("voz_clone"), null, new { consentimento = id }), ct);
            });
            return new VozCloneDto(true, p.ProvedorId, DateTimeOffset.UtcNow, p.Origem == OrigensIa.Zippy ? creditos : 0, saldo.Restam,
                resultado.Aviso, TermoVozClonada.Versao, TermoVozClonada.Texto);
        }
        catch
        {
            // Não cobrou (ex.: o saldo acabou entre a conferência e agora): a voz não fica no provedor.
            try { await adaptador.ApagarVozClonadaAsync(vozExterna, CancellationToken.None); }
            catch (Exception ex) { logger.LogWarning(ex, "Social/voz: não apagou a voz {Voz} depois da falha na cobrança", vozExterna); }
            await MarcarFalhaCloneAsync(loja, id, "cobrança não concluída", CancellationToken.None);
            throw;
        }
    }

    public async Task<VozCloneDto> ApagarCloneAsync(Guid loja, int usuario, CancellationToken ct)
    {
        var ativa = await CloneAtivoAsync(loja, usuario, ct) ?? throw SocialException.NaoEncontrado("Você não tem voz criada.");
        if (ativa.VozExternaId is not null)
        {
            var p = (await ia.OpcoesAsync(loja, UsosIa.VozClone, ativa.ProvedorId, ct))[0];
            try
            {
                await fabrica.Criar(p).ApagarVozClonadaAsync(ativa.VozExternaId, ct);
            }
            catch (FalhaIaException f)
            {
                throw IaErros.ProvedorFalhou(p.ProvedorId, f.Tipo);
            }
        }
        await SocialBanco.NaLoja(fonte, loja, ct, (c, tx) => c.ExecuteAsync(new CommandDefinition("""
            UPDATE social_voz_consentimentos SET status = 'revogada', revogado_em = NOW(), revogado_por = @usuario
             WHERE id = @id AND estabelecimento_id = @loja
            """, new { id = ativa.Id, loja, usuario }, tx, cancellationToken: ct)));
        return new VozCloneDto(false, null, null, ia.Op.CreditosVozClone, null, null, TermoVozClonada.Versao, TermoVozClonada.Texto);
    }

    private static readonly JsonSerializerOptions JsonWeb = new(JsonSerializerDefaults.Web);

    private static string Hash(VozRegras.Fala f, ProvedorEscolhido p, string? vozClonada) =>
        CreditosRegras.HashFala(f.Texto, vozClonada ?? p.VozDoProvedor(f.Voz), f.Vel, p.ProvedorId, p.ModeloDe("tts"));

    private FalaGeradaDto Dto(string falaId, CacheLinha l) => new(falaId, l.ArquivoId, l.Duracao,
        JsonSerializer.Deserialize<List<PalavraTempo>>(l.Palavras, JsonWeb) ?? new(), l.PalavrasEstimadas, true,
        armazenamento.Configurado ? armazenamento.UrlParaLer(l.R2Key, ArquivosRegras.ValidadeLeitura) : null);

    private async Task<Dictionary<string, CacheLinha>> CacheAsync(Guid loja, string[] hashes, CancellationToken ct) =>
        (await SocialBanco.NaLoja(fonte, loja, ct, (c, tx) => c.QueryAsync<CacheLinha>(new CommandDefinition("""
            SELECT vc.hash AS "Hash", vc.arquivo_id AS "ArquivoId", vc.duracao AS "Duracao", vc.palavras::text AS "Palavras",
                   vc.palavras_estimadas AS "PalavrasEstimadas", a.r2_key AS "R2Key"
              FROM social_voz_cache vc
              JOIN social_arquivos a ON a.id = vc.arquivo_id AND a.estabelecimento_id = vc.estabelecimento_id AND a.apagar_em IS NULL
             WHERE vc.estabelecimento_id = @loja AND vc.hash = ANY(@hashes)
            """, new { loja, hashes }, tx, cancellationToken: ct)))).ToDictionary(l => l.Hash);

    private Task<CloneLinha?> CloneAtivoAsync(Guid loja, int usuario, CancellationToken ct) =>
        SocialBanco.NaLoja(fonte, loja, ct, (c, tx) => c.QueryFirstOrDefaultAsync<CloneLinha?>(new CommandDefinition("""
            SELECT id AS "Id", provedor_id AS "ProvedorId", voz_externa_id AS "VozExternaId", status AS "Status", aceito_em AS "AceitoEm"
              FROM social_voz_consentimentos
             WHERE estabelecimento_id = @loja AND usuario_id = @usuario AND status = 'ativa'
            """, new { loja, usuario }, tx, cancellationToken: ct)));

    private Task MarcarFalhaCloneAsync(Guid loja, long id, string erro, CancellationToken ct) =>
        SocialBanco.NaLoja(fonte, loja, ct, (c, tx) => c.ExecuteAsync(new CommandDefinition("""
            UPDATE social_voz_consentimentos SET status = 'falhou', erro = @erro WHERE id = @id AND estabelecimento_id = @loja
            """, new { id, loja, erro = Curto(erro, 300) }, tx, cancellationToken: ct)));

    private async Task GarantirCorteAsync(Guid loja, long corteId, CancellationToken ct)
    {
        var existe = await SocialBanco.NaLoja(fonte, loja, ct, (c, tx) => c.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM social_cortes WHERE id = @corteId AND estabelecimento_id = @loja)",
            new { corteId, loja }, tx, cancellationToken: ct)));
        if (!existe) throw SocialException.NaoEncontrado("Corte não encontrado.");
    }

    private async Task ApagarSemFalharAsync(string key)
    {
        try { await armazenamento.ApagarAsync(key, CancellationToken.None); }
        catch (Exception ex) { logger.LogWarning(ex, "Social/voz: não apagou {Key} no armazenamento", key); }
    }

    private static string? Curto(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];
}
