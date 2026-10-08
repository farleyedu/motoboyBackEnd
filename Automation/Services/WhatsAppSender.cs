// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using APIBack.Atendimento;
using APIBack.Automation.Dtos;
using APIBack.Automation.Helpers;
using APIBack.Automation.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace APIBack.Automation.Services
{
    /// <summary>
    /// Envio pela Cloud API da Meta. O token e o ID do numero vem do canal (um token por numero); o token global de
    /// configuracao so e usado, com aviso no log, enquanto o numero ainda nao tem token proprio. Cada envio deixa
    /// linhas [wa.out] no log; falha permanente (ID inexistente, token invalido) marca o canal como "erro".
    /// </summary>
    public class WhatsAppSender
    {
        private readonly IHttpClientFactory _httpFactory;
        private readonly IWhatsAppTokenProvider _tokenProvider;
        private readonly ICanalRepository _canais;
        private readonly ITokenProtector _tokenProtector;
        private readonly IMessageService _mensagens;
        private readonly IConfiguration _configuration;
        private readonly ILogger<WhatsAppSender> _logger;
        private readonly APIBack.Service.ISimulatedCustomerGuard _simulatedGuard;

        public WhatsAppSender(
            IHttpClientFactory httpFactory,
            IWhatsAppTokenProvider tokenProvider,
            ICanalRepository canais,
            ITokenProtector tokenProtector,
            IMessageService mensagens,
            IConfiguration configuration,
            ILogger<WhatsAppSender> logger,
            APIBack.Service.ISimulatedCustomerGuard simulatedGuard)
        {
            _simulatedGuard = simulatedGuard;
            _httpFactory = httpFactory;
            _tokenProvider = tokenProvider;
            _canais = canais;
            _tokenProtector = tokenProtector;
            _mensagens = mensagens;
            _configuration = configuration;
            _logger = logger;
        }

        /// <returns>O id da mensagem na Meta (wamid), ou null quando nada foi enviado (texto vazio, cliente de teste).</returns>
        public Task<string?> SendTextAsync(Guid idConversa, string phoneNumberId, string numeroDestino, string texto, string? displayPhone = null, Guid? idMensagem = null)
        {
            if (string.IsNullOrWhiteSpace(texto))
            {
                return Task.FromResult<string?>(null);
            }

            var payload = new
            {
                messaging_product = "whatsapp",
                to = TelefoneHelper.NormalizeBrazilianForWhatsappTo(numeroDestino),
                type = "text",
                text = new { body = texto }
            };

            return SendPayloadAsync(idConversa, phoneNumberId, payload, "text", idMensagem);
        }

        public Task<string?> SendImageAsync(Guid idConversa, string phoneNumberId, string numeroDestino, string imageUrl, string? displayPhone = null, Guid? idMensagem = null)
        {
            var payload = new
            {
                messaging_product = "whatsapp",
                to = TelefoneHelper.NormalizeBrazilianForWhatsappTo(numeroDestino),
                type = "image",
                image = new { link = imageUrl }
            };

            return SendPayloadAsync(idConversa, phoneNumberId, payload, "image", idMensagem);
        }

        public Task<string?> SendDocumentAsync(Guid idConversa, string phoneNumberId, string numeroDestino, string documentUrl, string? filename, string? displayPhone = null, Guid? idMensagem = null)
        {
            var payload = new
            {
                messaging_product = "whatsapp",
                to = TelefoneHelper.NormalizeBrazilianForWhatsappTo(numeroDestino),
                type = "document",
                document = new { link = documentUrl, filename = string.IsNullOrWhiteSpace(filename) ? null : filename }
            };

            return SendPayloadAsync(idConversa, phoneNumberId, payload, "document", idMensagem);
        }

        public Task<string?> SendReplyButtonsAsync(
            Guid idConversa,
            string phoneNumberId,
            string numeroDestino,
            string bodyText,
            IReadOnlyCollection<WhatsAppReplyButtonOption> options,
            string? displayPhone = null,
            Guid? idMensagem = null)
        {
            if (string.IsNullOrWhiteSpace(bodyText))
            {
                return Task.FromResult<string?>(null);
            }

            var buttons = (options ?? Array.Empty<WhatsAppReplyButtonOption>())
                .Where(option => option != null
                    && !string.IsNullOrWhiteSpace(option.Id)
                    && !string.IsNullOrWhiteSpace(option.Title))
                .Take(3)
                .Select(option => new
                {
                    type = "reply",
                    reply = new
                    {
                        id = option.Id.Trim(),
                        title = option.Title.Trim()
                    }
                })
                .ToArray();

            if (buttons.Length == 0)
            {
                return SendTextAsync(idConversa, phoneNumberId, numeroDestino, bodyText, displayPhone, idMensagem);
            }

            var payload = new
            {
                messaging_product = "whatsapp",
                to = TelefoneHelper.NormalizeBrazilianForWhatsappTo(numeroDestino),
                type = "interactive",
                interactive = new
                {
                    type = "button",
                    body = new { text = bodyText },
                    action = new { buttons }
                }
            };

            return SendPayloadAsync(idConversa, phoneNumberId, payload, "interactive", idMensagem);
        }

        /// <summary>Erro da Meta que nao adianta repetir: ID do numero inexistente (100/33), token invalido (190) ou sem permissao (10, 200).</summary>
        internal static bool ErroPermanenteDoCanal(int? codigo, int? subcodigo) =>
            codigo is 190 or 10 or 200 || (codigo == 100 && subcodigo == 33);

        public async Task<string?> SendOperationalAsync(Guid conversationId, string phoneNumberId, string destination, string body, byte[]? media, string? contentType, Guid messageId, string? replyProviderId)
        {
            if (await _simulatedGuard.IsSimulatedConversationAsync(conversationId)) return null;
            var payload = new Dictionary<string, object?> { ["messaging_product"] = "whatsapp", ["to"] = TelefoneHelper.NormalizeBrazilianForWhatsappTo(destination) };
            var type = media == null ? "text" : contentType!.StartsWith("image/") ? "image" : "audio";
            payload["type"] = type;
            if (!string.IsNullOrWhiteSpace(replyProviderId)) payload["context"] = new { message_id = replyProviderId };
            if (media == null) payload["text"] = new { body };
            else
            {
                var canal = await _canais.ObterAtivoPorPhoneNumberIdAsync(phoneNumberId);
                var token = _tokenProtector.Revelar(canal?.TokenCifrado);
                if (string.IsNullOrWhiteSpace(token)) token = _tokenProvider.GetAccessToken();
                if (string.IsNullOrWhiteSpace(token) || token.StartsWith("__")) throw new HttpRequestException("Token do WhatsApp nao configurado.");
                using var client = _httpFactory.CreateClient();
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",token);
                using var form = new MultipartFormDataContent();
                form.Add(new StringContent("whatsapp"),"messaging_product");
                form.Add(new StringContent(contentType!),"type");
                var bytes = new ByteArrayContent(media); bytes.Headers.ContentType = new MediaTypeHeaderValue(contentType!);
                form.Add(bytes,"file",type=="image"?"photo.jpg":"audio.m4a");
                var version = _configuration["WhatsApp:GraphApiVersion"] ?? "v23.0";
                using var response = await client.PostAsync($"https://graph.facebook.com/{version}/{phoneNumberId}/media",form);
                response.EnsureSuccessStatusCode();
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var id = json.RootElement.GetProperty("id").GetString();
                payload[type] = type=="image" ? new Dictionary<string,object?>{["id"]=id,["caption"]=body} : new Dictionary<string,object?>{["id"]=id};
            }
            return await SendPayloadAsync(conversationId,phoneNumberId,payload,type,messageId,singleAttempt:true);
        }

        public Task<string?> SendOperationalReactionAsync(Guid conversation,string phone,string destination,string providerId,string emoji) =>
            SendPayloadAsync(conversation,phone,new{messaging_product="whatsapp",to=TelefoneHelper.NormalizeBrazilianForWhatsappTo(destination),type="reaction",reaction=new{message_id=providerId,emoji}},"reaction",null,singleAttempt:true);

        internal static bool AllowedMediaUrl(Uri uri) => uri.Scheme=="https" && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo)
            && (uri.Host=="lookaside.fbsbx.com" || uri.Host.EndsWith(".fbcdn.net",StringComparison.OrdinalIgnoreCase) || uri.Host=="graph.facebook.com");
        public async Task<(byte[] Content,string Type)> DownloadOperationalMediaAsync(Guid est,string phone,string mediaId,CancellationToken ct)
        {
            var canal=await _canais.ObterAtivoPorPhoneNumberIdAsync(phone);
            if(canal?.IdEstabelecimento!=est||!mediaId.All(char.IsAsciiDigit)||mediaId.Length is <1 or >100)
                throw new APIBack.Service.DeliveryDomainException(404,"CHAT_ATTACHMENT_INVALID","Anexo indisponivel nesta loja.");
            var token=_tokenProtector.Revelar(canal.TokenCifrado);
            if(string.IsNullOrWhiteSpace(token))token=_tokenProvider.GetAccessToken();
            if(string.IsNullOrWhiteSpace(token)||token.StartsWith("__"))throw new APIBack.Service.DeliveryDomainException(503,"CLIENT_MEDIA_UNAVAILABLE","WhatsApp da loja indisponivel.");
            using var client=_httpFactory.CreateClient("chat-media");
            client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",token);
            var version=_configuration["WhatsApp:GraphApiVersion"]??"v23.0";
            try
            {
                using var metadata=await client.GetAsync($"https://graph.facebook.com/{version}/{mediaId}?phone_number_id={Uri.EscapeDataString(phone)}",ct);metadata.EnsureSuccessStatusCode();
                using var json=JsonDocument.Parse(await metadata.Content.ReadAsStringAsync(ct));
                if(!Uri.TryCreate(json.RootElement.GetProperty("url").GetString(),UriKind.Absolute,out var url)||!AllowedMediaUrl(url))throw new HttpRequestException("URL de midia nao permitida.");
                using var response=await client.GetAsync(url,HttpCompletionOption.ResponseHeadersRead,ct);response.EnsureSuccessStatusCode();
                if(response.Content.Headers.ContentLength>10485760)throw new APIBack.Service.DeliveryDomainException(422,"CHAT_FILE_INVALID","Arquivo deve ter ate 10 MB.");
                await using var stream=await response.Content.ReadAsStreamAsync(ct);using var buffer=new System.IO.MemoryStream();var chunk=new byte[8192];int count;
                while((count=await stream.ReadAsync(chunk,ct))>0){if(buffer.Length+count>10485760)throw new APIBack.Service.DeliveryDomainException(422,"CHAT_FILE_INVALID","Arquivo deve ter ate 10 MB.");await buffer.WriteAsync(chunk.AsMemory(0,count),ct);}
                var bytes=buffer.ToArray();var type=APIBack.Service.CommunicationService.DetectType(bytes);
                if(type==null)throw new APIBack.Service.DeliveryDomainException(422,"CHAT_FILE_INVALID","Midia recebida em formato nao suportado.");
                return(bytes,type);
            }
            catch(HttpRequestException){throw new APIBack.Service.DeliveryDomainException(502,"CLIENT_MEDIA_UNAVAILABLE","Nao foi possivel abrir a midia no WhatsApp. Tente novamente.");}
        }
        private async Task<string?> SendPayloadAsync(Guid idConversa, string phoneNumberId, object payload, string payloadType, Guid? idMensagem, bool singleAttempt = false)
        {
            // Cliente de teste (simulador): a conversa e gravada normalmente, mas nada sai para o WhatsApp de verdade.
            if (await _simulatedGuard.IsSimulatedConversationAsync(idConversa))
            {
                _logger.LogInformation("[wa.out] ev=suprimida motivo=cliente_de_teste conversa={Conversa} tipo={Tipo}", idConversa, payloadType);
                return null;
            }

            var canal = await _canais.ObterAtivoPorPhoneNumberIdAsync(new string((phoneNumberId ?? string.Empty).Where(char.IsDigit).ToArray()));
            var token = _tokenProtector.Revelar(canal?.TokenCifrado);
            var origemToken = "canal";
            if (string.IsNullOrWhiteSpace(token))
            {
                token = _tokenProvider.GetAccessToken();
                origemToken = "global";
                if (!string.IsNullOrWhiteSpace(token) && !token.StartsWith("__", StringComparison.Ordinal))
                {
                    _logger.LogWarning(
                        "[wa.out] ev=token_global conversa={Conversa} canal={Canal} dica=\"cadastre o token deste numero na Gestao\"",
                        idConversa, canal?.Id);
                }
                else
                {
                    token = null;
                }
            }

            if (string.IsNullOrWhiteSpace(token))
            {
                var semToken = "Nenhum token da Meta configurado para este numero.";
                _logger.LogError("[wa.out] ev=falhou motivo=sem_token conversa={Conversa} canal={Canal} phone_number_id={PhoneNumberId}", idConversa, canal?.Id, phoneNumberId);
                if (canal != null) await _canais.MarcarErroAsync(canal.Id, semToken, desativar: false);
                throw new HttpRequestException($"WhatsApp: {semToken}");
            }

            var client = _httpFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var graphVersion = _configuration["WhatsApp:GraphApiVersion"] ?? "v23.0";
            var endpoint = $"https://graph.facebook.com/{graphVersion}/{phoneNumberId}/messages";
            var json = JsonSerializer.Serialize(payload);

            var esperas = singleAttempt ? Array.Empty<TimeSpan>() : new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) };
            string? ultimoCorpo = null;
            int? ultimoStatus = null;
            int? ultimoCodigo = null;
            int? ultimoSubcodigo = null;
            string? ultimaMensagem = null;

            for (var tentativa = 1; tentativa <= esperas.Length + 1; tentativa++)
            {
                var relogio = Stopwatch.StartNew();
                using var content = new StringContent(json, Encoding.UTF8, "application/json");

                try
                {
                    using var resposta = await client.PostAsync(endpoint, content);
                    var corpo = await resposta.Content.ReadAsStringAsync();
                    relogio.Stop();

                    if (resposta.IsSuccessStatusCode)
                    {
                        var wamid = ExtrairWamid(corpo);
                        _logger.LogInformation(
                            "[wa.out] ev=enviada conversa={Conversa} canal={Canal} wa={Wa} tipo={Tipo} tentativa={Tentativa} token={Token} ms={Ms}",
                            idConversa, canal?.Id, wamid, payloadType, tentativa, origemToken, relogio.ElapsedMilliseconds);

                        if (canal != null) await _canais.MarcarEnvioOkAsync(canal.Id);
                        if (idMensagem.HasValue && !string.IsNullOrWhiteSpace(wamid))
                        {
                            try { await _mensagens.VincularProvedorAsync(idMensagem.Value, wamid); }
                            catch (Exception ex) { _logger.LogWarning(ex, "[wa.out] ev=sem_vinculo conversa={Conversa} mensagem={Mensagem}", idConversa, idMensagem); }
                        }

                        return wamid;
                    }

                    ultimoStatus = (int)resposta.StatusCode;
                    ultimoCorpo = corpo;
                    (ultimoCodigo, ultimoSubcodigo, ultimaMensagem) = ExtrairErro(corpo);
                    _logger.LogWarning(
                        "[wa.out] ev=tentativa_falhou conversa={Conversa} canal={Canal} tentativa={Tentativa} http={Http} codigo={Codigo} subcodigo={Subcodigo} motivo=\"{Motivo}\" ms={Ms}",
                        idConversa, canal?.Id, tentativa, ultimoStatus, ultimoCodigo, ultimoSubcodigo, ultimaMensagem, relogio.ElapsedMilliseconds);

                    // 4xx (menos 429) e erro do pedido, nao do servidor: repetir nao muda nada.
                    if (ultimoStatus is >= 400 and < 500 and not 429) break;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    ultimoCorpo = ex.Message;
                    _logger.LogWarning(ex, "[wa.out] ev=tentativa_falhou conversa={Conversa} canal={Canal} tentativa={Tentativa} motivo=rede", idConversa, canal?.Id, tentativa);
                }

                if (tentativa <= esperas.Length)
                {
                    await Task.Delay(esperas[tentativa - 1]);
                }
            }

            var motivo = ultimaMensagem ?? ultimoCorpo ?? "sem resposta";
            var permanente = ErroPermanenteDoCanal(ultimoCodigo, ultimoSubcodigo);
            _logger.LogError(
                "[wa.out] ev=falhou conversa={Conversa} canal={Canal} phone_number_id={PhoneNumberId} http={Http} codigo={Codigo} subcodigo={Subcodigo} permanente={Permanente} motivo=\"{Motivo}\"",
                idConversa, canal?.Id, phoneNumberId, ultimoStatus, ultimoCodigo, ultimoSubcodigo, permanente, motivo);

            if (canal != null) await _canais.MarcarErroAsync(canal.Id, $"{ultimoCodigo}/{ultimoSubcodigo}: {motivo}", desativar: permanente);

            throw new HttpRequestException($"WhatsApp API retornou erro {ultimoStatus}: {ultimoCorpo}");
        }

        internal static string? ExtrairWamid(string corpo)
        {
            try
            {
                using var doc = JsonDocument.Parse(corpo);
                return doc.RootElement.TryGetProperty("messages", out var mensagens) && mensagens.ValueKind == JsonValueKind.Array && mensagens.GetArrayLength() > 0
                    ? mensagens[0].TryGetProperty("id", out var id) ? id.GetString() : null
                    : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        internal static (int? Codigo, int? Subcodigo, string? Mensagem) ExtrairErro(string corpo)
        {
            try
            {
                using var doc = JsonDocument.Parse(corpo);
                if (!doc.RootElement.TryGetProperty("error", out var erro)) return (null, null, null);

                int? Inteiro(string nome) => erro.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
                var mensagem = erro.TryGetProperty("message", out var m) ? m.GetString() : null;
                return (Inteiro("code"), Inteiro("error_subcode"), mensagem);
            }
            catch (JsonException)
            {
                return (null, null, null);
            }
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
