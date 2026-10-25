using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace APIBack.Atendimento.Templates
{
    /// <summary>Falha de uma chamada a Graph API. <see cref="SemResposta"/> = rede/timeout: nao se sabe o que a Meta fez.</summary>
    public sealed record MetaFalha(bool SemResposta, int? Http, int? Codigo, int? Subcodigo, string? Mensagem, string? MensagemUsuario);

    public sealed record MetaResultado<T>(T? Valor, MetaFalha? Falha)
    {
        public bool Ok => Falha == null;
        public static MetaResultado<T> Sucesso(T valor) => new(valor, null);
        public static MetaResultado<T> Erro(MetaFalha falha) => new(default, falha);
    }

    /// <summary>Um modelo como a Meta devolve (so os campos que o Zippy usa). Components e uma copia do JSON da Meta.</summary>
    public sealed record MetaTemplate(
        string Id, string Nome, string Idioma, string? Status, string? Categoria, string? MotivoRejeicao, string? Qualidade, JsonElement? Componentes);

    /// <param name="Completa">Todas as paginas foram lidas (so assim um modelo ausente pode ser dado como excluido).</param>
    public sealed record MetaListaTemplates(IReadOnlyList<MetaTemplate> Itens, bool Completa);

    public sealed record MetaCriacao(string Id, string? Status, string? Categoria);

    /// <summary>
    /// Gerenciamento de modelos na Graph API (permissao whatsapp_business_management). Opera sempre pela WABA, nunca pelo Phone Number ID.
    /// O token chega do backend (CanalToken), nunca do navegador, e so vai no cabecalho Authorization.
    /// </summary>
    public interface IMetaTemplatesApi
    {
        /// <summary>IDs dos numeros da WABA (GET /{waba}/phone_numbers, todas as paginas): confirma que o numero e desta conta.</summary>
        Task<MetaResultado<IReadOnlyList<string>>> ListarNumerosAsync(string wabaId, string token);
        /// <summary>Modelos da WABA (GET /{waba}/message_templates, todas as paginas). <paramref name="nome"/> filtra na Meta.</summary>
        Task<MetaResultado<MetaListaTemplates>> ListarAsync(string wabaId, string token, string? nome = null);
        Task<MetaResultado<MetaTemplate>> ObterAsync(string templateId, string token);
        /// <summary>POST /{waba}/message_templates. Nunca repete sozinho: sem resposta, quem chama confere na Meta antes de reenviar.</summary>
        Task<MetaResultado<MetaCriacao>> CriarAsync(string wabaId, string token, object payload);
        /// <summary>DELETE /{waba}/message_templates?hsm_id&amp;name: apaga SO este modelo (este idioma), nunca todos os de mesmo nome.</summary>
        Task<MetaResultado<bool>> ExcluirAsync(string wabaId, string token, string nome, string templateId);
    }

    public sealed class MetaTemplatesApi : IMetaTemplatesApi
    {
        private const string Campos = "id,name,language,status,category,rejected_reason,quality_score,components";
        internal const int PaginasMax = 50;

        private readonly IHttpClientFactory _http;
        private readonly IConfiguration _configuration;
        private readonly ILogger<MetaTemplatesApi> _logger;

        /// <summary>Espera entre tentativas de leitura (trocada nos testes).</summary>
        internal Func<TimeSpan, Task> Esperar { get; set; } = Task.Delay;

        public MetaTemplatesApi(IHttpClientFactory http, IConfiguration configuration, ILogger<MetaTemplatesApi> logger)
        {
            _http = http;
            _configuration = configuration;
            _logger = logger;
        }

        private string Base => $"https://graph.facebook.com/{_configuration["WhatsApp:GraphApiVersion"] ?? "v23.0"}";

        public async Task<MetaResultado<IReadOnlyList<string>>> ListarNumerosAsync(string wabaId, string token)
        {
            var ids = new List<string>();
            var (falha, _) = await PaginarAsync($"{Base}/{wabaId}/phone_numbers?fields=id&limit=100", token, "phone_numbers", item =>
            {
                if (Texto(item, "id") is { } id) ids.Add(id);
            });
            return falha != null ? MetaResultado<IReadOnlyList<string>>.Erro(falha) : MetaResultado<IReadOnlyList<string>>.Sucesso(ids);
        }

        public async Task<MetaResultado<MetaListaTemplates>> ListarAsync(string wabaId, string token, string? nome = null)
        {
            var url = $"{Base}/{wabaId}/message_templates?fields={Campos}&limit=100";
            if (!string.IsNullOrWhiteSpace(nome)) url += "&name=" + Uri.EscapeDataString(nome);

            var itens = new List<MetaTemplate>();
            var (falha, completa) = await PaginarAsync(url, token, "message_templates", item =>
            {
                if (Ler(item) is { } t) itens.Add(t);
            });
            return falha != null ? MetaResultado<MetaListaTemplates>.Erro(falha) : MetaResultado<MetaListaTemplates>.Sucesso(new MetaListaTemplates(itens, completa));
        }

        public async Task<MetaResultado<MetaTemplate>> ObterAsync(string templateId, string token)
        {
            var (corpo, falha) = await GetAsync($"{Base}/{templateId}?fields={Campos}", token, "template");
            if (falha != null) return MetaResultado<MetaTemplate>.Erro(falha);

            using var doc = JsonDocument.Parse(corpo!);
            return Ler(doc.RootElement) is { } t
                ? MetaResultado<MetaTemplate>.Sucesso(t)
                : MetaResultado<MetaTemplate>.Erro(new MetaFalha(false, 200, null, null, "Resposta sem id.", null));
        }

        public async Task<MetaResultado<MetaCriacao>> CriarAsync(string wabaId, string token, object payload)
        {
            using var cliente = Cliente(token);
            using var conteudo = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            try
            {
                using var resposta = await cliente.PostAsync($"{Base}/{wabaId}/message_templates", conteudo);
                var corpo = await resposta.Content.ReadAsStringAsync();
                if (!resposta.IsSuccessStatusCode) return MetaResultado<MetaCriacao>.Erro(Falha("criar", (int)resposta.StatusCode, corpo));

                using var doc = JsonDocument.Parse(corpo);
                var id = Texto(doc.RootElement, "id");
                if (string.IsNullOrWhiteSpace(id))
                {
                    // 2xx sem id: a Meta pode ter criado. Tratado como sem resposta para quem chama conferir.
                    return MetaResultado<MetaCriacao>.Erro(new MetaFalha(true, (int)resposta.StatusCode, null, null, "Resposta sem id.", null));
                }

                _logger.LogInformation("[wa.tpl] ev=meta_criou waba={Waba} template={Template}", wabaId, id);
                return MetaResultado<MetaCriacao>.Sucesso(new MetaCriacao(id!, Texto(doc.RootElement, "status"), Texto(doc.RootElement, "category")));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                _logger.LogWarning("[wa.tpl] ev=meta_sem_resposta op=criar waba={Waba} erro={Erro}", wabaId, ex.GetType().Name);
                return MetaResultado<MetaCriacao>.Erro(new MetaFalha(true, null, null, null, null, null));
            }
        }

        public async Task<MetaResultado<bool>> ExcluirAsync(string wabaId, string token, string nome, string templateId)
        {
            using var cliente = Cliente(token);
            var url = $"{Base}/{wabaId}/message_templates?hsm_id={Uri.EscapeDataString(templateId)}&name={Uri.EscapeDataString(nome)}";
            try
            {
                using var resposta = await cliente.DeleteAsync(url);
                var corpo = await resposta.Content.ReadAsStringAsync();
                return resposta.IsSuccessStatusCode
                    ? MetaResultado<bool>.Sucesso(true)
                    : MetaResultado<bool>.Erro(Falha("excluir", (int)resposta.StatusCode, corpo));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                _logger.LogWarning("[wa.tpl] ev=meta_sem_resposta op=excluir waba={Waba} erro={Erro}", wabaId, ex.GetType().Name);
                return MetaResultado<bool>.Erro(new MetaFalha(true, null, null, null, null, null));
            }
        }

        // ---- internos --------------------------------------------------------------------------------------

        /// <summary>
        /// Le todas as paginas montando a URL pelo cursor "after" (o "next" da Meta nao e seguido: assim o token so vai para
        /// graph.facebook.com). Para em <see cref="PaginasMax"/> paginas e devolve se leu tudo.
        /// </summary>
        private async Task<(MetaFalha? Falha, bool Completa)> PaginarAsync(string url, string token, string op, Action<JsonElement> item)
        {
            string? after = null;
            for (var pagina = 0; pagina < PaginasMax; pagina++)
            {
                var (corpo, falha) = await GetAsync(after == null ? url : url + "&after=" + Uri.EscapeDataString(after), token, op);
                if (falha != null) return (falha, false);

                using var doc = JsonDocument.Parse(corpo!);
                if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                {
                    foreach (var e in data.EnumerateArray()) item(e);
                }

                var temProxima = doc.RootElement.TryGetProperty("paging", out var paging) && paging.TryGetProperty("next", out _);
                after = temProxima && paging.TryGetProperty("cursors", out var cursores) ? Texto(cursores, "after") : null;
                if (after == null) return (null, true);
            }

            _logger.LogWarning("[wa.tpl] ev=paginas_demais op={Op} max={Max}", op, PaginasMax);
            return (null, false);
        }

        /// <summary>GET com ate 3 tentativas para rede/5xx (leitura e segura de repetir). 4xx e 429 nao se repetem.</summary>
        private async Task<(string? Corpo, MetaFalha? Falha)> GetAsync(string url, string token, string op)
        {
            var esperas = new[] { TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(1500) };
            MetaFalha? ultima = null;
            for (var tentativa = 0; tentativa <= esperas.Length; tentativa++)
            {
                if (tentativa > 0) await Esperar(esperas[tentativa - 1]);

                using var cliente = Cliente(token);
                try
                {
                    using var resposta = await cliente.GetAsync(url);
                    var corpo = await resposta.Content.ReadAsStringAsync();
                    if (resposta.IsSuccessStatusCode)
                    {
                        try { using var _ = JsonDocument.Parse(corpo); }
                        catch (JsonException) { ultima = new MetaFalha(false, (int)resposta.StatusCode, null, null, "Resposta invalida.", null); continue; }
                        return (corpo, null);
                    }

                    ultima = Falha(op, (int)resposta.StatusCode, corpo);
                    if ((int)resposta.StatusCode < 500) return (null, ultima);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    _logger.LogWarning("[wa.tpl] ev=meta_sem_resposta op={Op} tentativa={Tentativa} erro={Erro}", op, tentativa + 1, ex.GetType().Name);
                    ultima = new MetaFalha(true, null, null, null, null, null);
                }
            }

            return (null, ultima);
        }

        private HttpClient Cliente(string token)
        {
            var cliente = _http.CreateClient("meta-templates");
            cliente.Timeout = TimeSpan.FromSeconds(15);
            cliente.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return cliente;
        }

        private MetaFalha Falha(string op, int http, string corpo)
        {
            var (codigo, subcodigo, mensagem, mensagemUsuario) = ExtrairErro(corpo);
            _logger.LogWarning("[wa.tpl] ev=meta_recusou op={Op} http={Http} codigo={Codigo} subcodigo={Subcodigo} motivo=\"{Motivo}\"",
                op, http, codigo, subcodigo, mensagem);
            return new MetaFalha(false, http, codigo, subcodigo, mensagem, mensagemUsuario);
        }

        /// <summary>code, error_subcode, message e error_user_msg (texto que a Meta escreve para o usuario final).</summary>
        internal static (int? Codigo, int? Subcodigo, string? Mensagem, string? MensagemUsuario) ExtrairErro(string corpo)
        {
            try
            {
                using var doc = JsonDocument.Parse(corpo);
                if (!doc.RootElement.TryGetProperty("error", out var erro)) return (null, null, null, null);
                int? Inteiro(string nome) => erro.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
                return (Inteiro("code"), Inteiro("error_subcode"), Texto(erro, "message"), Texto(erro, "error_user_msg"));
            }
            catch (JsonException)
            {
                return (null, null, null, null);
            }
        }

        internal static MetaTemplate? Ler(JsonElement e)
        {
            var id = Texto(e, "id");
            if (string.IsNullOrWhiteSpace(id)) return null;
            JsonElement? componentes = e.TryGetProperty("components", out var c) ? c.Clone() : null;
            return new MetaTemplate(id!, Texto(e, "name") ?? string.Empty, Texto(e, "language") ?? string.Empty, Texto(e, "status"),
                Texto(e, "category"), Texto(e, "rejected_reason"), QualidadeDe(e), componentes);
        }

        private static string? QualidadeDe(JsonElement e) =>
            e.TryGetProperty("quality_score", out var q) && q.ValueKind == JsonValueKind.Object ? Texto(q, "score") : null;

        private static string? Texto(JsonElement e, string nome) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
}
