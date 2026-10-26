using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using APIBack.Atendimento.Templates;
using APIBack.Automation.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace APIBack.Atendimento.ConexaoMeta
{
    public sealed record TokenTrocado(string AccessToken, long? ExpiraEmSegundos);

    /// <param name="WabasGerenciaveis">target_ids de whatsapp_business_management: as WABAs que a loja realmente autorizou.</param>
    /// <param name="ExpiraEm">Nulo = nao expira (token de integracao de usuario do sistema).</param>
    public sealed record TokenInfo(
        bool Valido, string? AppId, DateTime? ExpiraEm, IReadOnlyList<string> Permissoes, IReadOnlyList<string> WabasGerenciaveis,
        IReadOnlyList<string> WabasMensagens);

    public sealed record WabaInfo(string Id, string? Nome, string? BusinessId);

    /// <param name="Estado">status do numero na Meta (CONNECTED, PENDING, DISCONNECTED, FLAGGED, RESTRICTED...).</param>
    /// <param name="NoAppBusiness">is_on_biz_app (coexistencia); nulo quando a Meta nao devolveu o campo.</param>
    public sealed record NumeroMeta(
        string Id, string? Telefone, string? Nome, string? Estado, string? VerificacaoCodigo, string? Plataforma, bool? NoAppBusiness);

    public sealed record AssinaturaApp(string Objeto, bool Ativa, IReadOnlyList<string> Campos);

    /// <summary>
    /// Chamadas da Graph API do onboarding de Tech Provider (todas servidor a servidor). Tokens e o App Secret vao no corpo ou no cabecalho
    /// Authorization, nunca no log. Nenhum metodo repete sozinho uma operacao que muda algo na Meta.
    /// </summary>
    public interface IMetaOnboardingApi
    {
        /// <summary>POST /oauth/access_token (client_id, client_secret, code): codigo do Embedded Signup -> token de integracao.</summary>
        Task<MetaResultado<TokenTrocado>> TrocarCodigoAsync(string appId, string appSecret, string code);
        /// <summary>GET /debug_token com token de app: validade, app, permissoes e WABAs efetivamente autorizadas (granular_scopes).</summary>
        Task<MetaResultado<TokenInfo>> DepurarTokenAsync(string appId, string appSecret, string token);
        Task<MetaResultado<WabaInfo>> ObterWabaAsync(string wabaId, string token);
        Task<MetaResultado<IReadOnlyList<NumeroMeta>>> ListarNumerosAsync(string wabaId, string token);
        Task<MetaResultado<NumeroMeta>> ObterNumeroAsync(string phoneNumberId, string token);
        /// <summary>GET /{waba}/subscribed_apps: IDs dos apps inscritos na WABA (so leitura).</summary>
        Task<MetaResultado<IReadOnlyList<string>>> AppsInscritosAsync(string wabaId, string token);
        /// <summary>POST /{waba}/subscribed_apps sem override: os eventos vao para o webhook global ja configurado no app.</summary>
        Task<MetaResultado<bool>> InscreverAppAsync(string wabaId, string token);
        /// <summary>DELETE /{waba}/subscribed_apps: o app deixa de receber eventos desta WABA (nao apaga nada do cliente).</summary>
        Task<MetaResultado<bool>> DesinscreverAppAsync(string wabaId, string token);
        /// <summary>POST /{phone}/register. Nao e idempotente do lado da Meta (limite de tentativas): quem chama decide se pode.</summary>
        Task<MetaResultado<bool>> RegistrarNumeroAsync(string phoneNumberId, string token, string pin);
        /// <summary>POST /{phone}/smb_app_data (coexistencia): sync_type smb_app_state_sync ou history. Cada um so pode uma vez.</summary>
        Task<MetaResultado<bool>> SincronizarAppBusinessAsync(string phoneNumberId, string token, string tipo);
        /// <summary>GET /{app}/subscriptions com token de app: so le a configuracao global do webhook (nunca altera).</summary>
        Task<MetaResultado<IReadOnlyList<AssinaturaApp>>> AssinaturasDoAppAsync(string appId, string appSecret);
        /// <summary>GET /{app}?fields=id,name com token de app: confere se o App ID e o App Secret sao do mesmo app. Devolve o nome.</summary>
        Task<MetaResultado<string>> ObterAppAsync(string appId, string appSecret);
    }

    public sealed class MetaOnboardingApi : IMetaOnboardingApi
    {
        private const string CamposNumero = "id,display_phone_number,verified_name,status,code_verification_status,platform_type";
        private readonly IHttpClientFactory _http;
        private readonly IConfiguration _configuration;
        private readonly ILogger<MetaOnboardingApi> _logger;

        public MetaOnboardingApi(IHttpClientFactory http, IConfiguration configuration, ILogger<MetaOnboardingApi> logger)
        {
            _http = http;
            _configuration = configuration;
            _logger = logger;
        }

        private string Base => $"https://graph.facebook.com/{_configuration["WhatsApp:GraphApiVersion"] ?? "v23.0"}";

        public async Task<MetaResultado<TokenTrocado>> TrocarCodigoAsync(string appId, string appSecret, string code)
        {
            var corpo = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = appId, ["client_secret"] = appSecret, ["code"] = code
            });
            var (texto, falha) = await EnviarAsync("trocar_codigo", HttpMethod.Post, $"{Base}/oauth/access_token", null, corpo);
            if (falha != null) return MetaResultado<TokenTrocado>.Erro(falha);

            return Ler(texto!, r =>
            {
                var token = Texto(r, "access_token");
                if (string.IsNullOrWhiteSpace(token)) return null;
                long? expira = r.TryGetProperty("expires_in", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt64() : null;
                return new TokenTrocado(token, expira);
            });
        }

        public async Task<MetaResultado<TokenInfo>> DepurarTokenAsync(string appId, string appSecret, string token)
        {
            var url = $"{Base}/debug_token?input_token={Uri.EscapeDataString(token)}";
            var (texto, falha) = await EnviarAsync("debug_token", HttpMethod.Get, url, $"{appId}|{appSecret}", null);
            if (falha != null) return MetaResultado<TokenInfo>.Erro(falha);

            return Ler(texto!, r => r.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object ? LerTokenInfo(d) : null);
        }

        internal static TokenInfo LerTokenInfo(JsonElement d)
        {
            var valido = d.TryGetProperty("is_valid", out var v) && v.ValueKind == JsonValueKind.True;
            DateTime? expira = d.TryGetProperty("expires_at", out var e) && e.ValueKind == JsonValueKind.Number && e.GetInt64() > 0
                ? DateTimeOffset.FromUnixTimeSeconds(e.GetInt64()).UtcDateTime
                : null;
            var escopos = Lista(d, "scopes");
            var gerenciaveis = new List<string>();
            var mensagens = new List<string>();
            if (d.TryGetProperty("granular_scopes", out var g) && g.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in g.EnumerateArray())
                {
                    var alvo = Texto(item, "scope") switch
                    {
                        "whatsapp_business_management" => gerenciaveis,
                        "whatsapp_business_messaging" => mensagens,
                        _ => null
                    };
                    alvo?.AddRange(Lista(item, "target_ids"));
                }
            }

            return new TokenInfo(valido, Texto(d, "app_id"), expira, escopos, gerenciaveis.Distinct().ToList(), mensagens.Distinct().ToList());
        }

        public async Task<MetaResultado<WabaInfo>> ObterWabaAsync(string wabaId, string token)
        {
            var (texto, falha) = await EnviarAsync("waba", HttpMethod.Get, $"{Base}/{wabaId}?fields=id,name,owner_business_info", token, null);
            if (falha != null) return MetaResultado<WabaInfo>.Erro(falha);

            return Ler(texto!, r =>
            {
                var id = Texto(r, "id");
                if (id == null) return null;
                var dono = r.TryGetProperty("owner_business_info", out var o) ? Texto(o, "id") : null;
                return new WabaInfo(id, Texto(r, "name"), dono);
            });
        }

        public async Task<MetaResultado<IReadOnlyList<NumeroMeta>>> ListarNumerosAsync(string wabaId, string token)
        {
            var numeros = new List<NumeroMeta>();
            string? url = $"{Base}/{wabaId}/phone_numbers?fields={CamposNumero}&limit=100";
            for (var pagina = 0; url != null && pagina < 20; pagina++)
            {
                var (texto, falha) = await EnviarAsync("phone_numbers", HttpMethod.Get, url, token, null);
                if (falha != null) return MetaResultado<IReadOnlyList<NumeroMeta>>.Erro(falha);

                try
                {
                    using var doc = JsonDocument.Parse(texto!);
                    if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                    {
                        numeros.AddRange(data.EnumerateArray().Select(LerNumero).OfType<NumeroMeta>());
                    }

                    url = doc.RootElement.TryGetProperty("paging", out var p) && p.TryGetProperty("next", out var n)
                          && n.GetString() is { } prox && prox.StartsWith("https://graph.facebook.com/", StringComparison.Ordinal)
                        ? prox
                        : null;
                }
                catch (JsonException)
                {
                    return MetaResultado<IReadOnlyList<NumeroMeta>>.Erro(new MetaFalha(false, 200, null, null, "Resposta invalida.", null));
                }
            }

            return MetaResultado<IReadOnlyList<NumeroMeta>>.Sucesso(numeros);
        }

        public async Task<MetaResultado<NumeroMeta>> ObterNumeroAsync(string phoneNumberId, string token)
        {
            // is_on_biz_app so existe para quem ja e Tech Provider/coexistencia; se a Meta recusar o campo, pergunta sem ele.
            var (texto, falha) = await EnviarAsync("numero", HttpMethod.Get, $"{Base}/{phoneNumberId}?fields={CamposNumero},is_on_biz_app", token, null);
            if (falha is { SemResposta: false, Codigo: 100 })
            {
                (texto, falha) = await EnviarAsync("numero", HttpMethod.Get, $"{Base}/{phoneNumberId}?fields={CamposNumero}", token, null);
            }

            if (falha != null) return MetaResultado<NumeroMeta>.Erro(falha);
            return Ler(texto!, LerNumero);
        }

        internal static NumeroMeta? LerNumero(JsonElement r)
        {
            var id = Texto(r, "id");
            if (id == null) return null;
            bool? noApp = r.TryGetProperty("is_on_biz_app", out var b) && b.ValueKind is JsonValueKind.True or JsonValueKind.False ? b.GetBoolean() : null;
            return new NumeroMeta(id, Texto(r, "display_phone_number"), Texto(r, "verified_name"), Texto(r, "status"),
                Texto(r, "code_verification_status"), Texto(r, "platform_type"), noApp);
        }

        public async Task<MetaResultado<IReadOnlyList<string>>> AppsInscritosAsync(string wabaId, string token)
        {
            var (texto, falha) = await EnviarAsync("subscribed_apps_ler", HttpMethod.Get, $"{Base}/{wabaId}/subscribed_apps", token, null);
            if (falha != null) return MetaResultado<IReadOnlyList<string>>.Erro(falha);

            return Ler<IReadOnlyList<string>>(texto!, r =>
            {
                var ids = new List<string>();
                if (r.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in data.EnumerateArray())
                    {
                        var id = item.TryGetProperty("whatsapp_business_api_data", out var w) ? Texto(w, "id") : Texto(item, "id");
                        if (id != null) ids.Add(id);
                    }
                }

                return ids;
            });
        }

        public Task<MetaResultado<bool>> InscreverAppAsync(string wabaId, string token) =>
            SucessoAsync("subscribed_apps", HttpMethod.Post, $"{Base}/{wabaId}/subscribed_apps", token, new FormUrlEncodedContent(new Dictionary<string, string>()));

        public Task<MetaResultado<bool>> DesinscreverAppAsync(string wabaId, string token) =>
            SucessoAsync("subscribed_apps_remover", HttpMethod.Delete, $"{Base}/{wabaId}/subscribed_apps", token, null);

        public Task<MetaResultado<bool>> RegistrarNumeroAsync(string phoneNumberId, string token, string pin) =>
            SucessoAsync("register", HttpMethod.Post, $"{Base}/{phoneNumberId}/register", token,
                Json(new { messaging_product = "whatsapp", pin }));

        public Task<MetaResultado<bool>> SincronizarAppBusinessAsync(string phoneNumberId, string token, string tipo) =>
            SucessoAsync("smb_app_data", HttpMethod.Post, $"{Base}/{phoneNumberId}/smb_app_data", token,
                Json(new { messaging_product = "whatsapp", sync_type = tipo }));

        public async Task<MetaResultado<IReadOnlyList<AssinaturaApp>>> AssinaturasDoAppAsync(string appId, string appSecret)
        {
            var (texto, falha) = await EnviarAsync("app_subscriptions_ler", HttpMethod.Get, $"{Base}/{appId}/subscriptions", $"{appId}|{appSecret}", null);
            if (falha != null) return MetaResultado<IReadOnlyList<AssinaturaApp>>.Erro(falha);

            return Ler<IReadOnlyList<AssinaturaApp>>(texto!, r =>
            {
                var lista = new List<AssinaturaApp>();
                if (r.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in data.EnumerateArray())
                    {
                        var campos = new List<string>();
                        if (item.TryGetProperty("fields", out var f) && f.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var campo in f.EnumerateArray())
                            {
                                var nome = campo.ValueKind == JsonValueKind.String ? campo.GetString() : Texto(campo, "name");
                                if (nome != null) campos.Add(nome);
                            }
                        }

                        lista.Add(new AssinaturaApp(Texto(item, "object") ?? string.Empty,
                            item.TryGetProperty("active", out var a) && a.ValueKind == JsonValueKind.True, campos));
                    }
                }

                return lista;
            });
        }

        public async Task<MetaResultado<string>> ObterAppAsync(string appId, string appSecret)
        {
            var (texto, falha) = await EnviarAsync("app", HttpMethod.Get, $"{Base}/{appId}?fields=id,name", $"{appId}|{appSecret}", null);
            if (falha != null) return MetaResultado<string>.Erro(falha);
            return Ler(texto!, r => Texto(r, "id") == appId ? Texto(r, "name") ?? appId : null);
        }

        // ---- internos --------------------------------------------------------------------------------------------

        private async Task<MetaResultado<bool>> SucessoAsync(string passo, HttpMethod metodo, string url, string token, HttpContent? corpo)
        {
            var (texto, falha) = await EnviarAsync(passo, metodo, url, token, corpo);
            if (falha != null) return MetaResultado<bool>.Erro(falha);

            try
            {
                using var doc = JsonDocument.Parse(texto!);
                var ok = !doc.RootElement.TryGetProperty("success", out var s) || s.ValueKind == JsonValueKind.True;
                return ok ? MetaResultado<bool>.Sucesso(true) : MetaResultado<bool>.Erro(new MetaFalha(false, 200, null, null, "A Meta respondeu success=false.", null));
            }
            catch (JsonException)
            {
                return MetaResultado<bool>.Sucesso(true);
            }
        }

        /// <param name="bearer">Token (do cliente ou "appId|appSecret"), so no cabecalho.</param>
        private async Task<(string? Corpo, MetaFalha? Falha)> EnviarAsync(string passo, HttpMethod metodo, string url, string? bearer, HttpContent? corpo)
        {
            using var cliente = _http.CreateClient();
            cliente.Timeout = TimeSpan.FromSeconds(15);
            using var pedido = new HttpRequestMessage(metodo, url) { Content = corpo };
            if (bearer != null) pedido.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

            try
            {
                using var resposta = await cliente.SendAsync(pedido);
                var texto = await resposta.Content.ReadAsStringAsync();
                if (resposta.IsSuccessStatusCode) return (texto, null);

                var (codigo, subcodigo, mensagem) = WhatsAppSender.ExtrairErro(texto);
                // So o passo, o HTTP e os codigos: a URL e o corpo podem ter token, codigo de autorizacao ou App Secret.
                _logger.LogWarning("[wa.meta] ev=passo_falhou passo={Passo} http={Http} codigo={Codigo} subcodigo={Subcodigo}",
                    passo, (int)resposta.StatusCode, codigo, subcodigo);
                return (null, new MetaFalha(false, (int)resposta.StatusCode, codigo, subcodigo, mensagem, ExtrairMensagemUsuario(texto)));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                _logger.LogWarning("[wa.meta] ev=passo_sem_resposta passo={Passo} erro={Erro}", passo, ex.GetType().Name);
                return (null, new MetaFalha(true, null, null, null, null, null));
            }
        }

        private static string? ExtrairMensagemUsuario(string corpo)
        {
            try
            {
                using var doc = JsonDocument.Parse(corpo);
                return doc.RootElement.TryGetProperty("error", out var e) ? Texto(e, "error_user_msg") : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static MetaResultado<T> Ler<T>(string texto, Func<JsonElement, T?> ler)
        {
            try
            {
                using var doc = JsonDocument.Parse(texto);
                var valor = ler(doc.RootElement);
                return valor == null
                    ? MetaResultado<T>.Erro(new MetaFalha(false, 200, null, null, "Resposta da Meta sem os dados esperados.", null))
                    : MetaResultado<T>.Sucesso(valor);
            }
            catch (JsonException)
            {
                return MetaResultado<T>.Erro(new MetaFalha(false, 200, null, null, "Resposta invalida da Meta.", null));
            }
        }

        private static StringContent Json(object corpo) => new(JsonSerializer.Serialize(corpo), Encoding.UTF8, "application/json");

        private static string? Texto(JsonElement e, string nome) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(nome, out var v)
                ? v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.GetRawText(), _ => null }
                : null;

        private static List<string> Lista(JsonElement e, string nome)
        {
            var lista = new List<string>();
            if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(nome, out var a) && a.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in a.EnumerateArray())
                {
                    var valor = item.ValueKind switch { JsonValueKind.String => item.GetString(), JsonValueKind.Number => item.GetRawText(), _ => null };
                    if (!string.IsNullOrWhiteSpace(valor)) lista.Add(valor);
                }
            }

            return lista;
        }
    }
}
