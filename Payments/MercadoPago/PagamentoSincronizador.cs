using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace APIBack.Payments.MercadoPago
{
    public enum ResultadoSincronizacao
    {
        Aplicado,
        SemMudanca,
        /// <summary>Retrato do MP nao bate com a intencao (titularidade, valor, ambiente, moeda): nada muda.</summary>
        Rejeitado,
        /// <summary>external_reference desconhecido ou de fora da Zippy.</summary>
        Desconhecido
    }

    public sealed record SincronizacaoResultado(ResultadoSincronizacao Resultado, Guid? IntentId, Guid? PagamentoId, string? StatusIntent, string? Motivo);

    /// <summary>
    /// Aplica o retrato de um pagamento do MP (sempre buscado server-to-server com a credencial da conta dona) sobre a intencao, de forma
    /// idempotente e transacional: linhas travadas, evento velho nao retrocede status, efeitos (cardapio/chat/outbox/auditoria) so quando
    /// algo mudou. Usado pela criacao (Pix/cartao), pelo webhook e pelo conciliador.
    /// </summary>
    public interface IPagamentoSincronizador
    {
        Task<SincronizacaoResultado> SincronizarAsync(CredencialRecebedora credencial, long mpPaymentId, string ator, CancellationToken ct = default);
        /// <summary>Aviso do topico 'order': busca a order (ORD...) no MP e aplica o retrato do pagamento dela.</summary>
        Task<SincronizacaoResultado> SincronizarOrderAsync(CredencialRecebedora credencial, string orderId, string ator, CancellationToken ct = default);
        Task<SincronizacaoResultado> AplicarAsync(CredencialRecebedora credencial, MpPagamento mp, Guid? idTentativa, string ator, CancellationToken ct = default);
        /// <summary>Mesmo que <see cref="AplicarAsync"/>, dentro de uma transacao do chamador (intencao ja travada por ele).</summary>
        Task<SincronizacaoResultado> AplicarNaTransacaoAsync(NpgsqlConnection cx, NpgsqlTransaction tx, PagamentoIntent intent, CredencialRecebedora credencial,
            MpPagamento mp, Guid? idTentativa, string ator, CancellationToken ct = default);
        Task MarcarIntentAsync(NpgsqlConnection cx, NpgsqlTransaction tx, PagamentoIntent intent, string novoStatus, string ator, int? usuarioId, CancellationToken ct = default);
    }

    public sealed class PagamentoSincronizador : IPagamentoSincronizador
    {
        private readonly NpgsqlDataSource _db;
        private readonly IMercadoPagoClient _client;
        private readonly IEnumerable<IPagamentoEfeitoHandler> _efeitos;
        private readonly MercadoPagoOptions _options;
        private readonly ILogger<PagamentoSincronizador> _logger;

        public PagamentoSincronizador(NpgsqlDataSource db, IMercadoPagoClient client, IEnumerable<IPagamentoEfeitoHandler> efeitos,
            IOptions<MercadoPagoOptions> options, ILogger<PagamentoSincronizador> logger)
        {
            _db = db;
            _client = client;
            _efeitos = efeitos;
            _options = options.Value;
            _logger = logger;
        }

        public async Task<SincronizacaoResultado> SincronizarAsync(CredencialRecebedora credencial, long mpPaymentId, string ator, CancellationToken ct = default)
        {
            // Pagamento que nasceu numa order se consulta pela order; os da API v1 (assinaturas) seguem em v1/payments.
            string? orderId;
            await using (var cx = await _db.OpenConnectionAsync(ct))
                orderId = await cx.ExecuteScalarAsync<string?>(new CommandDefinition(
                    "SELECT mp_order_id FROM pagamento WHERE ambiente = @Amb AND mp_payment_id = @Mp", new { Amb = credencial.Ambiente, Mp = mpPaymentId }, cancellationToken: ct));

            MpPagamento mp;
            try
            {
                mp = orderId != null
                    ? await _client.ObterOrderAsync(credencial, orderId, ct)
                    : await _client.ObterPagamentoAsync(credencial, mpPaymentId, ct);
            }
            catch (MercadoPagoApiException ex) when (ex.StatusHttp == 404)
            {
                return new SincronizacaoResultado(ResultadoSincronizacao.Desconhecido, null, null, null, "pagamento_inexistente_na_conta");
            }
            return await AplicarAsync(credencial, mp, null, ator, ct);
        }

        public async Task<SincronizacaoResultado> SincronizarOrderAsync(CredencialRecebedora credencial, string orderId, string ator, CancellationToken ct = default)
        {
            MpPagamento mp;
            try
            {
                mp = await _client.ObterOrderAsync(credencial, orderId, ct);
            }
            catch (MercadoPagoApiException ex) when (ex.StatusHttp == 404)
            {
                return new SincronizacaoResultado(ResultadoSincronizacao.Desconhecido, null, null, null, "pagamento_inexistente_na_conta");
            }
            return await AplicarAsync(credencial, mp, null, ator, ct);
        }

        public async Task<SincronizacaoResultado> AplicarAsync(CredencialRecebedora credencial, MpPagamento mp, Guid? idTentativa, string ator, CancellationToken ct = default)
        {
            if (!PagamentoIntentSql.ReferenciaNossa(mp.ExternalReference))
                return new SincronizacaoResultado(ResultadoSincronizacao.Desconhecido, null, null, null, "referencia_desconhecida");

            await using var cx = await _db.OpenConnectionAsync(ct);
            await using var tx = await cx.BeginTransactionAsync(ct);
            var intent = await PagamentoIntentSql.PorReferenciaAsync(cx, tx, mp.ExternalReference!, ct);
            if (intent == null)
                return new SincronizacaoResultado(ResultadoSincronizacao.Desconhecido, null, null, null, "referencia_desconhecida");

            var r = await AplicarNaTransacaoAsync(cx, tx, intent, credencial, mp, idTentativa, ator, ct);
            await tx.CommitAsync(ct);
            return r;
        }

        /// <summary>Por que o retrato nao pode ser aceito (null = ok). Publico para os testes.</summary>
        public static string? ValidarRetrato(PagamentoIntent intent, CredencialRecebedora credencial, MpPagamento mp, bool exigirLiveMode)
        {
            if (mp.ExternalReference != intent.ExternalReference) return "referencia_divergente";
            if (credencial.Ambiente != intent.Ambiente) return "ambiente_divergente";
            if (CredencialRegras.ContaTipo(credencial.Contexto) != intent.ContaTipo) return "conta_divergente";
            if (credencial.Contexto == ContextoCredencial.Loja && credencial.IdContaLoja != intent.IdContaLoja) return "conta_divergente";
            if (credencial.Contexto == ContextoCredencial.RecebedorZippy && credencial.IdContaZippy != intent.IdContaZippy) return "conta_divergente";
            if (mp.CollectorId.HasValue && credencial.MpUserId != 0 && mp.CollectorId.Value != credencial.MpUserId) return "titularidade_divergente";
            if (decimal.Round(mp.TransactionAmount, 2) != decimal.Round(intent.Valor, 2)) return "valor_divergente";
            if (!string.IsNullOrEmpty(mp.CurrencyId) && mp.CurrencyId != intent.Moeda) return "moeda_divergente";
            if (exigirLiveMode && mp.LiveMode != Ambientes.LiveMode(intent.Ambiente)) return "live_mode_divergente";
            return null;
        }

        public async Task<SincronizacaoResultado> AplicarNaTransacaoAsync(NpgsqlConnection cx, NpgsqlTransaction tx, PagamentoIntent intent,
            CredencialRecebedora credencial, MpPagamento mp, Guid? idTentativa, string ator, CancellationToken ct = default)
        {
            var motivo = ValidarRetrato(intent, credencial, mp, _options.ExigirLiveModeDoAmbiente);
            if (motivo != null)
            {
                _logger.LogWarning("[mp.sync] ev=retrato_rejeitado intent={Intent} mp={Mp} motivo={Motivo}", intent.Id, mp.Id, motivo);
                await PagamentoAuditoria.RegistrarAsync(cx, tx, intent.IdEstabelecimento, "pagamento_intent", intent.Id.ToString(), "retrato_rejeitado", ator, null,
                    new { mpPaymentId = mp.Id, motivo }, ct);
                return new SincronizacaoResultado(ResultadoSincronizacao.Rejeitado, intent.Id, null, intent.Status, motivo);
            }

            var estornadoMp = (mp.Refunds ?? new List<MpRefund>()).Where(x => x.Status is null or "approved").Sum(x => x.Amount);
            if (mp.TransactionAmountRefunded is { } refunded && refunded > estornadoMp) estornadoMp = refunded;
            var statusNovo = PagamentoStatusRules.Traduzir(mp.Status, mp.StatusDetail, mp.TransactionAmount, estornadoMp);
            var atualizadoMp = mp.DateLastUpdated?.UtcDateTime;
            var metodo = mp.PaymentMethodId == "pix" || mp.PaymentTypeId == "bank_transfer" ? MetodosPagamento.Pix : MetodosPagamento.Cartao;

            var pag = await cx.QueryFirstOrDefaultAsync<PagamentoRegistro>(new CommandDefinition(
                $"SELECT {PagamentoIntentSql.ColunasPagamento} FROM pagamento WHERE ambiente = @Amb AND mp_payment_id = @Mp FOR UPDATE",
                new { Amb = intent.Ambiente, Mp = mp.Id }, tx, cancellationToken: ct));

            var mudouPagamento = false;
            if (pag == null)
            {
                pag = new PagamentoRegistro
                {
                    Id = Guid.NewGuid(), IdIntent = intent.Id, IdTentativa = idTentativa, IdEstabelecimento = intent.IdEstabelecimento, Ambiente = intent.Ambiente,
                    MpPaymentId = mp.Id, MpOrderId = mp.OrderId, Metodo = metodo, Versao = 1
                };
                Preencher(pag, mp, statusNovo, estornadoMp);
                await cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO pagamento (id, id_intent, id_tentativa, id_estabelecimento, ambiente, mp_payment_id, mp_order_id, mp_collector_id, metodo, payment_method_id, payment_type_id,
                       status, mp_status, mp_status_detail, valor, valor_estornado, moeda, pix_copia_cola, pix_qr_code_base64, pix_ticket_url, cartao_bandeira,
                       cartao_final, parcelas, live_mode, expira_em, aprovado_em, mp_atualizado_em, versao, conciliado_em)
VALUES (@Id, @IdIntent, @IdTentativa, @IdEstabelecimento, @Ambiente, @MpPaymentId, @MpOrderId, @MpCollectorId, @Metodo, @PaymentMethodId, @PaymentTypeId,
        @Status, @MpStatus, @MpStatusDetail, @Valor, @ValorEstornado, @Moeda, @PixCopiaCola, @PixQrCodeBase64, @PixTicketUrl, @CartaoBandeira,
        @CartaoFinal, @Parcelas, @LiveMode, @ExpiraEm, @AprovadoEm, @MpAtualizadoEm, 1, NOW())", pag, tx, cancellationToken: ct));
                if (idTentativa.HasValue)
                {
                    await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE pagamento_tentativa SET status = 'criada', mp_payment_id = @Mp, concluida_em = NOW() WHERE id = @Id AND status <> 'criada'",
                        new { Id = idTentativa, Mp = mp.Id }, tx, cancellationToken: ct));
                }
                mudouPagamento = true;
            }
            else if (PagamentoStatusRules.DeveAplicar(pag.Status, pag.MpAtualizadoEm, statusNovo, atualizadoMp, pag.ValorEstornado, estornadoMp))
            {
                Preencher(pag, mp, statusNovo, estornadoMp);
                pag.Versao++;
                await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE pagamento SET status = @Status, mp_status = @MpStatus, mp_status_detail = @MpStatusDetail, valor_estornado = @ValorEstornado,
       mp_order_id = COALESCE(mp_order_id, @MpOrderId), aprovado_em = COALESCE(aprovado_em, @AprovadoEm), mp_atualizado_em = @MpAtualizadoEm, versao = @Versao,
       pix_copia_cola = COALESCE(pix_copia_cola, @PixCopiaCola), pix_qr_code_base64 = COALESCE(pix_qr_code_base64, @PixQrCodeBase64),
       pix_ticket_url = COALESCE(pix_ticket_url, @PixTicketUrl), cartao_final = COALESCE(cartao_final, @CartaoFinal),
       conciliado_em = NOW(), updated_at = NOW()
 WHERE id = @Id", pag, tx, cancellationToken: ct));
                mudouPagamento = true;
            }
            else
            {
                await cx.ExecuteAsync(new CommandDefinition("UPDATE pagamento SET conciliado_em = NOW() WHERE id = @Id", new { pag.Id }, tx, cancellationToken: ct));
            }

            if (!mudouPagamento)
                return new SincronizacaoResultado(ResultadoSincronizacao.SemMudanca, intent.Id, pag.Id, intent.Status, null);

            await RegistrarEstornosAsync(cx, tx, pag, mp, ct);
            await RegistrarDisputaAsync(cx, tx, pag, ct);

            var ultimo = await cx.ExecuteScalarAsync<long?>(new CommandDefinition(
                "SELECT mp_payment_id FROM pagamento WHERE id_intent = @Id ORDER BY created_at DESC, mp_payment_id DESC LIMIT 1",
                new { intent.Id }, tx, cancellationToken: ct));
            var statusAnterior = intent.Status;
            var statusIntent = PagamentoStatusRules.StatusDaIntent(intent.Status, pag.Status, ultimo == mp.Id);
            var valorPago = pag.Status is StatusPagamento.Aprovado or StatusPagamento.EstornadoParcial or StatusPagamento.Estornado or StatusPagamento.EmMediacao or StatusPagamento.Contestado
                ? pag.Valor : intent.ValorPago;
            var estornadoIntent = Math.Min(valorPago, pag.ValorEstornado);

            if (statusIntent != statusAnterior || valorPago != intent.ValorPago || estornadoIntent != intent.ValorEstornado)
            {
                intent.Status = statusIntent;
                intent.ValorPago = valorPago;
                intent.ValorEstornado = estornadoIntent;
                intent.Versao++;
                if (statusIntent == StatusIntent.Pago && intent.PagoEm == null) intent.PagoEm = pag.AprovadoEm ?? DateTime.UtcNow;
                if (pag.Metodo == MetodosPagamento.Pix && pag.ExpiraEm.HasValue && StatusIntent.Aberto(statusIntent)) intent.ExpiraEm = pag.ExpiraEm;
                await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE pagamento_intent SET status = @Status, versao = @Versao, valor_pago = @ValorPago, valor_estornado = @ValorEstornado, pago_em = @PagoEm,
       expira_em = @ExpiraEm, updated_at = NOW()
 WHERE id = @Id", intent, tx, cancellationToken: ct));

                Guid? cobrancaId = null;
                foreach (var efeito in _efeitos.Where(e => e.Origem == intent.Origem))
                    cobrancaId = await efeito.AplicarAsync(cx, tx, intent, statusAnterior, ator, ct) ?? cobrancaId;

                await PagamentoOutbox.PagamentoAtualizadoAsync(cx, tx, intent, cobrancaId, ct);
                await PagamentoAuditoria.RegistrarAsync(cx, tx, intent.IdEstabelecimento, "pagamento_intent", intent.Id.ToString(), "status", ator, null,
                    new { de = statusAnterior, para = statusIntent, mpPaymentId = mp.Id, pagamento = pag.Status, valorPago, valorEstornado = estornadoIntent }, ct);
            }
            else
            {
                await PagamentoAuditoria.RegistrarAsync(cx, tx, intent.IdEstabelecimento, "pagamento", pag.Id.ToString(), "status", ator, null,
                    new { mpPaymentId = mp.Id, pagamento = pag.Status }, ct);
            }

            _logger.LogInformation("[mp.sync] ev=aplicado intent={Intent} mp={Mp} pagamento={Status} intent_status={IntentStatus} ator={Ator}",
                intent.Id, mp.Id, pag.Status, intent.Status, ator);
            return new SincronizacaoResultado(ResultadoSincronizacao.Aplicado, intent.Id, pag.Id, intent.Status, null);
        }

        /// <summary>
        /// Muda a intencao sem retrato do MP (ex.: cancelar uma intencao que nunca chegou a gerar pagamento). Mesmos efeitos e outbox.
        /// Intencao precisa estar travada pelo chamador.
        /// </summary>
        public async Task MarcarIntentAsync(NpgsqlConnection cx, NpgsqlTransaction tx, PagamentoIntent intent, string novoStatus, string ator, int? usuarioId, CancellationToken ct = default)
        {
            if (intent.Status == novoStatus) return;
            if (!PagamentoStatusRules.PodeIntent(intent.Status, novoStatus))
                throw PagamentoException.Estado($"Pagamento em '{intent.Status}' nao pode ir para '{novoStatus}'.", intent.Status);
            var anterior = intent.Status;
            intent.Status = novoStatus;
            intent.Versao++;
            await cx.ExecuteAsync(new CommandDefinition(
                "UPDATE pagamento_intent SET status = @Status, versao = @Versao, updated_at = NOW() WHERE id = @Id", intent, tx, cancellationToken: ct));
            Guid? cobrancaId = null;
            foreach (var efeito in _efeitos.Where(e => e.Origem == intent.Origem))
                cobrancaId = await efeito.AplicarAsync(cx, tx, intent, anterior, ator, ct) ?? cobrancaId;
            await PagamentoOutbox.PagamentoAtualizadoAsync(cx, tx, intent, cobrancaId, ct);
            await PagamentoAuditoria.RegistrarAsync(cx, tx, intent.IdEstabelecimento, "pagamento_intent", intent.Id.ToString(), "status", ator, usuarioId,
                new { de = anterior, para = novoStatus }, ct);
        }

        private static void Preencher(PagamentoRegistro pag, MpPagamento mp, string status, decimal estornado)
        {
            pag.Status = status;
            pag.MpOrderId = mp.OrderId ?? pag.MpOrderId;
            pag.MpStatus = mp.Status;
            pag.MpStatusDetail = mp.StatusDetail;
            pag.MpCollectorId = mp.CollectorId;
            pag.PaymentMethodId = mp.PaymentMethodId;
            pag.PaymentTypeId = mp.PaymentTypeId;
            pag.Valor = mp.TransactionAmount;
            pag.ValorEstornado = Math.Min(estornado, mp.TransactionAmount);
            pag.Moeda = string.IsNullOrEmpty(mp.CurrencyId) ? "BRL" : mp.CurrencyId!;
            pag.PixCopiaCola = mp.TransactionData?.QrCode;
            pag.PixQrCodeBase64 = mp.TransactionData?.QrCodeBase64;
            pag.PixTicketUrl = mp.TransactionData?.TicketUrl;
            pag.CartaoBandeira = pag.Metodo == MetodosPagamento.Cartao ? mp.PaymentMethodId : null;
            var final = mp.Card?.LastFourDigits;
            pag.CartaoFinal = final != null && final.Length == 4 && final.All(char.IsDigit) ? final : null;
            pag.Parcelas = pag.Metodo == MetodosPagamento.Cartao ? mp.Installments : null;
            pag.LiveMode = mp.LiveMode;
            pag.ExpiraEm = mp.DateOfExpiration?.UtcDateTime;
            pag.AprovadoEm = mp.DateApproved?.UtcDateTime;
            pag.MpAtualizadoEm = mp.DateLastUpdated?.UtcDateTime;
        }

        /// <summary>Estornos vistos no MP: casa com o pedido nosso (mesmo valor, ainda sem id do MP) ou registra como feito fora da Zippy.</summary>
        private static async Task RegistrarEstornosAsync(NpgsqlConnection cx, NpgsqlTransaction tx, PagamentoRegistro pag, MpPagamento mp, CancellationToken ct)
        {
            foreach (var refund in (mp.Refunds ?? new List<MpRefund>()).Where(r => r.Status is null or "approved"))
            {
                var existe = await cx.ExecuteScalarAsync<Guid?>(new CommandDefinition(
                    "SELECT id FROM pagamento_estorno WHERE id_pagamento = @P AND mp_refund_id = @R", new { P = pag.Id, R = refund.Id }, tx, cancellationToken: ct));
                if (existe.HasValue)
                {
                    await cx.ExecuteAsync(new CommandDefinition(
                        "UPDATE pagamento_estorno SET status = 'concluido', concluido_em = COALESCE(concluido_em, NOW()) WHERE id = @Id AND status <> 'concluido'",
                        new { Id = existe }, tx, cancellationToken: ct));
                    continue;
                }

                var nosso = await cx.ExecuteScalarAsync<Guid?>(new CommandDefinition(@"
SELECT id FROM pagamento_estorno
 WHERE id_pagamento = @P AND mp_refund_id IS NULL AND valor = @V AND status IN ('solicitado', 'processando')
 ORDER BY created_at LIMIT 1 FOR UPDATE", new { P = pag.Id, V = refund.Amount }, tx, cancellationToken: ct));
                if (nosso.HasValue)
                {
                    await cx.ExecuteAsync(new CommandDefinition(
                        "UPDATE pagamento_estorno SET status = 'concluido', mp_refund_id = @R, concluido_em = NOW() WHERE id = @Id",
                        new { Id = nosso, R = refund.Id }, tx, cancellationToken: ct));
                    continue;
                }

                await cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO pagamento_estorno (id, id_pagamento, id_estabelecimento, valor, status, mp_refund_id, origem, idempotency_key, concluido_em)
VALUES (@Id, @P, @Loja, @V, 'concluido', @R, 'mercadopago', @Chave, NOW())
ON CONFLICT (idempotency_key) DO NOTHING", new
                {
                    Id = Guid.NewGuid(), P = pag.Id, Loja = pag.IdEstabelecimento, V = refund.Amount, R = refund.Id, Chave = $"mp-refund:{pag.Ambiente}:{refund.Id}"
                }, tx, cancellationToken: ct));
            }
        }

        private static Task RegistrarDisputaAsync(NpgsqlConnection cx, NpgsqlTransaction tx, PagamentoRegistro pag, CancellationToken ct)
        {
            var (tipo, status) = pag.Status switch
            {
                StatusPagamento.EmMediacao => ("mediacao", "aberta"),
                StatusPagamento.Contestado => ("chargeback", "aberta"),
                StatusPagamento.Aprovado => ("mediacao", "ganha"),
                _ => (null, null)
            };
            if (tipo == null) return Task.CompletedTask;

            // Aprovado so encerra uma mediacao que ja existia (nunca cria disputa).
            var sql = status == "ganha"
                ? "UPDATE pagamento_disputa SET status = 'ganha', updated_at = NOW() WHERE tipo = 'mediacao' AND mp_referencia = @Ref AND status IN ('aberta', 'em_analise')"
                : @"
INSERT INTO pagamento_disputa (id, id_pagamento, id_estabelecimento, tipo, mp_referencia, status, valor)
VALUES (@Id, @P, @Loja, @Tipo, @Ref, 'aberta', @Valor)
ON CONFLICT (tipo, mp_referencia) DO UPDATE SET updated_at = NOW()";
            return cx.ExecuteAsync(new CommandDefinition(sql, new
            {
                Id = Guid.NewGuid(), P = pag.Id, Loja = pag.IdEstabelecimento, Tipo = tipo, Ref = $"pagamento:{pag.Ambiente}:{pag.MpPaymentId}", Valor = pag.Valor
            }, tx, cancellationToken: ct));
        }
    }
}
