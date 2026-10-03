using APIBack.Payments.Models;
using APIBack.Payments.Repository.Interface;
using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Npgsql.PostgresTypes;

namespace APIBack.Payments.Repository
{
    public class CheckoutRepository : ICheckoutRepository
    {
        private readonly string _connectionString;

        public CheckoutRepository(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection")
                ?? configuration["ConnectionStrings:DefaultConnection"]
                ?? throw new InvalidOperationException("Connection string 'DefaultConnection' nao configurada.");
        }

        public async Task<(string Nome, string Email)?> GetUserBasicAsync(int userId)
        {
            const string sql = @"
SELECT nome AS Nome, email AS Email
  FROM usuario
 WHERE id = @UserId
   AND deleted_at IS NULL
 LIMIT 1";

            await using var connection = new NpgsqlConnection(_connectionString);
            return await connection.QueryFirstOrDefaultAsync<(string Nome, string Email)>(sql, new { UserId = userId });
        }

        public async Task<string?> GetAsaasCustomerIdAsync(int userId)
        {
            const string sql = @"
SELECT asaas_customer_id
  FROM checkout_asaas_customers
 WHERE id_usuario = @UserId";

            await using var connection = new NpgsqlConnection(_connectionString);
            return await connection.ExecuteScalarAsync<string?>(sql, new { UserId = userId });
        }

        public async Task UpsertAsaasCustomerIdAsync(int userId, string asaasCustomerId)
        {
            const string sql = @"
INSERT INTO checkout_asaas_customers
    (id_usuario, asaas_customer_id, data_criacao, data_atualizacao)
VALUES
    (@UserId, @AsaasCustomerId, NOW(), NOW())
ON CONFLICT (id_usuario) DO UPDATE SET
    asaas_customer_id = EXCLUDED.asaas_customer_id,
    data_atualizacao = NOW()";

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.ExecuteAsync(sql, new { UserId = userId, AsaasCustomerId = asaasCustomerId });
        }

        public async Task<long> CreatePaymentAsync(NewCheckoutPayment payment)
        {
            const string sql = @"
INSERT INTO checkout_pagamentos
    (id_usuario, id_estabelecimento, asaas_payment_id, asaas_customer_id, tipo_pagamento, status, asaas_status, valor, descricao, invoice_url, pix_qr_code_base64, pix_copia_cola, json_retorno_gateway, data_criacao, data_atualizacao)
VALUES
    (@UserId, @EstabelecimentoId, @AsaasPaymentId, @AsaasCustomerId, @PaymentType, @Status, @AsaasStatus, @Value, @Description, @InvoiceUrl, @PixQrCodeBase64, @PixCopyPaste, @GatewayResponseJson::jsonb, NOW(), NOW())
RETURNING id";

            await using var connection = new NpgsqlConnection(_connectionString);
            return await connection.ExecuteScalarAsync<long>(sql, payment);
        }

        public async Task<CheckoutPaymentRecord?> GetPaymentByIdAsync(long paymentId)
        {
            const string sql = @"
SELECT id AS Id,
       id_usuario AS UserId,
       id_estabelecimento AS EstabelecimentoId,
       asaas_payment_id AS AsaasPaymentId,
       asaas_customer_id AS AsaasCustomerId,
       tipo_pagamento AS PaymentType,
       status AS Status,
       asaas_status AS AsaasStatus,
       valor AS Value,
       descricao AS Description,
       invoice_url AS InvoiceUrl,
       pix_qr_code_base64 AS PixQrCodeBase64,
       pix_copia_cola AS PixCopyPaste,
       data_criacao AS CreatedAt,
       data_atualizacao AS UpdatedAt
  FROM checkout_pagamentos
 WHERE id = @PaymentId
 LIMIT 1";

            await using var connection = new NpgsqlConnection(_connectionString);
            return await connection.QueryFirstOrDefaultAsync<CheckoutPaymentRecord>(sql, new { PaymentId = paymentId });
        }

        public async Task<CheckoutPaymentRecord?> GetPaymentByAsaasIdAsync(string asaasPaymentId)
        {
            const string sql = @"
SELECT id AS Id,
       id_usuario AS UserId,
       id_estabelecimento AS EstabelecimentoId,
       asaas_payment_id AS AsaasPaymentId,
       asaas_customer_id AS AsaasCustomerId,
       tipo_pagamento AS PaymentType,
       status AS Status,
       asaas_status AS AsaasStatus,
       valor AS Value,
       descricao AS Description,
       invoice_url AS InvoiceUrl,
       pix_qr_code_base64 AS PixQrCodeBase64,
       pix_copia_cola AS PixCopyPaste,
       data_criacao AS CreatedAt,
       data_atualizacao AS UpdatedAt
  FROM checkout_pagamentos
 WHERE asaas_payment_id = @AsaasPaymentId
 LIMIT 1";

            await using var connection = new NpgsqlConnection(_connectionString);
            return await connection.QueryFirstOrDefaultAsync<CheckoutPaymentRecord>(sql, new { AsaasPaymentId = asaasPaymentId });
        }

        public async Task UpdatePaymentFromWebhookAsync(string asaasPaymentId, string status, string? asaasStatus, string? webhookPayloadJson)
        {
            const string sql = @"
UPDATE checkout_pagamentos
   SET status = @Status,
       asaas_status = @AsaasStatus,
       json_webhook_ultimo = CASE WHEN @WebhookPayloadJson IS NULL THEN json_webhook_ultimo ELSE @WebhookPayloadJson::jsonb END,
       data_atualizacao = NOW()
 WHERE asaas_payment_id = @AsaasPaymentId";

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.ExecuteAsync(sql, new
            {
                AsaasPaymentId = asaasPaymentId,
                Status = status,
                AsaasStatus = asaasStatus,
                WebhookPayloadJson = webhookPayloadJson
            });
        }

        public async Task<bool> TryCreateWebhookLogAsync(string eventId, string eventType, string? asaasPaymentId, string payloadJson)
        {
            const string sql = @"
INSERT INTO checkout_webhook_logs
    (event_id, event_type, asaas_payment_id, payload, sucesso, data_recebimento)
VALUES
    (@EventId, @EventType, @AsaasPaymentId, @Payload::jsonb, FALSE, NOW())";

            await using var connection = new NpgsqlConnection(_connectionString);
            try
            {
                await connection.ExecuteAsync(sql, new
                {
                    EventId = eventId,
                    EventType = eventType,
                    AsaasPaymentId = asaasPaymentId,
                    Payload = payloadJson
                });

                return true;
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                return false;
            }
        }

        public async Task CompleteWebhookLogAsync(string eventId, bool success, string? errorMessage)
        {
            const string sql = @"
UPDATE checkout_webhook_logs
   SET sucesso = @Success,
       mensagem_erro = @ErrorMessage,
       processado_em = NOW()
 WHERE event_id = @EventId";

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.ExecuteAsync(sql, new { EventId = eventId, Success = success, ErrorMessage = errorMessage });
        }
    }
}
