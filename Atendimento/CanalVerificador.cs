using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using APIBack.Automation.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace APIBack.Atendimento
{
    public sealed record VerificacaoCanal(bool Ok, string Mensagem, string? NumeroNaMeta, string? NomeVerificado);

    public interface ICanalVerificador
    {
        /// <summary>Pergunta a Meta de quem e o Phone Number ID e confere com o telefone cadastrado.</summary>
        Task<VerificacaoCanal> VerificarAsync(CanalWhatsapp canal);
    }

    /// <summary>
    /// Acaba com o erro de cadastrar um telefone no lugar do ID da Meta: so um ID que a Meta reconhece, e que pertence ao
    /// telefone informado, deixa o canal como "ativo".
    /// </summary>
    public sealed class CanalVerificador : ICanalVerificador
    {
        private readonly IHttpClientFactory _http;
        private readonly ICanalRepository _canais;
        private readonly ITokenProtector _token;
        private readonly IWhatsAppTokenProvider _tokenGlobal;
        private readonly IConfiguration _configuration;
        private readonly ILogger<CanalVerificador> _logger;

        public CanalVerificador(
            IHttpClientFactory http, ICanalRepository canais, ITokenProtector token, IWhatsAppTokenProvider tokenGlobal,
            IConfiguration configuration, ILogger<CanalVerificador> logger)
        {
            _http = http;
            _canais = canais;
            _token = token;
            _tokenGlobal = tokenGlobal;
            _configuration = configuration;
            _logger = logger;
        }

        public async Task<VerificacaoCanal> VerificarAsync(CanalWhatsapp canal)
        {
            var token = _token.Revelar(canal.TokenCifrado);
            var origem = "canal";
            if (string.IsNullOrWhiteSpace(token))
            {
                token = _tokenGlobal.GetAccessToken();
                origem = "global";
            }

            if (string.IsNullOrWhiteSpace(token) || token.StartsWith("__", StringComparison.Ordinal))
            {
                var semToken = new VerificacaoCanal(false, "Este numero ainda nao tem token da Meta: cadastre o token para poder verificar.", null, null);
                await Registrar(canal, semToken, "sem_token");
                return semToken;
            }

            var versao = _configuration["WhatsApp:GraphApiVersion"] ?? "v23.0";
            var url = $"https://graph.facebook.com/{versao}/{canal.PhoneNumberId}?fields=display_phone_number,verified_name";

            using var cliente = _http.CreateClient();
            cliente.Timeout = TimeSpan.FromSeconds(10);
            cliente.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            VerificacaoCanal resultado;
            try
            {
                using var resposta = await cliente.GetAsync(url);
                resultado = Avaliar(canal, (int)resposta.StatusCode, await resposta.Content.ReadAsStringAsync());
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // Rede fora: nao diz nada sobre o cadastro, entao nao marca erro no canal.
                _logger.LogWarning(ex, "[canal] ev=verificacao_sem_resposta canal={Canal}", canal.Id);
                return new VerificacaoCanal(false, "Nao foi possivel falar com a Meta agora. Tente de novo em instantes.", null, null);
            }

            await Registrar(canal, resultado, origem);
            return resultado;
        }

        private async Task Registrar(CanalWhatsapp canal, VerificacaoCanal resultado, string origemToken)
        {
            await _canais.MarcarVerificadoAsync(canal.Id, resultado.Ok, resultado.Ok ? null : resultado.Mensagem);
            _logger.LogInformation(
                "[canal] ev=verificado canal={Canal} loja={Loja} ok={Ok} token={Token} numero_meta={NumeroMeta} motivo=\"{Motivo}\"",
                canal.Id, canal.IdEstabelecimento, resultado.Ok, origemToken, resultado.NumeroNaMeta, resultado.Ok ? "" : resultado.Mensagem);
        }

        /// <summary>Interpreta a resposta da Meta (separado da rede para ser testado).</summary>
        internal static VerificacaoCanal Avaliar(CanalWhatsapp canal, int http, string corpo)
        {
            try
            {
                using var doc = JsonDocument.Parse(corpo);
                var raiz = doc.RootElement;

                if (http >= 200 && http < 300)
                {
                    var numero = raiz.TryGetProperty("display_phone_number", out var n) ? n.GetString() : null;
                    var nome = raiz.TryGetProperty("verified_name", out var v) ? v.GetString() : null;
                    var digitosMeta = Digitos(numero);
                    var digitosCadastro = Digitos(canal.NumeroE164);

                    if (digitosMeta.Length > 0 && digitosMeta != digitosCadastro)
                    {
                        return new VerificacaoCanal(false,
                            $"O ID da Meta pertence ao numero {numero}, mas o cadastro diz {canal.NumeroE164}. Corrija o ID ou o telefone.", numero, nome);
                    }

                    return new VerificacaoCanal(true, "Numero verificado na Meta.", numero, nome);
                }

                var (codigo, subcodigo, mensagem) = Automation.Services.WhatsAppSender.ExtrairErro(corpo);
                var motivo = (codigo, subcodigo) switch
                {
                    (100, 33) => "A Meta nao conhece este Phone Number ID (ou o token nao tem acesso a ele). Confira o ID no WhatsApp Manager.",
                    (190, _) => "O token da Meta e invalido ou expirou.",
                    (10, _) or (200, _) => "O token nao tem permissao para este numero.",
                    _ => $"A Meta recusou a consulta (HTTP {http}): {mensagem ?? "sem detalhe"}"
                };
                return new VerificacaoCanal(false, motivo, null, null);
            }
            catch (JsonException)
            {
                return new VerificacaoCanal(false, $"Resposta inesperada da Meta (HTTP {http}).", null, null);
            }
        }

        private static string Digitos(string? valor) => new((valor ?? string.Empty).Where(char.IsDigit).ToArray());
    }
}
