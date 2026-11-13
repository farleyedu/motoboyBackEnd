using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Payments.InfinitePay.Cliente;
using APIBack.Payments.MercadoPago;
using APIBack.Payments.Provedores;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace APIBack.Payments.InfinitePay.Webhooks
{
    public interface IInfinitePayWebhookProcessor
    {
        /// <summary>Reserva e processa avisos vencidos (varios workers/instancias nao pegam o mesmo). Devolve quantos processou.</summary>
        Task<int> ProcessarPendentesAsync(int lote, CancellationToken ct = default);
        Task ProcessarAsync(Guid eventoId, CancellationToken ct = default);
        /// <summary>Replay seguro: devolve a fila um aviso descartado ou em pendencia da PROPRIA loja. A confirmacao continua passando por /payment_check e e idempotente.</summary>
        Task<bool> ReenfileirarAsync(Guid eventoId, Guid idEstabelecimento, CancellationToken ct = default);
    }

    /// <summary>
    /// Confirma pagamento InfinitePay. O aviso nunca prova nada: o handle e a conta saem do checkout gravado no servidor, o
    /// transaction_nsu/slug do aviso so entram como parametros de POST /payment_check, e o valor conferido e o ORIGINAL cobrado
    /// (paid_amount e so informativo). A aplicacao e uma unica transacao com a intencao e o checkout travados.
    /// </summary>
    public sealed class InfinitePayWebhookProcessor : IInfinitePayWebhookProcessor
    {
        private const int ReservaMinutos = 2;
        private readonly NpgsqlDataSource _db;
        private readonly IPagamentoSegredoProtector _segredos;
        private readonly IInfinitePayClient _client;
        private readonly IEnumerable<IPagamentoEfeitoHandler> _efeitos;
        private readonly ILogger<InfinitePayWebhookProcessor> _logger;

        public InfinitePayWebhookProcessor(NpgsqlDataSource db, IPagamentoSegredoProtector segredos, IInfinitePayClient client,
            IEnumerable<IPagamentoEfeitoHandler> efeitos, ILogger<InfinitePayWebhookProcessor> logger)
        {
            _db = db;
            _segredos = segredos;
            _client = client;
            _efeitos = efeitos;
            _logger = logger;
        }

        public async Task<int> ProcessarPendentesAsync(int lote, CancellationToken ct = default)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            var ids = (await cx.QueryAsync<Guid>(new CommandDefinition(@"
UPDATE infinitepay_webhook_evento SET status = 'processando', tentativas = tentativas + 1, bloqueado_ate = NOW() + make_interval(mins => @M), updated_at = NOW()
 WHERE id IN (SELECT id FROM infinitepay_webhook_evento
               WHERE (status = 'pendente' AND proxima_tentativa_em <= NOW()) OR (status = 'processando' AND bloqueado_ate < NOW())
               ORDER BY proxima_tentativa_em LIMIT @L FOR UPDATE SKIP LOCKED)
RETURNING id", new { M = ReservaMinutos, L = Math.Clamp(lote, 1, 100) }, cancellationToken: ct))).ToList();

            foreach (var id in ids)
            {
                try { await ProcessarAsync(id, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    _logger.LogWarning("[ip.webhook] ev=erro_inesperado evento={Evento} erro={Erro}", id, ex.GetType().Name);
                    await ReagendarAsync(id, ex.GetType().Name, CancellationToken.None);
                }
            }
            return ids.Count;
        }

        public async Task ProcessarAsync(Guid eventoId, CancellationToken ct = default)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            var ev = await cx.QueryFirstOrDefaultAsync<EventoRow>(new CommandDefinition(
                "SELECT id, id_estabelecimento, id_intent, order_nsu, tipo_evento, transaction_nsu, invoice_slug, tentativas, status FROM infinitepay_webhook_evento WHERE id = @Id",
                new { Id = eventoId }, cancellationToken: ct));
            if (ev == null || ev.Status is StatusWebhookEvento.Processado or StatusWebhookEvento.Descartado or StatusWebhookEvento.Pendencia) return;

            var ck = await cx.QueryFirstOrDefaultAsync<CheckoutRow>(new CommandDefinition(@"
SELECT c.id, c.id_intent, c.id_estabelecimento, c.id_conta_versao, c.order_nsu, c.handle_snapshot, c.valor_centavos, c.confirmado_em, c.transaction_nsu
  FROM infinitepay_checkout c WHERE c.order_nsu = @N", new { N = ev.OrderNsu }, cancellationToken: ct));
            // Correlacao interna: aviso, checkout, intencao e loja precisam ser a mesma cadeia.
            if (ck == null || ck.IdIntent != ev.IdIntent || ck.IdEstabelecimento != ev.IdEstabelecimento)
            {
                await FinalizarAsync(cx, null, ev.Id, StatusWebhookEvento.Descartado, "correlacao_inconsistente", ct);
                return;
            }

            switch (InfinitePayWebhookRegras.Classificar(ev.TipoEvento, ev.TransactionNsu, ev.InvoiceSlug))
            {
                case AcaoEvento.Ignorar:
                    await FinalizarAsync(cx, null, ev.Id, StatusWebhookEvento.Processado, $"ignorado_{ev.TipoEvento ?? "sem_tipo"}", ct);
                    return;
                case AcaoEvento.PendenciaCancelamento:
                    await AbrirPendenciaEFinalizarAsync(cx, ev, TiposPendenciaInfinitePay.CancelamentoNaoVerificavel, new { tipo = ev.TipoEvento }, ct);
                    return;
                case AcaoEvento.PendenciaEstorno:
                    await AbrirPendenciaEFinalizarAsync(cx, ev, TiposPendenciaInfinitePay.EstornoNaoVerificavel, new { tipo = ev.TipoEvento }, ct);
                    return;
                case AcaoEvento.PendenciaSemIdentificadores:
                    await AbrirPendenciaEFinalizarAsync(cx, ev, TiposPendenciaInfinitePay.PagoSemIdentificadores,
                        new { tipo = ev.TipoEvento, recuperar = "comprovante_ou_app_infinitepay" }, ct);
                    return;
            }

            await VerificarEConfirmarAsync(cx, ev, ck, ct);
        }

        private async Task VerificarEConfirmarAsync(NpgsqlConnection cx, EventoRow ev, CheckoutRow ck, CancellationToken ct)
        {
            var conta = await cx.QueryFirstOrDefaultAsync<ContaRow>(new CommandDefinition(@"
SELECT id, infinite_tag_snapshot, api_key_cifrada FROM infinitepay_conta_versao WHERE id = @V AND id_estabelecimento = @Loja",
                new { V = ck.IdContaVersao, Loja = ck.IdEstabelecimento }, cancellationToken: ct));
            if (conta == null)
            {
                await AbrirPendenciaEFinalizarAsync(cx, ev, TiposPendenciaInfinitePay.VerificacaoFalhou, new { motivo = "conta_nao_encontrada" }, ct);
                return;
            }

            string? chave = null;
            if (!string.IsNullOrEmpty(conta.ApiKeyCifrada))
            {
                chave = _segredos.Configurado ? _segredos.Revelar(conta.ApiKeyCifrada, SegredoContexto.InfinitePayApiKey(conta.Id)) : null;
                if (chave == null)
                {
                    await ReagendarOuPendenciaAsync(cx, ev, "chave_indisponivel", ct);
                    return;
                }
            }

            InfinitePayPaymentCheck check;
            try
            {
                check = await _client.VerificarPagamentoAsync(conta.InfiniteTagSnapshot, ck.OrderNsu, ev.TransactionNsu!, ev.InvoiceSlug!, chave, ct);
            }
            catch (InfinitePayApiException ex) when (ex.Indisponivel)
            {
                await ReagendarOuPendenciaAsync(cx, ev, $"http_{ex.StatusHttp}", ct);
                return;
            }
            catch (InfinitePayApiException ex)
            {
                // Recusa definitiva (ex.: 403 de conta/chave): problema de configuracao, nao de pagamento. Vai para suporte, com replay possivel.
                await AbrirPendenciaEFinalizarAsync(cx, ev, TiposPendenciaInfinitePay.VerificacaoFalhou, new { http = ex.StatusHttp, erro = ex.Erro }, ct);
                return;
            }

            switch (InfinitePayWebhookRegras.Avaliar(check, ck.ValorCentavos))
            {
                case AvaliacaoPagamento.NaoConfirmado:
                    // Aviso forjado ou pagamento ainda nao liquidado do lado deles: poucas novas consultas, depois descarta (sem pendencia: nao ha dinheiro provado).
                    if (ev.Tentativas < InfinitePayWebhookRegras.MaxTentativasNaoConfirmado) await ReagendarAsync(ev.Id, "nao_confirmado", ct);
                    else await FinalizarAsync(cx, null, ev.Id, StatusWebhookEvento.Descartado, "nao_confirmado", ct);
                    return;
                case AvaliacaoPagamento.ValorDivergente:
                    await AbrirPendenciaEFinalizarAsync(cx, ev, TiposPendenciaInfinitePay.ValorDivergente,
                        new { cobradoCentavos = ck.ValorCentavos, confirmadoCentavos = check.Amount }, ct);
                    return;
            }

            await AplicarConfirmacaoAsync(cx, ev, ck, check, ct);
        }

        private async Task AplicarConfirmacaoAsync(NpgsqlConnection cx, EventoRow ev, CheckoutRow ck, InfinitePayPaymentCheck check, CancellationToken ct)
        {
            await using var tx = await cx.BeginTransactionAsync(ct);
            var intent = await PagamentoIntentSql.ObterAsync(cx, tx, ck.IdIntent, true, ct)
                         ?? throw new InvalidOperationException("Intencao nao encontrada.");
            var atual = await cx.QueryFirstAsync<CheckoutRow>(new CommandDefinition(@"
SELECT id, id_intent, id_estabelecimento, id_conta_versao, order_nsu, handle_snapshot, valor_centavos, confirmado_em, transaction_nsu
  FROM infinitepay_checkout WHERE id = @Id FOR UPDATE", new { ck.Id }, tx, cancellationToken: ct));

            if (intent.IdEstabelecimento != ck.IdEstabelecimento || intent.Provedor != ProvedoresPagamento.InfinitePay)
            {
                await FinalizarAsync(cx, tx, ev.Id, StatusWebhookEvento.Descartado, "correlacao_inconsistente", ct);
                await tx.CommitAsync(ct);
                return;
            }

            if (atual.ConfirmadoEm != null)
            {
                if (atual.TransactionNsu == ev.TransactionNsu)
                {
                    await FinalizarAsync(cx, tx, ev.Id, StatusWebhookEvento.Processado, "ja_confirmado", ct);
                }
                else
                {
                    // Segunda transacao realmente paga para a mesma cobranca: dinheiro a mais, so suporte devolve.
                    await PendenciaAsync(cx, tx, ev, TiposPendenciaInfinitePay.PagamentoDuplicado, new { paidAmountCentavos = check.PaidAmount }, ct);
                    await FinalizarAsync(cx, tx, ev.Id, StatusWebhookEvento.Pendencia, TiposPendenciaInfinitePay.PagamentoDuplicado, ct);
                }
                await tx.CommitAsync(ct);
                return;
            }

            var usada = await cx.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS (SELECT 1 FROM infinitepay_checkout WHERE transaction_nsu = @T AND id <> @Id)", new { T = ev.TransactionNsu, ck.Id }, tx, cancellationToken: ct));
            if (usada)
            {
                await PendenciaAsync(cx, tx, ev, TiposPendenciaInfinitePay.PagamentoDuplicado, new { motivo = "transacao_ja_usada_em_outra_cobranca" }, ct);
                await FinalizarAsync(cx, tx, ev.Id, StatusWebhookEvento.Pendencia, TiposPendenciaInfinitePay.PagamentoDuplicado, ct);
                await tx.CommitAsync(ct);
                return;
            }

            var statusAnterior = intent.Status;
            if (!StatusIntent.FamiliaPaga(statusAnterior) && !PagamentoStatusRules.PodeIntent(statusAnterior, StatusIntent.Pago))
            {
                await PendenciaAsync(cx, tx, ev, TiposPendenciaInfinitePay.PagoEmEstadoFinal, new { statusIntent = statusAnterior }, ct);
                await FinalizarAsync(cx, tx, ev.Id, StatusWebhookEvento.Pendencia, TiposPendenciaInfinitePay.PagoEmEstadoFinal, ct);
                await tx.CommitAsync(ct);
                return;
            }

            await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE infinitepay_checkout SET confirmado_em = NOW(), transaction_nsu = @T, invoice_slug = @S, capture_method = @M,
       amount_confirmado_centavos = @A, paid_amount_centavos = @P, updated_at = NOW() WHERE id = @Id",
                new { T = ev.TransactionNsu, S = ev.InvoiceSlug, M = check.CaptureMethod, A = check.Amount, P = check.PaidAmount, ck.Id }, tx, cancellationToken: ct));

            // Monotonico: quem ja esta na familia paga nao volta a "pago" nem dispara efeito de novo.
            if (!StatusIntent.FamiliaPaga(statusAnterior))
            {
                // valor_pago = valor ORIGINAL cobrado (o que a loja vende); a taxa repassada ao comprador fica so em paid_amount_centavos.
                intent.Status = StatusIntent.Pago;
                intent.ValorPago = intent.Valor;
                intent.PagoEm ??= DateTime.UtcNow;
                intent.Versao++;
                await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE pagamento_intent SET status = @Status, versao = @Versao, valor_pago = @ValorPago, pago_em = @PagoEm, updated_at = NOW() WHERE id = @Id",
                    intent, tx, cancellationToken: ct));

                Guid? cobrancaId = null;
                foreach (var efeito in _efeitos.Where(e => e.Origem == intent.Origem))
                    cobrancaId = await efeito.AplicarAsync(cx, tx, intent, statusAnterior, "webhook_infinitepay", ct) ?? cobrancaId;
                await PagamentoOutbox.PagamentoAtualizadoAsync(cx, tx, intent, cobrancaId, ct);
                await PagamentoAuditoria.RegistrarAsync(cx, tx, intent.IdEstabelecimento, "pagamento_intent", intent.Id.ToString(), "status", "sistema", null,
                    new { de = statusAnterior, para = StatusIntent.Pago, provedor = ProvedoresPagamento.InfinitePay, valorPago = intent.ValorPago, paidAmountCentavos = check.PaidAmount, metodo = check.CaptureMethod }, ct);
            }

            await FinalizarAsync(cx, tx, ev.Id, StatusWebhookEvento.Processado, StatusIntent.FamiliaPaga(statusAnterior) ? "ja_pago" : "confirmado", ct);
            await tx.CommitAsync(ct);
            _logger.LogInformation("[ip.webhook] ev=confirmado intent={Intent} evento={Evento}", intent.Id, ev.Id);
        }

        public async Task<bool> ReenfileirarAsync(Guid eventoId, Guid idEstabelecimento, CancellationToken ct = default)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            return await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE infinitepay_webhook_evento SET status = 'pendente', tentativas = 0, proxima_tentativa_em = NOW(), bloqueado_ate = NULL, updated_at = NOW()
 WHERE id = @Id AND id_estabelecimento = @Loja AND status IN ('descartado', 'pendencia')", new { Id = eventoId, Loja = idEstabelecimento }, cancellationToken: ct)) > 0;
        }

        // ---- apoio -----------------------------------------------------------------------------------------------------------------

        private async Task ReagendarOuPendenciaAsync(NpgsqlConnection cx, EventoRow ev, string motivo, CancellationToken ct)
        {
            if (ev.Tentativas >= InfinitePayWebhookRegras.MaxTentativas)
                await AbrirPendenciaEFinalizarAsync(cx, ev, TiposPendenciaInfinitePay.VerificacaoFalhou, new { motivo, tentativas = ev.Tentativas }, ct);
            else
                await ReagendarAsync(ev.Id, motivo, ct);
        }

        private async Task ReagendarAsync(Guid eventoId, string motivo, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            var tentativas = await cx.ExecuteScalarAsync<int>(new CommandDefinition("SELECT tentativas FROM infinitepay_webhook_evento WHERE id = @Id", new { Id = eventoId }, cancellationToken: ct));
            if (tentativas >= InfinitePayWebhookRegras.MaxTentativas)
            {
                await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE infinitepay_webhook_evento SET status = 'pendencia', resultado = 'tentativas_esgotadas', ultimo_erro = @M, bloqueado_ate = NULL, updated_at = NOW() WHERE id = @Id",
                    new { Id = eventoId, M = motivo }, cancellationToken: ct));
                return;
            }
            await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE infinitepay_webhook_evento SET status = 'pendente', ultimo_erro = @M, bloqueado_ate = NULL,
       proxima_tentativa_em = NOW() + make_interval(secs => @S), updated_at = NOW() WHERE id = @Id",
                new { Id = eventoId, M = motivo, S = InfinitePayWebhookRegras.Backoff(tentativas).TotalSeconds }, cancellationToken: ct));
        }

        private async Task AbrirPendenciaEFinalizarAsync(NpgsqlConnection cx, EventoRow ev, string tipo, object detalhe, CancellationToken ct)
        {
            await using var tx = await cx.BeginTransactionAsync(ct);
            await PendenciaAsync(cx, tx, ev, tipo, detalhe, ct);
            await FinalizarAsync(cx, tx, ev.Id, StatusWebhookEvento.Pendencia, tipo, ct);
            await tx.CommitAsync(ct);
            _logger.LogWarning("[ip.webhook] ev=pendencia tipo={Tipo} intent={Intent} evento={Evento}", tipo, ev.IdIntent, ev.Id);
        }

        private static async Task PendenciaAsync(NpgsqlConnection cx, NpgsqlTransaction tx, EventoRow ev, string tipo, object detalhe, CancellationToken ct)
        {
            var statusIntent = await cx.ExecuteScalarAsync<string?>(new CommandDefinition("SELECT status FROM pagamento_intent WHERE id = @I", new { I = ev.IdIntent }, tx, cancellationToken: ct));
            var criada = await cx.ExecuteScalarAsync<Guid?>(new CommandDefinition(@"
INSERT INTO infinitepay_pendencia (id, id_estabelecimento, id_intent, id_evento, tipo, detalhe)
VALUES (@Id, @Loja, @Intent, @Evento, @Tipo, CAST(@Detalhe AS jsonb))
ON CONFLICT (id_evento, tipo) DO NOTHING RETURNING id", new
            {
                Id = Guid.NewGuid(), Loja = ev.IdEstabelecimento, Intent = ev.IdIntent, Evento = ev.Id, Tipo = tipo,
                Detalhe = System.Text.Json.JsonSerializer.Serialize(new { detalhe, statusIntent })
            }, tx, cancellationToken: ct));
            if (criada.HasValue)
                await PagamentoAuditoria.RegistrarAsync(cx, tx, ev.IdEstabelecimento, "pagamento_intent", ev.IdIntent.ToString(), "pendencia_infinitepay", "sistema", null,
                    new { tipo, eventoId = ev.Id }, ct);
        }

        private static Task FinalizarAsync(NpgsqlConnection cx, NpgsqlTransaction? tx, Guid id, string status, string resultado, CancellationToken ct) =>
            cx.ExecuteAsync(new CommandDefinition(@"
UPDATE infinitepay_webhook_evento SET status = @S, resultado = @R, processado_em = NOW(), bloqueado_ate = NULL, updated_at = NOW() WHERE id = @Id",
                new { Id = id, S = status, R = resultado }, tx, cancellationToken: ct));

        private sealed class EventoRow
        {
            public Guid Id { get; set; }
            public Guid IdEstabelecimento { get; set; }
            public Guid IdIntent { get; set; }
            public string OrderNsu { get; set; } = string.Empty;
            public string? TipoEvento { get; set; }
            public string? TransactionNsu { get; set; }
            public string? InvoiceSlug { get; set; }
            public int Tentativas { get; set; }
            public string Status { get; set; } = string.Empty;
        }

        private sealed class CheckoutRow
        {
            public Guid Id { get; set; }
            public Guid IdIntent { get; set; }
            public Guid IdEstabelecimento { get; set; }
            public Guid IdContaVersao { get; set; }
            public string OrderNsu { get; set; } = string.Empty;
            public string HandleSnapshot { get; set; } = string.Empty;
            public long ValorCentavos { get; set; }
            public DateTime? ConfirmadoEm { get; set; }
            public string? TransactionNsu { get; set; }
        }

        private sealed class ContaRow
        {
            public Guid Id { get; set; }
            public string InfiniteTagSnapshot { get; set; } = string.Empty;
            public string? ApiKeyCifrada { get; set; }
        }
    }
}
