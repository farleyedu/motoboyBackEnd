using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace APIBack.Atendimento
{
    /// <summary>Webhook de um numero proprio como a tela enxerga. O verify token so vai para quem pode configurar o WhatsApp da loja.</summary>
    public sealed record CanalWebhookView(
        string Estado, string? Url, string? VerifyToken, string? Erro, DateTime? RegistradoEm,
        string? Codigo = null, string? ComoResolver = null, string? Campo = null);

    /// <summary>X-Hub-Signature-256: HMAC-SHA256 do corpo com o app secret do app que enviou o webhook.</summary>
    public static class AssinaturaMeta
    {
        public static string Calcular(string segredo, string corpo) =>
            "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(segredo), Encoding.UTF8.GetBytes(corpo))).ToLowerInvariant();

        /// <summary>Sem segredo ou sem cabecalho nunca vale: o webhook por canal nao tem modo "aceita sem assinatura".</summary>
        public static bool Valida(string? segredo, string? cabecalho, string corpo)
        {
            if (string.IsNullOrWhiteSpace(segredo) || string.IsNullOrWhiteSpace(cabecalho)) return false;

            return CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(Calcular(segredo, corpo)), Encoding.ASCII.GetBytes(cabecalho.Trim().ToLowerInvariant()));
        }
    }

    public interface ICanalWebhookService
    {
        /// <summary>https://{App:BaseUrl}/wa/webhook/{chave}, ou nulo se a API nao conhece o proprio endereco https.</summary>
        string? UrlDe(string? chave);
        /// <summary>Estado do webhook para a tela. <paramref name="comSegredos"/> inclui URL e verify token (quem configura o WhatsApp).</summary>
        CanalWebhookView Visao(CanalWhatsapp canal, bool comSegredos);
        /// <summary>Gera a chave da URL e o verify token (aleatorios, URL-safe) de um numero proprio novo.</summary>
        (string Chave, string VerifyToken) GerarCredenciais();
        /// <summary>Registra o webhook na Meta (app do cliente) e grava o resultado. Devolve o canal ja atualizado.</summary>
        Task<CanalWhatsapp> RegistrarAsync(CanalWhatsapp canal, bool primeiraTentativa);
        Task<CanalWhatsapp?> ObterPorChaveAsync(string chave);
        /// <summary>Handshake GET da Meta: devolve o desafio se o verify token confere, ou nulo.</summary>
        Task<string?> ResponderVerificacaoAsync(string chave, string? modo, string? token, string? desafio);
        bool AssinaturaValida(CanalWhatsapp canal, string? cabecalho, string corpo);
    }

    public sealed class CanalWebhookService : ICanalWebhookService
    {
        /// <summary>Depois de tanto tempo conectado sem nenhuma mensagem, a tela avisa em vez de ficar "aguardando" para sempre.</summary>
        public const int MinutosSemRecebimento = 10;

        private readonly ICanalRepository _canais;
        private readonly ITokenProtector _token;
        private readonly IWebhookRegistrador _registrador;
        private readonly IConfiguration _configuration;
        private readonly ILogger<CanalWebhookService> _logger;
        private readonly Func<DateTime> _agora;

        public CanalWebhookService(
            ICanalRepository canais, ITokenProtector token, IWebhookRegistrador registrador, IConfiguration configuration,
            ILogger<CanalWebhookService> logger, Func<DateTime>? agora = null)
        {
            _canais = canais;
            _token = token;
            _registrador = registrador;
            _configuration = configuration;
            _logger = logger;
            _agora = agora ?? (() => DateTime.UtcNow);
        }

        public string? UrlDe(string? chave)
        {
            if (string.IsNullOrWhiteSpace(chave)) return null;

            var baseUrl = _configuration["App:BaseUrl"]?.Trim().TrimEnd('/');
            if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                return null;
            }

            return $"{baseUrl}/wa/webhook/{chave}";
        }

        public CanalWebhookView Visao(CanalWhatsapp canal, bool comSegredos) =>
            MontarVisao(canal, comSegredos ? UrlDe(canal.WebhookChave) : null,
                comSegredos ? _token.Revelar(canal.VerifyTokenCifrado) : null, _agora());

        /// <summary>Pura (sem banco, rede ou relogio): o que a tela mostra para o webhook de um canal.</summary>
        public static CanalWebhookView MontarVisao(CanalWhatsapp canal, string? url, string? verifyToken, DateTime agora)
        {
            // Autorizado: webhook global do app ZippyGo; so o estado (sem URL nem verify token por canal).
            if (canal.Origem == OrigemCanal.Autorizado) return new CanalWebhookView(canal.WebhookEstado, null, null, null, canal.WebhookRegistradoEm);
            if (canal.Origem != OrigemCanal.Proprio) return new CanalWebhookView(WebhookEstado.NaoAplicavel, null, null, null, null);

            var erro = LerErro(canal.WebhookErro);
            if (erro == null
                && canal.WebhookEstado is WebhookEstado.Pendente or WebhookEstado.Registrado
                && canal.UltimoRecebimentoEm == null
                && canal.VerificadoEm is { } desde
                && agora - desde > TimeSpan.FromMinutes(MinutosSemRecebimento))
            {
                erro = WhatsappErros.SemRecebimento(MinutosSemRecebimento);
            }

            return new CanalWebhookView(
                canal.WebhookEstado, url, verifyToken, erro?.Mensagem, canal.WebhookRegistradoEm,
                erro?.Codigo, erro?.ComoResolver, erro?.Campo);
        }

        public (string Chave, string VerifyToken) GerarCredenciais() => (Aleatorio(32), Aleatorio(24));

        public async Task<CanalWhatsapp> RegistrarAsync(CanalWhatsapp canal, bool primeiraTentativa)
        {
            var erro = await TentarRegistrarAsync(canal);
            if (erro == null)
            {
                await _canais.DefinirWebhookAsync(canal.Id, WebhookEstado.AoRegistrar(canal.WebhookEstado), null, _agora());
                _logger.LogInformation("[canal] ev=webhook_registrado canal={Canal} loja={Loja}", canal.Id, canal.IdEstabelecimento);
            }
            else
            {
                await _canais.DefinirWebhookAsync(
                    canal.Id, WebhookEstado.AoFalharRegistro(canal.WebhookEstado, primeiraTentativa), GuardarErro(erro), null);
                _logger.LogWarning(
                    "[canal] ev=webhook_nao_registrado canal={Canal} loja={Loja} codigo={Codigo} automatico_primeira={Primeira}",
                    canal.Id, canal.IdEstabelecimento, erro.Codigo, primeiraTentativa);
            }

            return await _canais.ObterAsync(canal.Id) ?? canal;
        }

        private async Task<WhatsappErro?> TentarRegistrarAsync(CanalWhatsapp canal)
        {
            var url = UrlDe(canal.WebhookChave);
            if (url == null) return WhatsappErros.BaseUrlAusente();

            var token = _token.Revelar(canal.TokenCifrado);
            var appSecret = _token.Revelar(canal.AppSecretCifrado);
            var verifyToken = _token.Revelar(canal.VerifyTokenCifrado);
            // Cifrado com outra chave (Atendimento__ChaveToken trocada): o segredo existe mas nao abre. O cliente precisa conectar de novo.
            if (string.IsNullOrWhiteSpace(token)) return WhatsappErros.TokenInvalido();
            if (string.IsNullOrWhiteSpace(appSecret)) return WhatsappErros.AppSecretInvalido();
            if (string.IsNullOrWhiteSpace(verifyToken) || string.IsNullOrWhiteSpace(canal.AppId) || string.IsNullOrWhiteSpace(canal.WabaId))
            {
                return WhatsappErros.WebhookNaoRegistrado();
            }

            var resultado = await _registrador.RegistrarAsync(canal.AppId, appSecret, canal.WabaId, token, url, verifyToken);
            return resultado.Ok ? null : resultado.Erro ?? WhatsappErros.WebhookNaoRegistrado();
        }

        public Task<CanalWhatsapp?> ObterPorChaveAsync(string chave) => _canais.ObterPorWebhookChaveAsync(chave);

        public async Task<string?> ResponderVerificacaoAsync(string chave, string? modo, string? token, string? desafio)
        {
            var canal = await _canais.ObterPorWebhookChaveAsync(chave);
            if (canal == null || modo != "subscribe") return null;

            var esperado = _token.Revelar(canal.VerifyTokenCifrado);
            if (string.IsNullOrWhiteSpace(esperado) || string.IsNullOrEmpty(token)) return null;
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(esperado), Encoding.UTF8.GetBytes(token))) return null;

            // A Meta conseguiu falar com a URL: vale tanto para o registro automatico quanto para quem colou a URL na mao.
            var proximo = WebhookEstado.AoRegistrar(canal.WebhookEstado);
            if (proximo != canal.WebhookEstado)
            {
                await _canais.DefinirWebhookAsync(canal.Id, proximo, null, _agora());
                _logger.LogInformation("[canal] ev=webhook_validado_pela_meta canal={Canal} loja={Loja}", canal.Id, canal.IdEstabelecimento);
            }

            return desafio ?? string.Empty;
        }

        public bool AssinaturaValida(CanalWhatsapp canal, string? cabecalho, string corpo) =>
            AssinaturaMeta.Valida(_token.Revelar(canal.AppSecretCifrado), cabecalho, corpo);

        // ---- erro guardado em canal_whatsapp.webhook_erro (texto): JSON com o erro explicado ------------------------------

        public static string GuardarErro(WhatsappErro erro) => JsonSerializer.Serialize(new
        {
            codigo = erro.Codigo, mensagem = erro.Mensagem, comoResolver = erro.ComoResolver, campo = erro.Campo, link = erro.Link
        });

        public static WhatsappErro? LerErro(string? guardado)
        {
            if (string.IsNullOrWhiteSpace(guardado)) return null;

            try
            {
                using var doc = JsonDocument.Parse(guardado);
                var r = doc.RootElement;
                string? Texto(string nome) => r.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                return new WhatsappErro(
                    Texto("codigo") ?? WhatsappCodigos.WebhookNaoRegistrado, Texto("mensagem") ?? guardado,
                    Texto("comoResolver") ?? string.Empty, Texto("campo"), Texto("link"));
            }
            catch (JsonException)
            {
                return new WhatsappErro(WhatsappCodigos.WebhookNaoRegistrado, guardado, string.Empty, "webhook");
            }
        }

        private static string Aleatorio(int bytes) =>
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
