using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using APIBack.Automation.Infra;
using APIBack.Automation.Interfaces;
using APIBack.DTOs.Clientes;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace APIBack.Service
{
    public interface IClienteSimulatorService
    {
        Task<IReadOnlyList<ClienteDto>> ListAsync(Guid estabelecimentoId);
        Task<SimulatedSendResult> SendMessageAsync(Guid estabelecimentoId, Guid clienteId, string? texto);
        Task<IReadOnlyList<SimulatedMessageDto>> GetMessagesAsync(Guid estabelecimentoId, Guid clienteId, int limit);
    }

    public sealed class SimulatedSendResult
    {
        public string MessageId { get; set; } = string.Empty;
    }

    public sealed class SimulatedMessageDto
    {
        public Guid Id { get; set; }
        /// <summary>"cliente" (o que o simulador mandou) ou "atendimento" (atendente, IA ou sistema).</summary>
        public string Origem { get; set; } = "atendimento";
        public string CriadaPor { get; set; } = string.Empty;
        public string Conteudo { get; set; } = string.Empty;
        public string Tipo { get; set; } = "texto";
        public DateTime DataCriacao { get; set; }
    }

    /// <summary>Monta o corpo que a Meta enviaria ao webhook e a assinatura dele (puro: testavel sem rede).</summary>
    public static class SimulatedWebhook
    {
        private static readonly JsonSerializerOptions Json = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        /// <summary>wa_id do WhatsApp: so digitos, sem '+'.</summary>
        public static string WaId(string telefoneE164) => new string((telefoneE164 ?? string.Empty).Where(char.IsDigit).ToArray());

        public static string BuildTextPayload(
            string phoneNumberId, string? displayPhone, string waId, string? profileName, string texto, string messageId, DateTimeOffset now)
        {
            var payload = new
            {
                @object = "whatsapp_business_account",
                entry = new[]
                {
                    new
                    {
                        id = "simulator",
                        changes = new[]
                        {
                            new
                            {
                                field = "messages",
                                value = new
                                {
                                    messaging_product = "whatsapp",
                                    metadata = new { display_phone_number = displayPhone ?? string.Empty, phone_number_id = phoneNumberId },
                                    contacts = new[] { new { profile = new { name = profileName ?? string.Empty }, wa_id = waId } },
                                    messages = new[]
                                    {
                                        new
                                        {
                                            from = waId,
                                            id = messageId,
                                            timestamp = now.ToUnixTimeSeconds().ToString(),
                                            text = new { body = texto },
                                            type = "text"
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            };
            return JsonSerializer.Serialize(payload, Json);
        }

        /// <summary>X-Hub-Signature-256: "sha256=" + HMAC-SHA256(corpo) em hex minusculo, como a Meta assina.</summary>
        public static string Sign(string appSecret, string body)
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(appSecret ?? string.Empty));
            return "sha256=" + Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
        }
    }

    /// <summary>
    /// Simula o cliente do WhatsApp: a mensagem entra pelo MESMO POST /wa/webhook que a Meta usa
    /// (assinada, com dedupe, fila e IA/atendimento), so que disparada de dentro da API. Somente
    /// clientes marcados como teste (clientes.simulado) e o envio real ao WhatsApp deles e suprimido.
    /// </summary>
    public sealed class ClienteSimulatorService : IClienteSimulatorService
    {
        private const int MaxTextLength = 1000;

        private readonly NpgsqlDataSource _dataSource;
        private readonly IWabaPhoneRepository _waba;
        private readonly IOptions<AutomationOptions> _automation;
        private readonly IHttpClientFactory _httpFactory;
        private readonly IConfiguration _configuration;
        private readonly ILogger<ClienteSimulatorService> _logger;

        public ClienteSimulatorService(
            NpgsqlDataSource dataSource,
            IWabaPhoneRepository waba,
            IOptions<AutomationOptions> automation,
            IHttpClientFactory httpFactory,
            IConfiguration configuration,
            ILogger<ClienteSimulatorService> logger)
        {
            _dataSource = dataSource;
            _waba = waba;
            _automation = automation;
            _httpFactory = httpFactory;
            _configuration = configuration;
            _logger = logger;
        }

        public async Task<IReadOnlyList<ClienteDto>> ListAsync(Guid estabelecimentoId)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            return (await connection.QueryAsync<ClienteDto>(@"
SELECT id AS Id, COALESCE(nome, '') AS Nome, telefone_e164 AS Telefone, ativo AS Ativo, simulado AS Simulado,
       data_criacao AS CriadoEm, data_atualizacao AS AtualizadoEm
  FROM clientes
 WHERE id_estabelecimento = @EstabelecimentoId AND simulado = TRUE AND ativo = TRUE
 ORDER BY lower(COALESCE(nome, telefone_e164, ''));", new { EstabelecimentoId = estabelecimentoId })).ToList();
        }

        public async Task<SimulatedSendResult> SendMessageAsync(Guid estabelecimentoId, Guid clienteId, string? texto)
        {
            var text = texto?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                throw new DeliveryDomainException(422, "INVALID_MESSAGE", "Escreva a mensagem.");
            }
            if (text.Length > MaxTextLength)
            {
                throw new DeliveryDomainException(422, "INVALID_MESSAGE", $"Mensagem excede {MaxTextLength} caracteres.");
            }

            var cliente = await GetSimulatedClientAsync(estabelecimentoId, clienteId);

            var phoneNumberId = await _waba.ObterPhoneNumberIdPorEstabelecimentoAsync(estabelecimentoId);
            if (string.IsNullOrWhiteSpace(phoneNumberId))
            {
                throw new DeliveryDomainException(422, "WHATSAPP_NOT_CONFIGURED",
                    "Este estabelecimento nao tem numero de WhatsApp configurado: sem ele o webhook nao sabe a quem entregar a mensagem.");
            }
            var display = await _waba.ObterDisplayPhonePorEstabelecimentoAsync(estabelecimentoId);

            var messageId = $"wamid.sim.{Guid.NewGuid():N}";
            var body = SimulatedWebhook.BuildTextPayload(
                phoneNumberId, display, SimulatedWebhook.WaId(cliente.Telefone ?? string.Empty),
                string.IsNullOrWhiteSpace(cliente.Nome) ? null : cliente.Nome, text, messageId, DateTimeOffset.UtcNow);

            var url = _configuration["Simulator:WebhookUrl"] ?? "http://127.0.0.1:7137/wa/webhook";
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("X-Hub-Signature-256", SimulatedWebhook.Sign(_automation.Value.Meta?.AppSecret ?? string.Empty, body));

            try
            {
                var client = _httpFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(15);
                using var response = await client.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    throw new DeliveryDomainException(502, "WEBHOOK_REJECTED", $"O webhook respondeu {(int)response.StatusCode}.");
                }
            }
            catch (DeliveryDomainException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao entregar a mensagem simulada ao webhook em {Url}.", url);
                throw new DeliveryDomainException(502, "WEBHOOK_UNREACHABLE", "Nao foi possivel entregar a mensagem ao webhook.");
            }

            return new SimulatedSendResult { MessageId = messageId };
        }

        public async Task<IReadOnlyList<SimulatedMessageDto>> GetMessagesAsync(Guid estabelecimentoId, Guid clienteId, int limit)
        {
            await GetSimulatedClientAsync(estabelecimentoId, clienteId);
            await using var connection = await _dataSource.OpenConnectionAsync();
            var rows = (await connection.QueryAsync<SimulatedMessageDto>(@"
SELECT * FROM (
    SELECT m.id AS Id, COALESCE(m.criada_por, '') AS CriadaPor, COALESCE(m.conteudo, '') AS Conteudo,
           COALESCE(m.tipo::text, 'texto') AS Tipo, COALESCE(m.data_criacao, m.data_envio, NOW()) AS DataCriacao
      FROM mensagens m
      JOIN conversas c ON c.id = m.id_conversa
     WHERE c.id_cliente = @ClienteId AND c.id_estabelecimento = @EstabelecimentoId
     ORDER BY COALESCE(m.data_criacao, m.data_envio) DESC
     LIMIT @Limit
) recent
ORDER BY DataCriacao ASC;", new { ClienteId = clienteId, EstabelecimentoId = estabelecimentoId, Limit = Math.Clamp(limit, 1, 200) })).ToList();

            foreach (var row in rows)
            {
                row.Origem = string.Equals(row.CriadaPor, "cliente", StringComparison.OrdinalIgnoreCase) ? "cliente" : "atendimento";
            }
            return rows;
        }

        private async Task<ClienteDto> GetSimulatedClientAsync(Guid estabelecimentoId, Guid clienteId)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            var cliente = await connection.QuerySingleOrDefaultAsync<ClienteDto>(@"
SELECT id AS Id, COALESCE(nome, '') AS Nome, telefone_e164 AS Telefone, ativo AS Ativo, simulado AS Simulado
  FROM clientes
 WHERE id = @Id AND id_estabelecimento = @EstabelecimentoId;", new { Id = clienteId, EstabelecimentoId = estabelecimentoId });

            if (cliente == null || !cliente.Ativo)
            {
                throw new DeliveryDomainException(404, "CLIENT_NOT_FOUND", "Cliente nao encontrado.");
            }
            if (!cliente.Simulado)
            {
                // Sem isto, a resposta do atendente/IA iria para o WhatsApp de uma pessoa real.
                throw new DeliveryDomainException(409, "CLIENT_NOT_SIMULATED",
                    "So clientes marcados como \"cliente de teste\" podem ser simulados.");
            }
            if (string.IsNullOrWhiteSpace(cliente.Telefone))
            {
                throw new DeliveryDomainException(422, "CLIENT_WITHOUT_PHONE", "O cliente nao tem telefone.");
            }
            return cliente;
        }
    }
}
