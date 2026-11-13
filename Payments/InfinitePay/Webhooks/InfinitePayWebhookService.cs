using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;

namespace APIBack.Payments.InfinitePay.Webhooks
{
    public enum DesfechoRecebimento
    {
        /// <summary>Gravado em definitivo (ou ja estava): responder 2xx.</summary>
        Aceito,
        Duplicado,
        Invalido,
        /// <summary>order_nsu que nao e de nenhuma cobranca do Zippy: nada e gravado.</summary>
        SemCorrelacao,
        LimiteExcedido
    }

    public sealed record ResultadoRecebimento(DesfechoRecebimento Desfecho, Guid? EventoId, string? Motivo);

    public interface IInfinitePayWebhookService
    {
        Task<ResultadoRecebimento> ReceberAsync(string corpoBruto, CancellationToken ct = default);
    }

    /// <summary>
    /// Entrada do webhook (sem assinatura: o corpo e dado nao confiavel). So valida formato, correlaciona com uma cobranca NOSSA pelo
    /// order_nsu e grava de forma duravel e deduplicada. Nenhum efeito financeiro acontece aqui.
    /// </summary>
    public sealed class InfinitePayWebhookService : IInfinitePayWebhookService
    {
        private readonly NpgsqlDataSource _db;

        private sealed class Correlacao
        {
            public Guid IdIntent { get; set; }
            public Guid IdEstabelecimento { get; set; }
        }

        public InfinitePayWebhookService(NpgsqlDataSource db) => _db = db;

        public async Task<ResultadoRecebimento> ReceberAsync(string corpoBruto, CancellationToken ct = default)
        {
            var p = InfinitePayWebhookRegras.Validar(corpoBruto, out var motivo);
            if (p == null) return new ResultadoRecebimento(DesfechoRecebimento.Invalido, null, motivo);

            await using var cx = await _db.OpenConnectionAsync(ct);
            var checkout = await cx.QueryFirstOrDefaultAsync<Correlacao>(new CommandDefinition(
                "SELECT id_intent, id_estabelecimento FROM infinitepay_checkout WHERE order_nsu = @N", new { N = p.OrderNsu }, cancellationToken: ct));
            if (checkout == null) return new ResultadoRecebimento(DesfechoRecebimento.SemCorrelacao, null, "order_nsu_desconhecido");

            var total = await cx.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM infinitepay_webhook_evento WHERE id_intent = @I", new { I = checkout.IdIntent }, cancellationToken: ct));
            var hash = p.HashDeduplicacao();
            if (total >= InfinitePayWebhookRegras.MaxEventosPorIntencao)
            {
                var jaExiste = await cx.ExecuteScalarAsync<bool>(new CommandDefinition(
                    "SELECT EXISTS (SELECT 1 FROM infinitepay_webhook_evento WHERE dedupe_hash = @H)", new { H = hash }, cancellationToken: ct));
                return jaExiste
                    ? new ResultadoRecebimento(DesfechoRecebimento.Duplicado, null, null)
                    : new ResultadoRecebimento(DesfechoRecebimento.LimiteExcedido, null, "limite_por_cobranca");
            }

            var id = Guid.NewGuid();
            var gravado = await cx.ExecuteScalarAsync<Guid?>(new CommandDefinition(@"
INSERT INTO infinitepay_webhook_evento (id, id_estabelecimento, id_intent, order_nsu, tipo_evento, transaction_nsu, invoice_slug, amount_informado,
                                        paid_amount_informado, capture_method, dedupe_hash, payload_json)
VALUES (@Id, @Loja, @Intent, @Nsu, @Tipo, @Tx, @Slug, @Amount, @Paid, @Capture, @Hash, @Json)
ON CONFLICT (dedupe_hash) DO NOTHING
RETURNING id", new
            {
                Id = id, Loja = checkout.IdEstabelecimento, Intent = checkout.IdIntent, Nsu = p.OrderNsu, Tipo = p.TipoEvento, Tx = p.TransactionNsu,
                Slug = p.InvoiceSlug, p.Amount, Paid = p.PaidAmount, Capture = p.CaptureMethod, Hash = hash, Json = corpoBruto
            }, cancellationToken: ct));

            return gravado.HasValue
                ? new ResultadoRecebimento(DesfechoRecebimento.Aceito, gravado, null)
                : new ResultadoRecebimento(DesfechoRecebimento.Duplicado, null, null);
        }
    }
}
