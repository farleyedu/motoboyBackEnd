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
using APIBack.DTOs.Simulador;
using APIBack.Model.Enum;
using APIBack.Repository;
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
        Task<SimClienteListaDto> ListWithStatsAsync(Guid estabelecimentoId, string? busca, int page, int pageSize);
        Task<SimClienteDto> GetAsync(Guid estabelecimentoId, Guid clienteId);
        Task<SimConversaDto> GetConversaAsync(Guid estabelecimentoId, Guid clienteId);
        Task<IReadOnlyList<SimPedidoResumoDto>> GetPedidosAsync(Guid estabelecimentoId, Guid clienteId, int limit);
        Task<SimulatedSendResult> SendMessageAsync(Guid estabelecimentoId, int userId, Guid clienteId, string? texto);
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
        private readonly ISimuladorRepository _eventos;

        public ClienteSimulatorService(
            NpgsqlDataSource dataSource,
            IWabaPhoneRepository waba,
            IOptions<AutomationOptions> automation,
            IHttpClientFactory httpFactory,
            IConfiguration configuration,
            ILogger<ClienteSimulatorService> logger,
            ISimuladorRepository eventos)
        {
            _eventos = eventos;
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

        public async Task<SimulatedSendResult> SendMessageAsync(Guid estabelecimentoId, int userId, Guid clienteId, string? texto)
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

            try
            {
                await _eventos.AddEventoAsync(estabelecimentoId, userId > 0 ? userId : null, null, new SimEventoInput(
                    "cliente", clienteId.ToString(), "mensagem_cliente", "Cliente simulado enviou mensagem",
                    text.Length > 200 ? text[..200] : text, "sucesso", null, null));
            }
            catch (PostgresException)
            {
                // O log e melhor esforco: migration pendente nao pode barrar a mensagem.
            }

            return new SimulatedSendResult { MessageId = messageId };
        }

        // ------------------------------------------------------------------ lista, conversa e pedidos

        private const string ClienteColumns = @"
       id AS Id, COALESCE(nome, '') AS Nome, telefone_e164 AS Telefone, email AS Email, observacoes AS Observacoes,
       cep AS Cep, logradouro AS Logradouro, numero AS Numero, complemento AS Complemento, bairro AS Bairro,
       cidade AS Cidade, uf AS Uf, latitude AS Latitude, longitude AS Longitude, ativo AS Ativo, simulado AS Simulado,
       avatar AS Avatar, cpf AS Cpf, data_nascimento::text AS DataNascimento, referencia AS Referencia,
       canal_preferido AS CanalPreferido, origem AS Origem, tags AS Tags, consentimento_whatsapp AS ConsentimentoWhatsapp,
       data_criacao AS CriadoEm, data_atualizacao AS AtualizadoEm";

        private sealed class StatsRow
        {
            public Guid Id { get; set; }
            public int TotalPedidos { get; set; }
            public DateTime? Ultima { get; set; }
            public Guid? ConversaId { get; set; }
            public bool Humano { get; set; }
        }

        public async Task<SimClienteListaDto> ListWithStatsAsync(Guid estabelecimentoId, string? busca, int page, int pageSize)
        {
            var (safePage, safeSize) = ClienteRules.ClampPaging(page, pageSize);
            var term = string.IsNullOrWhiteSpace(busca) ? null : busca.Trim();
            var digits = term == null ? string.Empty : new string(term.Where(char.IsDigit).ToArray());

            await using var connection = await _dataSource.OpenConnectionAsync();
            const string where = @"
 WHERE id_estabelecimento = @Est AND simulado = TRUE AND ativo = TRUE
   AND (@Term IS NULL OR COALESCE(nome, '') ILIKE '%' || @Term || '%'
        OR (@Digits <> '' AND COALESCE(telefone_e164, '') LIKE '%' || @Digits || '%'))";
            var p = new { Est = estabelecimentoId, Term = term, Digits = digits, Offset = (safePage - 1) * safeSize, Limit = safeSize };

            var total = await connection.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM clientes{where};", p);
            var clientes = (await connection.QueryAsync<ClienteDto>(
                $"SELECT{ClienteColumns} FROM clientes{where} ORDER BY lower(COALESCE(nome, telefone_e164, '')), id LIMIT @Limit OFFSET @Offset;", p)).ToList();

            var stats = await LoadStatsAsync(connection, estabelecimentoId, clientes.Select(item => item.Id).ToArray());
            return new SimClienteListaDto
            {
                Itens = clientes.Select(item => ToSimCliente(item, stats.GetValueOrDefault(item.Id))).ToList(),
                Total = total,
                Page = safePage,
                PageSize = safeSize
            };
        }

        public async Task<SimClienteDto> GetAsync(Guid estabelecimentoId, Guid clienteId)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            var cliente = await connection.QuerySingleOrDefaultAsync<ClienteDto>(
                $"SELECT{ClienteColumns} FROM clientes WHERE id = @Id AND id_estabelecimento = @Est;", new { Id = clienteId, Est = estabelecimentoId });
            if (cliente == null) throw new DeliveryDomainException(404, "CLIENT_NOT_FOUND", "Cliente nao encontrado.");
            var stats = await LoadStatsAsync(connection, estabelecimentoId, new[] { clienteId });
            return ToSimCliente(cliente, stats.GetValueOrDefault(clienteId));
        }

        private static SimClienteDto ToSimCliente(ClienteDto cliente, StatsRow? stats) => new()
        {
            Cliente = cliente,
            TotalPedidos = stats?.TotalPedidos ?? 0,
            UltimaAtividadeUtc = stats?.Ultima ?? cliente.AtualizadoEm,
            ConversaId = stats?.ConversaId,
            Etiqueta = SimuladorPedidoRules.ClienteEtiqueta(cliente.Ativo, cliente.Tags, stats?.Humano ?? false, stats?.TotalPedidos ?? 0)
        };

        private static async Task<Dictionary<Guid, StatsRow>> LoadStatsAsync(NpgsqlConnection connection, Guid est, Guid[] ids)
        {
            if (ids.Length == 0) return new Dictionary<Guid, StatsRow>();
            var rows = await connection.QueryAsync<StatsRow>(@"
SELECT cl.id AS Id,
       (SELECT COUNT(*)::int FROM pedido p
         WHERE p.id_estabelecimento = cl.id_estabelecimento AND COALESCE(p.status_pedido, 1) <> 6
           AND RIGHT(regexp_replace(COALESCE(p.telefone_cliente::text, ''), '\D', '', 'g'), 10)
             = RIGHT(regexp_replace(COALESCE(cl.telefone_e164, ''), '\D', '', 'g'), 10)) AS TotalPedidos,
       conv.ultima AS Ultima, conv.id AS ConversaId, COALESCE(conv.humano, FALSE) AS Humano
  FROM clientes cl
  LEFT JOIN LATERAL (
      SELECT c.id, COALESCE(c.data_ultima_mensagem, c.data_criacao) AS ultima, (c.id_agente_atribuido IS NOT NULL) AS humano
        FROM conversas c
       WHERE c.id_cliente = cl.id AND c.id_estabelecimento = cl.id_estabelecimento
       ORDER BY c.data_criacao DESC LIMIT 1) conv ON TRUE
 WHERE cl.id = ANY(@Ids) AND cl.id_estabelecimento = @Est;", new { Ids = ids, Est = est });
            return rows.ToDictionary(row => row.Id);
        }

        public async Task<SimConversaDto> GetConversaAsync(Guid estabelecimentoId, Guid clienteId)
        {
            await GetSimulatedClientAsync(estabelecimentoId, clienteId);
            await using var connection = await _dataSource.OpenConnectionAsync();
            var row = await connection.QuerySingleOrDefaultAsync<(Guid Id, string? Estado, bool Humano, int NaoLidas)?>(@"
SELECT c.id, c.estado::text, (c.id_agente_atribuido IS NOT NULL), COALESCE(c.qtd_nao_lidas, 0)
  FROM conversas c
 WHERE c.id_cliente = @Cliente AND c.id_estabelecimento = @Est
 ORDER BY c.data_criacao DESC LIMIT 1;", new { Cliente = clienteId, Est = estabelecimentoId });
            return row is null
                ? new SimConversaDto()
                : new SimConversaDto { ConversaId = row.Value.Id, Estado = row.Value.Estado, Humano = row.Value.Humano, NaoLidas = row.Value.NaoLidas };
        }

        public async Task<IReadOnlyList<SimPedidoResumoDto>> GetPedidosAsync(Guid estabelecimentoId, Guid clienteId, int limit)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            var telefone = await connection.QuerySingleOrDefaultAsync<string?>(
                "SELECT telefone_e164 FROM clientes WHERE id = @Id AND id_estabelecimento = @Est;", new { Id = clienteId, Est = estabelecimentoId })
                ?? throw new DeliveryDomainException(404, "CLIENT_NOT_FOUND", "Cliente nao encontrado.");

            var rows = await connection.QueryAsync<(int Id, int Status, string? Origem, decimal? Total, string? Data, string? Nome)>(@"
SELECT p.id, COALESCE(p.status_pedido, 1), p.origem, p.value, p.data_pedido::text, p.nome_cliente::text
  FROM pedido p
 WHERE p.id_estabelecimento = @Est AND COALESCE(p.status_pedido, 1) <> 6
   AND RIGHT(regexp_replace(COALESCE(p.telefone_cliente::text, ''), '\D', '', 'g'), 10)
     = RIGHT(regexp_replace(@Tel, '\D', '', 'g'), 10)
 ORDER BY p.id DESC
 LIMIT @Limit;", new { Est = estabelecimentoId, Tel = telefone, Limit = Math.Clamp(limit, 1, 50) });

            return rows.Select(row =>
            {
                var simulado = string.Equals(row.Origem, "simulador", StringComparison.OrdinalIgnoreCase);
                return new SimPedidoResumoDto
                {
                    Id = row.Id,
                    IdExibicao = SimuladorPedidoRules.DisplayId(row.Id, simulado),
                    Status = StatusPedidoExtensions.ToApiKey(row.Status),
                    Origem = row.Origem ?? "atendente",
                    Total = row.Total,
                    CriadoEm = DeliveryRules.ParseStoredDateTime(row.Data),
                    NomeCliente = row.Nome
                };
            }).ToList();
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
