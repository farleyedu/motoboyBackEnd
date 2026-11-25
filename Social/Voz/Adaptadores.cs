using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security;
using System.Text;
using System.Text.Json;
using APIBack.Social.Ia;

namespace APIBack.Social.Voz;

/// <summary>Chamada HTTP comum: erro do provedor vira <see cref="FalhaIaException"/>; o corpo do erro nunca vai para a resposta.</summary>
internal static class HttpVoz
{
    public static async Task<HttpResponseMessage> EnviarAsync(HttpClient http, HttpRequestMessage pedido, string provedor, CancellationToken ct)
    {
        HttpResponseMessage r;
        try
        {
            r = await http.SendAsync(pedido, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new FalhaIaException(provedor, "sem conexão", TipoFalhaIa.ForaDoAr, ex);
        }
        if (r.IsSuccessStatusCode) return r;
        var corpo = await r.Content.ReadAsStringAsync(ct);
        var status = (int)r.StatusCode;
        r.Dispose();
        // ElevenLabs avisa cota esgotada com 401 + "quota_exceeded".
        if (corpo.Contains("quota_exceeded", StringComparison.OrdinalIgnoreCase) || corpo.Contains("insufficient_quota", StringComparison.OrdinalIgnoreCase))
            throw new FalhaIaException(provedor, "sem saldo", TipoFalhaIa.SemSaldo);
        throw FalhaIaException.DeHttp(provedor, status, corpo);
    }

    public static decimal Limitar(decimal v, decimal min, decimal max) => Math.Clamp(v <= 0 ? 1 : v, min, max);
}

/// <summary>ElevenLabs: fala com tempo por caractere (vira tempo por palavra) e voz clonada (Instant Voice Cloning).</summary>
public sealed class VozElevenLabs(HttpClient http, string chave, string? baseUrl) : IVoz
{
    private readonly string _base = (baseUrl ?? "https://api.elevenlabs.io").TrimEnd('/');

    public string Provedor => ProvedoresSocial.ElevenLabs;
    public CapacidadesVoz Capacidades { get; } = new(true, true, true);

    private sealed record Alinhado(
        [property: System.Text.Json.Serialization.JsonPropertyName("characters")] List<string>? Caracteres,
        [property: System.Text.Json.Serialization.JsonPropertyName("character_start_times_seconds")] List<decimal>? Inicios,
        [property: System.Text.Json.Serialization.JsonPropertyName("character_end_times_seconds")] List<decimal>? Fins);

    private sealed record ComTempo(
        [property: System.Text.Json.Serialization.JsonPropertyName("audio_base64")] string? Audio,
        [property: System.Text.Json.Serialization.JsonPropertyName("alignment")] Alinhado? Alinhamento);

    public async Task<AudioVoz> SintetizarAsync(PedidoVoz pedido, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"{_base}/v1/text-to-speech/{Uri.EscapeDataString(pedido.Voz)}/with-timestamps?output_format=mp3_44100_128")
        {
            Content = JsonContent.Create(new
            {
                text = pedido.Texto,
                model_id = pedido.Modelo ?? "eleven_multilingual_v2",
                voice_settings = new { stability = 0.5, similarity_boost = 0.75, speed = HttpVoz.Limitar(pedido.Velocidade, 0.7m, 1.2m) },
            }),
        };
        req.Headers.Add("xi-api-key", chave);
        using var r = await HttpVoz.EnviarAsync(http, req, Provedor, ct);
        var corpo = await r.Content.ReadFromJsonAsync<ComTempo>(cancellationToken: ct);
        if (string.IsNullOrEmpty(corpo?.Audio)) throw new FalhaIaException(Provedor, "resposta vazia", TipoFalhaIa.ForaDoAr);
        var bytes = Convert.FromBase64String(corpo.Audio);
        var a = corpo.Alinhamento;
        var palavras = a?.Caracteres is { Count: > 0 } && a.Inicios is not null && a.Fins is not null
            ? Alinhamento.DeCaracteres(a.Caracteres, a.Inicios, a.Fins)
            : null;
        var duracao = AudioInfo.DuracaoMp3(bytes) ?? palavras?.LastOrDefault()?.Fim ?? 0;
        return new AudioVoz(bytes, "audio/mpeg", duracao, palavras);
    }

    public async Task<string> CriarVozClonadaAsync(AmostraVoz amostra, ConsentimentoVoz consentimento, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent($"Zippy {consentimento.Loja:N}-{consentimento.Usuario}"), "name");
        form.Add(new StringContent($"Voz própria do usuário {consentimento.Usuario}, consentimento {consentimento.ConsentimentoId} " +
                                   $"(termo {consentimento.TermoVersao}, {consentimento.AceitoEm:O})."), "description");
        form.Add(new StringContent("true"), "remove_background_noise");
        var arquivo = new ByteArrayContent(amostra.Bytes);
        arquivo.Headers.ContentType = new MediaTypeHeaderValue(amostra.Mime);
        form.Add(arquivo, "files", amostra.NomeArquivo);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_base}/v1/voices/add") { Content = form };
        req.Headers.Add("xi-api-key", chave);
        using var r = await HttpVoz.EnviarAsync(http, req, Provedor, ct);
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
        return doc.RootElement.TryGetProperty("voice_id", out var id) && id.GetString() is { Length: > 0 } v
            ? v
            : throw new FalhaIaException(Provedor, "resposta sem voice_id", TipoFalhaIa.ForaDoAr);
    }

    public async Task ApagarVozClonadaAsync(string vozId, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Delete, $"{_base}/v1/voices/{Uri.EscapeDataString(vozId)}");
        req.Headers.Add("xi-api-key", chave);
        try
        {
            using var _ = await HttpVoz.EnviarAsync(http, req, Provedor, ct);
        }
        catch (FalhaIaException f) when (f.Tipo == TipoFalhaIa.Pedido)
        {
            // 404/400: a voz já não existe no provedor. A revogação segue.
        }
    }
}

/// <summary>OpenAI TTS: só voz pronta, MP3, sem tempo por palavra (o serviço estima).</summary>
public sealed class VozOpenAI(HttpClient http, string chave, string? baseUrl) : IVoz
{
    private readonly string _base = (baseUrl ?? "https://api.openai.com").TrimEnd('/');

    public string Provedor => ProvedoresSocial.OpenAI;
    public CapacidadesVoz Capacidades { get; } = new(true, false, false);

    public async Task<AudioVoz> SintetizarAsync(PedidoVoz pedido, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_base}/v1/audio/speech")
        {
            Content = JsonContent.Create(new
            {
                model = pedido.Modelo ?? "gpt-4o-mini-tts",
                input = pedido.Texto,
                voice = pedido.Voz,
                speed = HttpVoz.Limitar(pedido.Velocidade, 0.25m, 4m),
                response_format = "mp3",
            }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", chave);
        using var r = await HttpVoz.EnviarAsync(http, req, Provedor, ct);
        var bytes = await r.Content.ReadAsByteArrayAsync(ct);
        if (bytes.Length == 0) throw new FalhaIaException(Provedor, "resposta vazia", TipoFalhaIa.ForaDoAr);
        return new AudioVoz(bytes, "audio/mpeg", AudioInfo.DuracaoMp3(bytes) ?? CreditosRegras.SegundosEstimados(pedido.Texto, pedido.Velocidade), null);
    }

    public Task<string> CriarVozClonadaAsync(AmostraVoz amostra, ConsentimentoVoz consentimento, CancellationToken ct) =>
        throw new FalhaIaException(Provedor, "não clona voz", TipoFalhaIa.Pedido);

    public Task ApagarVozClonadaAsync(string vozId, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Azure Speech (REST com SSML): só voz pronta, MP3. O endereço tem a região (ex.: brazilsouth).</summary>
public sealed class VozAzure(HttpClient http, string chave, string endpoint) : IVoz
{
    public string Provedor => ProvedoresSocial.Azure;
    public CapacidadesVoz Capacidades { get; } = new(true, false, false);

    public static string Ssml(PedidoVoz p)
    {
        var vel = HttpVoz.Limitar(p.Velocidade, 0.5m, 2m);
        var taxa = ((vel - 1) * 100).ToString("+0;-0;+0", CultureInfo.InvariantCulture) + "%";
        var idioma = string.IsNullOrWhiteSpace(p.Idioma) ? "pt-BR" : p.Idioma;
        return $"<speak version='1.0' xml:lang='{SecurityElement.Escape(idioma)}'><voice name='{SecurityElement.Escape(p.Voz)}'>" +
               $"<prosody rate='{taxa}'>{SecurityElement.Escape(p.Texto)}</prosody></voice></speak>";
    }

    public async Task<AudioVoz> SintetizarAsync(PedidoVoz pedido, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{endpoint.TrimEnd('/')}/cognitiveservices/v1")
        {
            Content = new StringContent(Ssml(pedido), Encoding.UTF8, "application/ssml+xml"),
        };
        req.Headers.Add("Ocp-Apim-Subscription-Key", chave);
        req.Headers.Add("X-Microsoft-OutputFormat", "audio-24khz-48kbitrate-mono-mp3");
        req.Headers.UserAgent.ParseAdd("ZippySocial/1.0");
        using var r = await HttpVoz.EnviarAsync(http, req, Provedor, ct);
        var bytes = await r.Content.ReadAsByteArrayAsync(ct);
        if (bytes.Length == 0) throw new FalhaIaException(Provedor, "resposta vazia", TipoFalhaIa.ForaDoAr);
        return new AudioVoz(bytes, "audio/mpeg", AudioInfo.DuracaoMp3(bytes) ?? CreditosRegras.SegundosEstimados(pedido.Texto, pedido.Velocidade), null);
    }

    public Task<string> CriarVozClonadaAsync(AmostraVoz amostra, ConsentimentoVoz consentimento, CancellationToken ct) =>
        throw new FalhaIaException(Provedor, "não clona voz", TipoFalhaIa.Pedido);

    public Task ApagarVozClonadaAsync(string vozId, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Desenvolvimento e testes, sem chave: WAV em silêncio com a duração estimada e o tempo de cada palavra.</summary>
public sealed class VozFalsa : IVoz
{
    public string Provedor => ProvedoresSocial.Falso;
    public CapacidadesVoz Capacidades { get; } = new(true, true, true);

    public Task<AudioVoz> SintetizarAsync(PedidoVoz pedido, CancellationToken ct)
    {
        var duracao = CreditosRegras.SegundosEstimados(pedido.Texto, pedido.Velocidade);
        var bytes = AudioInfo.WavSilencio(duracao);
        // Um byte do texto no fim do silêncio: falas diferentes não viram o mesmo arquivo.
        var marca = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(pedido.Voz + "|" + pedido.Texto));
        bytes[^1] = marca[0];
        bytes[^2] = marca[1];
        return Task.FromResult(new AudioVoz(bytes, "audio/wav", duracao, Alinhamento.Estimar(pedido.Texto, duracao)));
    }

    public Task<string> CriarVozClonadaAsync(AmostraVoz amostra, ConsentimentoVoz consentimento, CancellationToken ct) =>
        Task.FromResult("falsa-" + Guid.NewGuid().ToString("N")[..12]);

    public Task ApagarVozClonadaAsync(string vozId, CancellationToken ct) => Task.CompletedTask;
}
