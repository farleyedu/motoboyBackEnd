// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using APIBack.Automation.Dtos;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Services;
using APIBack.Automation.Infra;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using APIBack.Automation.Models;
using APIBack.Automation.Helpers; // Adicionado para normalização de telefone no envio
using System.Collections.Generic;
using APIBack.Automation.Dtos;

namespace APIBack.Automation.Controllers
{
    [ApiController]
    [Route("wa")]
    public class WaWebhookController : ControllerBase
    {
        private readonly ILogger<WaWebhookController> _logger;
        private readonly IWebhookSignatureValidator _validadorAssinatura;
        private readonly ConversationService _servicoConversa;
        private readonly IQueueBus _fila;
        private readonly IAssistantService? _ia;
        private readonly IHttpClientFactory _httpFactory;
        private readonly IOptions<AutomationOptions> _opcoes;
        private readonly IWabaPhoneRepository _wabaRepo;
        private readonly IIARegraRepository _regrasRepo;
        private readonly IIARespostaRepository _respostasRepo;
        private readonly APIBack.Automation.Interfaces.IConversationRepository _repositorio;
        private readonly IMessageService _mensagemService;
        private readonly IMessageRepository _mensagemRepository;
        private readonly IConfiguration _configuration;
        private readonly IWhatsAppTokenProvider _waTokenProvider;

        public WaWebhookController(
            ILogger<WaWebhookController> logger,
            IWebhookSignatureValidator validadorAssinatura,
            ConversationService servicoConversa,
            IQueueBus fila,
            IHttpClientFactory httpFactory,
            IOptions<AutomationOptions> opcoes,
            IWabaPhoneRepository wabaRepo,
            IIARegraRepository regrasRepo,
            IIARespostaRepository respostasRepo,
            APIBack.Automation.Interfaces.IConversationRepository repositorio,
            IConfiguration configuration,
            IWhatsAppTokenProvider waTokenProvider,
            IMessageService mensagemService,
            IMessageRepository mensagemRepository,
            IAssistantService? ia = null)
        {
            _logger = logger;
            _validadorAssinatura = validadorAssinatura;
            _servicoConversa = servicoConversa;
            _fila = fila;
            _httpFactory = httpFactory;
            _opcoes = opcoes;
            _ia = ia;
            _wabaRepo = wabaRepo;
            _regrasRepo = regrasRepo;
            _respostasRepo = respostasRepo;
            _repositorio = repositorio;
            _configuration = configuration;
            _waTokenProvider = waTokenProvider;
            _mensagemService = mensagemService;
            _mensagemRepository = mensagemRepository;
        }

        [HttpGet("webhook")]
        public IActionResult VerifyWebhook(
            [FromQuery(Name = "hub.mode")] string mode,
            [FromQuery(Name = "hub.verify_token")] string token,
            [FromQuery(Name = "hub.challenge")] string challenge)
        {
            var verifyToken = _opcoes.Value?.VerifyToken ?? "zippygo123"; // configurável em appsettings
            if (mode == "subscribe" && token == verifyToken)
            {
                _logger.LogInformation("Webhook verificado com sucesso pelo Meta.");
                return Ok(challenge); // devolve o challenge
            }
            else
            {
                _logger.LogWarning("Falha na verificaÃ§Ã£o do webhook. Token invÃ¡lido.");
                return Forbid(); // 403
            }
        }

        [HttpPost("webhook")]
        public async Task<IActionResult> Webhook()
        {
            try
            {
                // Signature validation (when enabled)
                Request.EnableBuffering();
                string corpo;
                using (var reader = new StreamReader(Request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true))
                {
                    corpo = await reader.ReadToEndAsync();
                    Request.Body.Position = 0;
                }

                _logger.LogInformation("Webhook recebido: {Payload}", corpo);

                var cabecalho = Request.Headers["X-Hub-Signature-256"].ToString();
                if (!_validadorAssinatura.ValidarXHubSignature256(cabecalho, corpo))
                {
                    _logger.LogWarning("Invalid X-Hub-Signature-256");
                    return Ok(); // Retorna 200 mesmo com assinatura invÃ¡lida
                }

                WebhookPayloadDto? carga = null;
                try
                {
                    carga = JsonSerializer.Deserialize<WebhookPayloadDto>(corpo);
                }
                catch
                {
                    // invalid payload: ack 200 to avoid excessive retries
                    return Ok();
                }

                // Processar todas as entradas e mudanÃ§as
                if (carga?.Entradas != null)
                {
                    foreach (var entrada in carga.Entradas)
                    {
                        if (entrada.Mudancas != null)
                        {
                            foreach (var mudanca in entrada.Mudancas)
                            {
                                if (mudanca.Campo == "messages" && mudanca.Valor?.Mensagens != null)
                                {
                                    // Extrair phone_number_id dos metadados
                                    var phoneNumberEstabelecimento = mudanca.Valor.Metadados?.NumeroTelefoneExibicao;
                                    var phoneNumberwhats = mudanca.Valor.Metadados?.IdNumeroTelefone;

                                    foreach (var mensagem in mudanca.Valor.Mensagens)
                                    {
                                        try
                                        {
                                            if (mensagem.De != null && !string.IsNullOrWhiteSpace(mensagem.Id))
                                            {
                                                var texto = mensagem.Texto?.Corpo ?? string.Empty;
                                                DateTime? dataMsgUtc = null;
                                                if (!string.IsNullOrWhiteSpace(mensagem.CarimboTempo) && long.TryParse(mensagem.CarimboTempo, out var unix))
                                                {
                                                    try { dataMsgUtc = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime; } catch { /* ignora parse invÃ¡lido */ }
                                                }
                                                // Use o display number (numero exibido) como parÃ¢metro para o service,
                                                var criada = await _servicoConversa.AcrescentarEntradaAsync(mensagem.De!, mensagem.Id!, texto, phoneNumberEstabelecimento,  dataMsgUtc );

                                                if (criada != null)
                                                {
                                                    await _fila.PublicarEntradaAsync(criada);
                                                    try
                                                    {
                                                        Guid? idEstab = null;
                                                        if (!string.IsNullOrWhiteSpace(phoneNumberEstabelecimento))
                                                            idEstab = await _wabaRepo.ObterIdEstabelecimentoPorPhoneNumberIdAsync(phoneNumberEstabelecimento);

                                                        Guid? idRegraAplicada = null;
                                                        string? contexto = null;
                                                        if (idEstab.HasValue && idEstab.Value != Guid.Empty)
                                                        {
                                                            // Combina todas as regras ativas (ordem: mais recentes primeiro)
                                                            var regras = await _regrasRepo.ListaregrasAsync(idEstab.Value);
                                                            var ativas = regras?
                                                                .Where(r => r.Ativo && !string.IsNullOrWhiteSpace(r.Contexto))
                                                                .OrderByDescending(r => r.DataAtualizacao)
                                                                .ThenByDescending(r => r.DataCriacao)
                                                                .ToList();
                                                            if (ativas != null && ativas.Count > 0)
                                                            {
                                                                idRegraAplicada = ativas.First().Id;
                                                                contexto = string.Join("\n\n", ativas.Select(r => r.Contexto));
                                                            }
                                                            else
                                                            {
                                                                contexto = await _regrasRepo.ObterContextoAtivoAsync(idEstab.Value);
                                                            }
                                                        }

                                                        // Carrega histórico da conversa ABERTA (limitado) e chama IA com histórico
                                                        var historico = await _mensagemRepository.GetByConversationAsync(criada.IdConversa, limit: 200, onlyWhenOpen: true);
                                                        var turns = new List<AssistantChatTurn>();
                                                        foreach (var m in historico)
                                                        {
                                                            if (string.IsNullOrWhiteSpace(m.Conteudo)) continue;
                                                            var role = m.Direcao == DirecaoMensagem.Entrada ? "user" : "assistant";
                                                            turns.Add(new AssistantChatTurn
                                                            {
                                                                Role = role,
                                                                Content = m.Conteudo,
                                                                Timestamp = m.DataCriacao ?? m.DataEnvio ?? m.DataEntrega
                                                            });
                                                        }
                                                        // Usa apenas o contexto vindo do banco (todas regras ativas combinadas)
                                                        var respostaIa = _ia != null ? await _ia.GerarRespostaComHistoricoAsync(criada.IdConversa, texto, turns, contexto) : null;
                                                        if (!string.IsNullOrWhiteSpace(respostaIa) && !string.IsNullOrWhiteSpace(phoneNumberEstabelecimento) && !string.IsNullOrWhiteSpace(mensagem.De))
                                                        {
                                                            // Tenta interpretar a decisão da IA
                                                            string conteudoFinal = respostaIa!;
                                                            string? handoverAcao = null;
                                                            string? agentPrompt = null;
                                                            try
                                                            {
                                                                var decisao = JsonSerializer.Deserialize<AssistantDecisionDto>(respostaIa!);
                                                                if (decisao != null)
                                                                {
                                                                    if (!string.IsNullOrWhiteSpace(decisao.reply)) conteudoFinal = decisao.reply!;
                                                                    handoverAcao = decisao.handover?.Trim().ToLowerInvariant();
                                                                    agentPrompt = decisao.agent_prompt;
                                                                }
                                                            }
                                                            catch { /* se não for JSON válido, segue com texto bruto */ }

                                                            if (handoverAcao == "confirm")
                                                            {
                                                                //
                                                                //
                                                                //
                                                                // Aciona handoff e envia mensagem fixa única ao usuário
                                                                        var agenteDesignadoId = 4;
                                                                //nessa seção eu vou definir qual agente chamar de com o contexto da conversa, eu quero que IA decida qual o 
                                                                //melhor agente para chamar, baseado no contexto da conversa. //Exemplo: se a conversa for sobre Agendamento,
                                                                //chamar o agente de Agendamento, se for sobre Suporte, chamar o agente de Suporte.
                                                                //
                                                                //


                                                                var clienteNome = mudanca.Valor?.Contatos?.FirstOrDefault()?.Perfil?.Nome;
                                                                var historicoResumo = turns
                                                                    .TakeLast(10)
                                                                    .Select(t => $"{(t.Role == "user" ? "Cliente" : "Assistente")}: {t.Content}")
                                                                    .ToList();
                                                                var handoverDetalhes = new HandoverContextDto
                                                                {
                                                                    ClienteNome = clienteNome,
                                                                    Telefone = mensagem.De,
                                                                    QueixaPrincipal = texto,
                                                                    Contexto = contexto,
                                                                    Historico = historicoResumo
                                                                };
                                                                var reservaConfirmada = false;

                                                                if (agenteDesignadoId > 0)
                                                                            {
                                                                                var agente = new HandoverAgentDto { Id = agenteDesignadoId };
                                                                                await ChamarHandoverEndpointAsync(criada.IdConversa, reservaConfirmada, agente, handoverDetalhes);
                                                                            }

                                                                var handoffMsg = "Vou te encaminhar para um atendente humano para continuar o atendimento. Um momento, por favor.";
                                                                var msgHandoff = new Message
                                                                {
                                                                    IdConversa = criada.IdConversa,
                                                                    IdMensagemWa = Guid.NewGuid().ToString(),
                                                                    Direcao = DirecaoMensagem.Saida,
                                                                    Conteudo = handoffMsg,
                                                                    DataHora = DateTime.UtcNow,
                                                                    CriadaPor = "ia",
                                                                    Status = "fila"
                                                                };
                                                                await _mensagemService.AdicionarMensagemAsync(msgHandoff, phoneNumberwhats!, mensagem.De);
                                                                await EnviarRespostaWhatsAppAsync(phoneNumberwhats!, mensagem.De!, handoffMsg);
                                                                _logger.LogInformation("Mensagem de handoff enviada para {Destino}", mensagem.De);
                                                            }
                                                            else if (handoverAcao == "ask")
                                                            {
                                                                // Envia a resposta principal
                                                                var mensagemSaida = new Message
                                                                {
                                                                    IdConversa = criada.IdConversa,
                                                                    IdMensagemWa = Guid.NewGuid().ToString(),
                                                                    Direcao = DirecaoMensagem.Saida,
                                                                    Conteudo = conteudoFinal,
                                                                    DataHora = DateTime.UtcNow,
                                                                    CriadaPor = "ia",
                                                                    Status = "fila"
                                                                };
                                                                await _mensagemService.AdicionarMensagemAsync(mensagemSaida, phoneNumberEstabelecimento, mensagem.De);
                                                                await EnviarRespostaWhatsAppAsync(phoneNumberwhats!, mensagem.De!, conteudoFinal);
                                                                _logger.LogInformation("Resposta automatica enviada para {Destino}", mensagem.De);

                                                                // Envia pergunta de confirmação de handover
                                                                var pergunta = string.IsNullOrWhiteSpace(agentPrompt)
                                                                    ? "Deseja que eu chame um atendente humano para ajudar você?"
                                                                    : agentPrompt!;
                                                                var msgPergunta = new Message
                                                                {
                                                                    IdConversa = criada.IdConversa,
                                                                    IdMensagemWa = Guid.NewGuid().ToString(),
                                                                    Direcao = DirecaoMensagem.Saida,
                                                                    Conteudo = pergunta,
                                                                    DataHora = DateTime.UtcNow,
                                                                    CriadaPor = "ia",
                                                                    Status = "fila"
                                                                };
                                                                await _mensagemService.AdicionarMensagemAsync(msgPergunta, phoneNumberwhats!, mensagem.De);
                                                                await EnviarRespostaWhatsAppAsync(phoneNumberwhats!, mensagem.De!, pergunta);
                                                                _logger.LogInformation("Pergunta de handover enviada para {Destino}", mensagem.De);
                                                            }
                                                            else
                                                            {
                                                                // Sem handover: envia apenas a resposta normal da IA
                                                                var mensagemSaida = new Message
                                                                {
                                                                    IdConversa = criada.IdConversa,
                                                                    IdMensagemWa = Guid.NewGuid().ToString(),
                                                                    Direcao = DirecaoMensagem.Saida,
                                                                    Conteudo = conteudoFinal,
                                                                    DataHora = DateTime.UtcNow,
                                                                    CriadaPor = "ia",
                                                                    Status = "fila"
                                                                };
                                                                await _mensagemService.AdicionarMensagemAsync(mensagemSaida, phoneNumberEstabelecimento, mensagem.De);
                                                                await EnviarRespostaWhatsAppAsync(phoneNumberwhats!, mensagem.De!, conteudoFinal);
                                                                _logger.LogInformation("Resposta automatica enviada para {Destino}", mensagem.De);
                                                            }
                                                        }
                                                    }
                                                    catch (Exception exAuto)
                                                    {
                                                        _logger.LogError(exAuto, "Falha ao gerar/enviar resposta automatica");
                                                    }
                                                }
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            _logger.LogError(ex, "Erro ao processar mensagem individual {MensagemId} de {De}", mensagem.Id, mensagem.De);
                                            // Continua processando outras mensagens
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                return Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro cri­tico ao processar webhook");
                return Ok(); // SEMPRE retorna 200 para evitar reenvios do WhatsApp
            }
        }

        // Funï¿½ï¿½o para detectar intenï¿½ï¿½o de handover
        private bool DetectaHandover(string texto)
        {
            return texto.Contains("atendente", StringComparison.OrdinalIgnoreCase)
                || texto.Contains("humano", StringComparison.OrdinalIgnoreCase)
                || texto.Contains("pessoa", StringComparison.OrdinalIgnoreCase)
                || texto.Contains("gente", StringComparison.OrdinalIgnoreCase)
                || texto.Contains("falar com alguï¿½m", StringComparison.OrdinalIgnoreCase);
        }

        // Funï¿½ï¿½o para chamar o endpoint de handover
        private async Task ChamarHandoverEndpointAsync(Guid idConversa, bool reservaConfirmada, HandoverAgentDto agente, HandoverContextDto detalhes)
        {
            using var httpClient = new HttpClient();
            var baseUrl = _opcoes.Value?.Handover?.BaseUrl ?? "http://127.0.0.1:7137/automation";
            var url = $"{baseUrl.TrimEnd('/')}/conversation/{idConversa}/handover";
            var body = JsonSerializer.Serialize(new { Agente = agente, ReservaConfirmada = reservaConfirmada, Detalhes = detalhes });
            var content = new StringContent(body, Encoding.UTF8, "application/json");
            await httpClient.PostAsync(url, content);
        }

        // Envia resposta de texto via WhatsApp Cloud API (Graph)
        private async Task EnviarRespostaWhatsAppAsync(string phoneNumberId, string numeroDestino, string textoResposta)
        {
            try
            {
                // Obtém token do provedor em memória; se não houver, cai no appsettings.
                var token = _waTokenProvider.GetAccessToken();
                if (string.IsNullOrWhiteSpace(token))
                {
                    _logger.LogWarning("Token do WhatsApp (WhatsApp:AccessToken) não configurado");
                    return;
                }

                var client = _httpFactory.CreateClient();
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

                var graphVersion = _configuration["WhatsApp:GraphApiVersion"] ?? "v17.0";
                var endpoint = $"https://graph.facebook.com/{graphVersion}/{phoneNumberId}/messages";
                // Normalização solicitada para números brasileiros: se começar com "55" e tiver 12 dígitos, inserir '9' após o DDD (índice 4).
                // Ex.: 553491480112 -> 5534991480112
                var numeroDestinoNormalizado = TelefoneHelper.NormalizeBrazilianForWhatsappTo(numeroDestino);
                var payload = new
                {
                    messaging_product = "whatsapp",
                    to = numeroDestinoNormalizado, // utiliza o número normalizado
                    type = "text",
                    text = new { body = textoResposta }
                };

                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var resp = await client.PostAsync(endpoint, content);
                if (!resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync();
                    _logger.LogWarning("Falha ao enviar resposta WA: {Status} {Body}", (int)resp.StatusCode, body);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao enviar resposta para WhatsApp");
            }
        }

        // Endpoint para atualizar o Access Token do WhatsApp em tempo de execução (uso em testes/desenvolvimento)
        [HttpPost("token")]
        public IActionResult AtualizarAccessToken([FromBody] UpdateWhatsAppTokenRequest req)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.AccessToken))
            {
                return BadRequest(new { error = "AccessToken obrigatório" });
            }

            _waTokenProvider.SetAccessToken(req.AccessToken);
            _logger.LogInformation("Token do WhatsApp atualizado via endpoint em {When}", DateTimeOffset.UtcNow);

            return Ok(new
            {
                message = "Token atualizado com sucesso (apenas em memória).",
                updated_at_utc = _waTokenProvider.LastUpdatedUtc?.ToString("o")
            });
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================




