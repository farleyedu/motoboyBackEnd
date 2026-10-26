using System;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace APIBack.Payments.MercadoPago
{
    public sealed class EstornoRequest
    {
        public Guid IntentId { get; set; }
        public Guid IdEstabelecimento { get; set; }
        /// <summary>Null = tudo o que ainda nao foi estornado.</summary>
        public decimal? Valor { get; set; }
        public string? Motivo { get; set; }
        public int? UsuarioId { get; set; }
        /// <summary>Chave do chamador: repetir nao estorna duas vezes.</summary>
        public string IdempotencyKey { get; set; } = string.Empty;
    }

    public sealed class EstornoResult
    {
        public Guid EstornoId { get; set; }
        public string Status { get; set; } = string.Empty;
        public decimal Valor { get; set; }
        public string StatusIntent { get; set; } = string.Empty;
        public decimal ValorEstornadoTotal { get; set; }
    }

    /// <summary>Estorno total ou parcial na conta que recebeu. A soma e limitada ao valor pago pelo banco (tg_pagamento_estorno_limite).</summary>
    public interface IPagamentoEstornoService
    {
        Task<EstornoResult> EstornarAsync(EstornoRequest request, CancellationToken ct = default);
    }

    public sealed class PagamentoEstornoService : IPagamentoEstornoService
    {
        private readonly NpgsqlDataSource _db;
        private readonly IMercadoPagoCredencialResolver _credenciais;
        private readonly IMercadoPagoClient _client;
        private readonly IPagamentoSincronizador _sync;
        private readonly ILogger<PagamentoEstornoService> _logger;

        public PagamentoEstornoService(NpgsqlDataSource db, IMercadoPagoCredencialResolver credenciais, IMercadoPagoClient client, IPagamentoSincronizador sync,
            ILogger<PagamentoEstornoService> logger)
        {
            _db = db;
            _credenciais = credenciais;
            _client = client;
            _sync = sync;
            _logger = logger;
        }

        public async Task<EstornoResult> EstornarAsync(EstornoRequest r, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(r.IdempotencyKey) || r.IdempotencyKey.Length > 160)
                throw PagamentoException.Invalida("Chave de idempotencia obrigatoria.", "idempotencyKey");
            if (r.Valor is { } v && (v <= 0 || decimal.Round(v, 2) != v))
                throw PagamentoException.Regra(PagamentoCodigos.ValorInvalido, "Valor do estorno invalido.");

            await using var cx = await _db.OpenConnectionAsync(ct);
            PagamentoIntent intent;
            PagamentoRegistro pag;
            Guid estornoId;
            decimal valor;
            string status;
            await using (var tx = await cx.BeginTransactionAsync(ct))
            {
                intent = await PagamentoIntentSql.ObterAsync(cx, tx, r.IntentId, true, ct) ?? throw PagamentoException.NaoEncontrado();
                if (intent.IdEstabelecimento != r.IdEstabelecimento) throw PagamentoException.NaoEncontrado();

                var existente = await cx.QueryFirstOrDefaultAsync<(Guid Id, string Status, decimal Valor, Guid IdPagamento)?>(new CommandDefinition(
                    "SELECT id, status, valor, id_pagamento FROM pagamento_estorno WHERE idempotency_key = @K AND id_estabelecimento = @Loja",
                    new { K = Chave(r), Loja = r.IdEstabelecimento }, tx, cancellationToken: ct));
                if (existente.HasValue)
                {
                    await tx.CommitAsync(ct);
                    return new EstornoResult { EstornoId = existente.Value.Id, Status = existente.Value.Status, Valor = existente.Value.Valor, StatusIntent = intent.Status, ValorEstornadoTotal = intent.ValorEstornado };
                }

                if (intent.Status is not (StatusIntent.Pago or StatusIntent.EstornadoParcial))
                    throw PagamentoException.Estado("So pagamento aprovado pode ser estornado.", intent.Status);
                pag = await cx.QueryFirstOrDefaultAsync<PagamentoRegistro>(new CommandDefinition($@"
SELECT {PagamentoIntentSql.ColunasPagamento} FROM pagamento
 WHERE id_intent = @Id AND status IN ('aprovado', 'estornado_parcial') ORDER BY created_at DESC LIMIT 1 FOR UPDATE", new { intent.Id }, tx, cancellationToken: ct))
                    ?? throw PagamentoException.Estado("Pagamento aprovado nao encontrado.", intent.Status);

                var vivo = await cx.ExecuteScalarAsync<decimal>(new CommandDefinition(
                    "SELECT COALESCE(SUM(valor), 0) FROM pagamento_estorno WHERE id_pagamento = @P AND status NOT IN ('recusado', 'falhou')",
                    new { P = pag.Id }, tx, cancellationToken: ct));
                valor = r.Valor ?? pag.Valor - vivo;
                if (valor <= 0 || vivo + valor > pag.Valor)
                    throw PagamentoException.Regra(PagamentoCodigos.EstornoAcimaDoPago, "O estorno passa do valor pago.");

                estornoId = Guid.NewGuid();
                try
                {
                    await cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO pagamento_estorno (id, id_pagamento, id_estabelecimento, valor, status, motivo, origem, solicitado_por, idempotency_key)
VALUES (@Id, @P, @Loja, @V, 'solicitado', @M, 'usuario', @U, @K)", new
                    {
                        Id = estornoId, P = pag.Id, Loja = intent.IdEstabelecimento, V = valor, M = r.Motivo, U = r.UsuarioId, K = Chave(r)
                    }, tx, cancellationToken: ct));
                }
                catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.CheckViolation)
                {
                    throw PagamentoException.Regra(PagamentoCodigos.EstornoAcimaDoPago, "O estorno passa do valor pago.");
                }

                if (intent.Origem == OrigensPagamento.Chat)
                {
                    var cobranca = await cx.QueryFirstOrDefaultAsync<CobrancaChatRegistro>(new CommandDefinition(@"
SELECT id, id_estabelecimento, conversa_id, id_intent, pedido_id, status, versao FROM cobranca_chat WHERE id_intent = @Id FOR UPDATE",
                        new { intent.Id }, tx, cancellationToken: ct));
                    if (cobranca is { Status: StatusCobranca.Pago })
                        await CobrancaChatSql.MudarStatusAsync(cx, tx, cobranca, StatusCobranca.ReembolsoSolicitado, "usuario", r.UsuarioId, new { valor }, ct);
                }
                await PagamentoAuditoria.RegistrarAsync(cx, tx, intent.IdEstabelecimento, "estorno", estornoId.ToString(), "solicitado", "usuario", r.UsuarioId,
                    new { intentId = intent.Id, valor, r.Motivo }, ct);
                await tx.CommitAsync(ct);
            }

            var cred = await _credenciais.ParaIntentAsync(intent, ct);
            try
            {
                var parcial = valor == pag.Valor ? (decimal?)null : valor;
                // Order (Pix/cartao do checkout) estorna pela order; pagamento da API v1 (assinatura) segue em v1/payments.
                var refund = pag.MpOrderId != null
                    ? await _client.EstornarOrderAsync(cred, pag.MpOrderId, parcial, Chave(r), ct)
                    : await _client.EstornarAsync(cred, pag.MpPaymentId, parcial, Chave(r), ct);
                status = refund.Status is null or "approved" ? StatusEstorno.Concluido : StatusEstorno.Processando;
                await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE pagamento_estorno SET status = @S, mp_refund_id = @R, concluido_em = CASE WHEN @S = 'concluido' THEN NOW() END
 WHERE id = @Id AND status IN ('solicitado', 'processando')", new { Id = estornoId, S = status, R = refund.Id == 0 ? (long?)null : refund.Id }, cancellationToken: ct));
            }
            catch (MercadoPagoApiException ex) when (!ex.Indisponivel)
            {
                await FalharAsync(cx, intent, estornoId, StatusEstorno.Recusado, ex.MpErro, ct);
                throw ex.ParaPagamento();
            }
            catch (MercadoPagoApiException ex)
            {
                // Resultado desconhecido: o estorno fica 'solicitado'; repetir com a mesma chave e seguro (X-Idempotency-Key).
                _logger.LogWarning("[mp.estorno] ev=sem_resposta estorno={Estorno}", estornoId);
                throw ex.ParaPagamento();
            }

            await _sync.SincronizarAsync(cred, pag.MpPaymentId, "usuario", ct);
            var final = (await PagamentoIntentSql.ObterAsync(cx, null, intent.Id, false, ct))!;
            return new EstornoResult { EstornoId = estornoId, Status = status, Valor = valor, StatusIntent = final.Status, ValorEstornadoTotal = final.ValorEstornado };
        }

        private static string Chave(EstornoRequest r) => $"{r.IdEstabelecimento:N}:{r.IdempotencyKey}";

        private static async Task FalharAsync(NpgsqlConnection cx, PagamentoIntent intent, Guid estornoId, string status, string? erro, CancellationToken ct)
        {
            await using var tx = await cx.BeginTransactionAsync(ct);
            await cx.ExecuteAsync(new CommandDefinition("UPDATE pagamento_estorno SET status = @S, erro_mensagem = @E WHERE id = @Id",
                new { Id = estornoId, S = status, E = erro }, tx, cancellationToken: ct));
            var cobranca = await cx.QueryFirstOrDefaultAsync<CobrancaChatRegistro>(new CommandDefinition(@"
SELECT id, id_estabelecimento, conversa_id, id_intent, pedido_id, status, versao FROM cobranca_chat WHERE id_intent = @Id FOR UPDATE",
                new { intent.Id }, tx, cancellationToken: ct));
            if (cobranca is { Status: StatusCobranca.ReembolsoSolicitado })
                await CobrancaChatSql.MudarStatusAsync(cx, tx, cobranca, StatusCobranca.Pago, "sistema", null, new { estorno = status }, ct);
            await PagamentoAuditoria.RegistrarAsync(cx, tx, intent.IdEstabelecimento, "estorno", estornoId.ToString(), status, "sistema", null, new { erro }, ct);
            await tx.CommitAsync(ct);
        }
    }
}
