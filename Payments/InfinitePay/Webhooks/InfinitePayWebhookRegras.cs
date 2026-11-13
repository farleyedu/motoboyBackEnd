using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using APIBack.Payments.InfinitePay.Cliente;

namespace APIBack.Payments.InfinitePay.Webhooks
{
    public static class InfinitePayEventos
    {
        public const string Created = "created";
        public const string Paid = "paid";
        public const string Settled = "settled";
        public const string Canceled = "canceled";
        public const string Refunded = "refunded";
        public const string PaymentFailed = "payment_failed";
    }

    public static class StatusWebhookEvento
    {
        public const string Pendente = "pendente";
        public const string Processando = "processando";
        public const string Processado = "processado";
        public const string Descartado = "descartado";
        public const string Pendencia = "pendencia";
    }

    public static class TiposPendenciaInfinitePay
    {
        public const string CancelamentoNaoVerificavel = "cancelamento_nao_verificavel";
        public const string EstornoNaoVerificavel = "estorno_nao_verificavel";
        public const string PagoSemIdentificadores = "pago_sem_identificadores";
        public const string ValorDivergente = "valor_divergente";
        public const string PagamentoDuplicado = "pagamento_duplicado";
        public const string PagoEmEstadoFinal = "pago_em_estado_final";
        public const string VerificacaoFalhou = "verificacao_falhou";
        public const string NaoConfirmado = "nao_confirmado";
    }

    /// <summary>Aviso ja validado em formato. NADA aqui e confiavel: so serve para escolher o que verificar em /payment_check.</summary>
    public sealed class InfinitePayWebhookPayload
    {
        public string OrderNsu { get; init; } = string.Empty;
        public string? TipoEvento { get; init; }
        public string? TransactionNsu { get; init; }
        public string? InvoiceSlug { get; init; }
        public long? Amount { get; init; }
        public long? PaidAmount { get; init; }
        public string? CaptureMethod { get; init; }

        /// <summary>Mesmo conteudo relevante = mesmo hash: reenvio nao cria segundo evento.</summary>
        public string HashDeduplicacao()
        {
            var texto = string.Join('|', OrderNsu, TipoEvento ?? "", TransactionNsu ?? "", InvoiceSlug ?? "", Amount?.ToString() ?? "", PaidAmount?.ToString() ?? "", CaptureMethod ?? "");
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(texto))).ToLowerInvariant();
        }
    }

    public enum AcaoEvento
    {
        /// <summary>created / payment_failed / desconhecido sem identificadores: nenhum efeito (pagamento recusado pode ser tentado de novo).</summary>
        Ignorar,
        /// <summary>paid / settled / sem tipo com identificadores: confirmar em /payment_check.</summary>
        VerificarPagamento,
        PendenciaCancelamento,
        PendenciaEstorno,
        PendenciaSemIdentificadores
    }

    public enum AvaliacaoPagamento { Confirmado, NaoConfirmado, ValorDivergente }

    public static class InfinitePayWebhookRegras
    {
        public const int CorpoMaximoBytes = 16 * 1024;
        public const int MaxTentativas = 8;
        public const int MaxTentativasNaoConfirmado = 5;
        public const int MaxEventosPorIntencao = 200;

        private static readonly Regex OrderNsuNosso = new("^zp_p_(cardapio|chat)_[0-9a-f]{32}$", RegexOptions.Compiled);
        private static readonly Regex Identificador = new("^[A-Za-z0-9_-]{1,100}$", RegexOptions.Compiled);

        /// <summary>Valida tamanho de campos e formatos. Devolve null e o motivo (sem ecoar conteudo) quando invalido.</summary>
        public static InfinitePayWebhookPayload? Validar(string corpo, out string? motivo)
        {
            motivo = null;
            if (string.IsNullOrWhiteSpace(corpo) || Encoding.UTF8.GetByteCount(corpo) > CorpoMaximoBytes) { motivo = "corpo_invalido"; return null; }
            try
            {
                using var doc = JsonDocument.Parse(corpo);
                var r = doc.RootElement;
                if (r.ValueKind != JsonValueKind.Object) { motivo = "corpo_invalido"; return null; }

                var order = Texto(r, "order_nsu");
                if (order == null || !OrderNsuNosso.IsMatch(order)) { motivo = "order_nsu_invalido"; return null; }

                var tx = Texto(r, "transaction_nsu");
                var slug = Texto(r, "invoice_slug") ?? Texto(r, "slug");
                if ((tx != null && !Identificador.IsMatch(tx)) || (slug != null && !Identificador.IsMatch(slug))) { motivo = "identificador_invalido"; return null; }

                var amount = Inteiro(r, "amount", out var okA);
                var paidAmount = Inteiro(r, "paid_amount", out var okP);
                if (!okA || !okP) { motivo = "valor_invalido"; return null; }

                var capture = Texto(r, "capture_method");
                if (capture != null && capture is not (InfinitePayRegras.MetodoPix or InfinitePayRegras.MetodoCartao)) { motivo = "metodo_invalido"; return null; }

                var tipo = Texto(r, "event") ?? Texto(r, "type") ?? Texto(r, "event_type");
                tipo = tipo is InfinitePayEventos.Created or InfinitePayEventos.Paid or InfinitePayEventos.Settled or InfinitePayEventos.Canceled
                    or InfinitePayEventos.Refunded or InfinitePayEventos.PaymentFailed ? tipo : null;

                return new InfinitePayWebhookPayload
                {
                    OrderNsu = order, TipoEvento = tipo, TransactionNsu = tx, InvoiceSlug = slug, Amount = amount, PaidAmount = paidAmount, CaptureMethod = capture
                };
            }
            catch (JsonException)
            {
                motivo = "corpo_invalido";
                return null;
            }
        }

        public static AcaoEvento Classificar(string? tipo, string? transactionNsu, string? slug)
        {
            var temIds = !string.IsNullOrEmpty(transactionNsu) && !string.IsNullOrEmpty(slug);
            return tipo switch
            {
                InfinitePayEventos.Created or InfinitePayEventos.PaymentFailed => AcaoEvento.Ignorar,
                InfinitePayEventos.Canceled => AcaoEvento.PendenciaCancelamento,
                InfinitePayEventos.Refunded => AcaoEvento.PendenciaEstorno,
                InfinitePayEventos.Paid or InfinitePayEventos.Settled => temIds ? AcaoEvento.VerificarPagamento : AcaoEvento.PendenciaSemIdentificadores,
                // O exemplo oficial de "pago" nao traz campo de tipo: sem tipo, so a presenca dos identificadores pede verificacao.
                _ => temIds ? AcaoEvento.VerificarPagamento : AcaoEvento.Ignorar
            };
        }

        /// <summary>Confere a resposta de /payment_check contra o que o Zippy cobrou. paid_amount NAO entra: pode incluir taxa do comprador.</summary>
        public static AvaliacaoPagamento Avaliar(InfinitePayPaymentCheck check, long valorCobradoCentavos)
        {
            if (!check.Success || !check.Paid) return AvaliacaoPagamento.NaoConfirmado;
            if (check.Amount == null || check.Amount.Value != valorCobradoCentavos) return AvaliacaoPagamento.ValorDivergente;
            return AvaliacaoPagamento.Confirmado;
        }

        /// <summary>30 s, 60 s, 2 min... ate 1 h.</summary>
        public static TimeSpan Backoff(int tentativas)
        {
            var n = Math.Clamp(tentativas, 1, 12);
            return TimeSpan.FromSeconds(Math.Min(3600, 30 * Math.Pow(2, n - 1)));
        }

        private static string? Texto(JsonElement r, string nome) =>
            r.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString() : null;

        private static long? Inteiro(JsonElement r, string nome, out bool ok)
        {
            ok = true;
            if (!r.TryGetProperty(nome, out var v) || v.ValueKind == JsonValueKind.Null) return null;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) && n is >= 0 and <= 1_000_000_000) return n;
            ok = false;
            return null;
        }
    }
}
