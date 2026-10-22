using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using APIBack.Automation.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace APIBack.Atendimento
{
    /// <param name="Erro">Primeiro erro explicado; nulo quando os dois passos deram certo.</param>
    public sealed record ResultadoRegistro(bool Ok, WhatsappErro? Erro);

    /// <summary>Registra, na Graph API, o webhook de um numero proprio no app do cliente.</summary>
    public interface IWebhookRegistrador
    {
        /// <summary>
        /// 1) POST /{app-id}/subscriptions (token de app "appId|appSecret"): URL de retorno e token de verificacao do app do cliente,
        /// objeto whatsapp_business_account, campo messages. 2) POST /{waba-id}/subscribed_apps (token do cliente): liga o app a conta.
        /// Os dois sao tentados mesmo que o primeiro falhe, para o registro manual ter o que falta so de um lado.
        /// </summary>
        Task<ResultadoRegistro> RegistrarAsync(string appId, string appSecret, string wabaId, string token, string callbackUrl, string verifyToken);
    }

    public sealed class MetaWebhookRegistrador : IWebhookRegistrador
    {
        private readonly IHttpClientFactory _http;
        private readonly IConfiguration _configuration;
        private readonly ILogger<MetaWebhookRegistrador> _logger;

        public MetaWebhookRegistrador(IHttpClientFactory http, IConfiguration configuration, ILogger<MetaWebhookRegistrador> logger)
        {
            _http = http;
            _configuration = configuration;
            _logger = logger;
        }

        public async Task<ResultadoRegistro> RegistrarAsync(
            string appId, string appSecret, string wabaId, string token, string callbackUrl, string verifyToken)
        {
            var versao = _configuration["WhatsApp:GraphApiVersion"] ?? "v23.0";

            // O token de app vai no corpo do POST (nunca na URL): URL acaba em log de proxy; corpo, nao.
            var assinatura = await PostAsync("subscriptions", ChamadaMeta.AssinaturaDoApp,
                $"https://graph.facebook.com/{versao}/{appId}/subscriptions",
                bearer: null,
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["object"] = "whatsapp_business_account",
                    ["callback_url"] = callbackUrl,
                    ["fields"] = "messages",
                    ["verify_token"] = verifyToken,
                    ["access_token"] = $"{appId}|{appSecret}"
                }));

            var conta = await PostAsync("subscribed_apps", ChamadaMeta.AppDaConta,
                $"https://graph.facebook.com/{versao}/{wabaId}/subscribed_apps",
                bearer: token,
                new FormUrlEncodedContent(new Dictionary<string, string>()));

            var erro = assinatura ?? conta;
            return new ResultadoRegistro(erro == null, erro);
        }

        /// <returns>O erro explicado, ou nulo quando a Meta respondeu sucesso.</returns>
        private async Task<WhatsappErro?> PostAsync(string passo, ChamadaMeta chamada, string url, string? bearer, HttpContent corpo)
        {
            using var cliente = _http.CreateClient();
            cliente.Timeout = TimeSpan.FromSeconds(15);
            if (bearer != null) cliente.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

            try
            {
                using var resposta = await cliente.PostAsync(url, corpo);
                var texto = await resposta.Content.ReadAsStringAsync();
                if (resposta.IsSuccessStatusCode)
                {
                    _logger.LogInformation("[canal] ev=webhook_passo_ok passo={Passo}", passo);
                    return null;
                }

                var (codigo, subcodigo, mensagem) = WhatsAppSender.ExtrairErro(texto);
                _logger.LogWarning(
                    "[canal] ev=webhook_passo_falhou passo={Passo} http={Http} codigo={Codigo} subcodigo={Subcodigo} motivo=\"{Motivo}\"",
                    passo, (int)resposta.StatusCode, codigo, subcodigo, mensagem);
                return WhatsappErros.DaMeta(chamada, (int)resposta.StatusCode, codigo, subcodigo, mensagem);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                _logger.LogWarning("[canal] ev=webhook_passo_sem_resposta passo={Passo} erro={Erro}", passo, ex.GetType().Name);
                return WhatsappErros.MetaIndisponivel();
            }
        }
    }
}
