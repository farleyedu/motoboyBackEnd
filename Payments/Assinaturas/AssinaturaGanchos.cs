using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Payments.MercadoPago;
using APIBack.Service;
using Dapper;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace APIBack.Payments.Assinaturas
{
    /// <summary>
    /// Pix ou cartao avulso de mensalidade/upgrade/compra mudou no nucleo (dentro da transacao do sincronizador). origem_ref_id = comercial_cobranca.id.
    /// Dinheiro entrou: confirma a cobranca (idempotente; pagamento tardio vira excedente). Recusado: so avisa.
    /// </summary>
    public abstract class ComercialPagamentoEfeitoBase : IPagamentoEfeitoHandler
    {
        private readonly IMemoryCache _cache;

        protected ComercialPagamentoEfeitoBase(IMemoryCache cache) => _cache = cache;

        public abstract string Origem { get; }

        public async Task<Guid?> AplicarAsync(NpgsqlConnection cx, NpgsqlTransaction tx, PagamentoIntent intent, string statusAnterior, string ator, CancellationToken ct)
        {
            if (intent.OrigemRefId is not { } cobrancaId) return null;
            if (StatusIntent.FamiliaPaga(intent.Status) && !StatusIntent.FamiliaPaga(statusAnterior))
            {
                var r = await AssinaturaSql.ConfirmarPagamentoAsync(cx, tx, cobrancaId, intent.ValorPago > 0 ? intent.ValorPago : intent.Valor, "mercadopago",
                    intent.Id, null, ator, null, ct);
                if (r.Aplicado) _cache.Remove(SituacaoComercialService.CacheKey(intent.IdEstabelecimento));
            }
            else if (intent.Status == StatusIntent.Recusado)
            {
                await AssinaturaSql.AvisarAsync(cx, tx, intent.IdEstabelecimento, cobrancaId, $"{cobrancaId:N}:recusado:{intent.Versao}", "pagamento_recusado",
                    "O pagamento foi recusado. Tente outro cartao ou pague com Pix.", ct);
            }
            else if (intent.Status is StatusIntent.Estornado or StatusIntent.Contestado)
            {
                // Sem efeito automatico no acesso: a Gestao decide (estorno pedido por ela ou chargeback do cartao).
                await PagamentoAuditoria.RegistrarAsync(cx, tx, intent.IdEstabelecimento, "comercial_cobranca", cobrancaId.ToString(), intent.Status, ator, null,
                    new { intentId = intent.Id, intent.ValorEstornado }, ct);
            }
            return null;
        }
    }

    public sealed class AssinaturaPagamentoEfeito : ComercialPagamentoEfeitoBase
    {
        public AssinaturaPagamentoEfeito(IMemoryCache cache) : base(cache) { }
        public override string Origem => OrigensPagamento.Assinatura;
    }

    public sealed class CompraExtraPagamentoEfeito : ComercialPagamentoEfeitoBase
    {
        public CompraExtraPagamentoEfeito(IMemoryCache cache) : base(cache) { }
        public override string Origem => OrigensPagamento.CompraExtra;
    }

    /// <summary>Referencia propria do preapproval (zpa_{s|p}_{assinatura}): nao comeca com zp_, entao o nucleo ignora os pagamentos dele.</summary>
    public static class PreapprovalReferencia
    {
        public static string Gerar(string ambiente, Guid assinaturaId) => $"zpa_{(ambiente == Ambientes.Producao ? "p" : "s")}_{assinaturaId:N}";

        public static Guid? Assinatura(string? referencia) =>
            referencia != null && referencia.StartsWith("zpa_", StringComparison.Ordinal) && referencia.Length == 38 && Guid.TryParseExact(referencia[6..], "N", out var id)
                ? id : null;
    }

    /// <summary>
    /// Webhooks subscription_preapproval (estado da assinatura no cartao) e subscription_authorized_payment (cada cobranca que o MP fez).
    /// Busca sempre no MP com a conta da Zippy e confere referencia, conta e valor. O MP repete tentativas recusadas sozinho (recycling):
    /// a Zippy so registra, nunca recobra o mesmo ciclo pelo cartao.
    /// </summary>
    public sealed class AssinaturaWebhookHandler : IMercadoPagoTopicoHandler
    {
        private readonly NpgsqlDataSource _db;
        private readonly IMercadoPagoCredencialResolver _credenciais;
        private readonly IMercadoPagoPreapprovalClient _mp;
        private readonly IMemoryCache _cache;
        private readonly ILogger<AssinaturaWebhookHandler> _logger;

        public AssinaturaWebhookHandler(NpgsqlDataSource db, IMercadoPagoCredencialResolver credenciais, IMercadoPagoPreapprovalClient mp, IMemoryCache cache,
            ILogger<AssinaturaWebhookHandler> logger)
        {
            _db = db;
            _credenciais = credenciais;
            _mp = mp;
            _cache = cache;
            _logger = logger;
        }

        public bool Atende(string topico) => topico is "subscription_preapproval" or "subscription_authorized_payment" or "preapproval" or "authorized_payment";

        public async Task<(string Status, string? Motivo)> ProcessarAsync(MpWebhookEvento evento, CancellationToken ct)
        {
            var cred = await _credenciais.RecebedorZippyAsync(evento.Ambiente, ct);
            if (evento.Topico.Contains("authorized_payment", StringComparison.Ordinal))
            {
                if (!long.TryParse(evento.DataId, out var id)) return ("ignorado", "id_invalido");
                var ap = await _mp.ObterCobrancaAsync(cred, id, ct);
                return await AplicarCobrancaRecorrenteAsync(cred, ap, "webhook", ct);
            }
            var pre = await _mp.ObterAsync(cred, evento.DataId, ct);
            return await AplicarPreapprovalAsync(pre, "webhook", ct);
        }

        public async Task<(string Status, string? Motivo)> AplicarPreapprovalAsync(MpPreapproval pre, string ator, CancellationToken ct)
        {
            var assinaturaId = PreapprovalReferencia.Assinatura(pre.ExternalReference);
            if (assinaturaId == null) return ("ignorado", "referencia_desconhecida");
            await using var cx = await _db.OpenConnectionAsync(ct);
            await using var tx = await cx.BeginTransactionAsync(ct);
            var a = await AssinaturaSql.PorIdAsync(cx, tx, assinaturaId.Value, true, ct);
            if (a == null || a.MpPreapprovalId != pre.Id) return ("ignorado", "assinatura_nao_confere");
            if (a.MpPreapprovalStatus == pre.Status) return ("processado", "sem_mudanca");
            await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE assinatura SET mp_preapproval_status = @S,
       renovacao_automatica = CASE WHEN @S IN ('cancelled', 'paused') THEN FALSE ELSE renovacao_automatica END,
       versao = versao + 1, updated_at = NOW() WHERE id = @Id", new { S = pre.Status, a.Id }, tx, cancellationToken: ct));
            if (pre.Status is "cancelled" or "paused")
            {
                // Ciclo esperando o cartao volta a ser pago a mao (Pix ou cartao avulso).
                await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE comercial_cobranca SET status = 'aberta', meio = NULL, versao = versao + 1, updated_at = NOW()
 WHERE id_assinatura = @Id AND status = 'processando' AND meio = 'cartao_recorrente'", new { a.Id }, tx, cancellationToken: ct));
                if (a.CancelamentoSolicitadoEm == null)
                    await AssinaturaSql.AvisarAsync(cx, tx, a.IdEstabelecimento, null, $"preapproval:{pre.Id}:{pre.Status}", "renovacao_interrompida",
                        "A renovacao automatica no cartao foi interrompida. As proximas faturas chegam para pagar com Pix ou cartao.", ct);
            }
            await PagamentoAuditoria.RegistrarAsync(cx, tx, a.IdEstabelecimento, "assinatura", a.Id.ToString(), "preapproval_status", ator, null,
                new { de = a.MpPreapprovalStatus, para = pre.Status, preapproval = pre.Id }, ct);
            await tx.CommitAsync(ct);
            return ("processado", null);
        }

        /// <summary>Uma cobranca do MP no cartao: aprovada confirma o ciclo mais antigo em aberto; recusada/recycling so avisa.</summary>
        public async Task<(string Status, string? Motivo)> AplicarCobrancaRecorrenteAsync(CredencialRecebedora cred, MpAuthorizedPayment ap, string ator, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(ap.PreapprovalId)) return ("ignorado", "sem_preapproval");
            await using var cx = await _db.OpenConnectionAsync(ct);
            await using var tx = await cx.BeginTransactionAsync(ct);
            var a = await cx.QueryFirstOrDefaultAsync<AssinaturaRow>(new CommandDefinition(
                $"SELECT {AssinaturaSql.ColunasAssinatura} FROM assinatura WHERE mp_preapproval_id = @P FOR UPDATE", new { P = ap.PreapprovalId }, tx, cancellationToken: ct));
            if (a == null) return ("ignorado", "preapproval_desconhecido");
            if (!string.IsNullOrEmpty(ap.CurrencyId) && ap.CurrencyId != "BRL") return ("rejeitado", "moeda_divergente");

            var aprovado = ap.Payment?.Status == "approved";
            if (!aprovado)
            {
                var chave = $"recorrente:{ap.Id}:{ap.Status}:{ap.RetryAttempt ?? 0}";
                if (ap.Payment?.Status == "rejected" || ap.Status == "recycling")
                    await AssinaturaSql.AvisarAsync(cx, tx, a.IdEstabelecimento, null, chave, "renovacao_recusada",
                        "O cartao recusou a renovacao. O Mercado Pago vai tentar de novo; se preferir, pague agora com Pix.", ct);
                await tx.CommitAsync(ct);
                return ("processado", "nao_aprovado");
            }

            // Ja registrado (webhook repetido ou conciliacao): nada a fazer.
            var ja = await cx.ExecuteScalarAsync<Guid?>(new CommandDefinition(
                "SELECT id FROM comercial_cobranca WHERE mp_authorized_payment_id = @Ap OR (mp_payment_id = @Mp AND pago_via = 'mp_recorrente') LIMIT 1",
                new { Ap = ap.Id, Mp = ap.Payment!.Id }, tx, cancellationToken: ct));
            if (ja.HasValue) { await tx.CommitAsync(ct); return ("processado", "duplicado"); }

            var alvo = await cx.ExecuteScalarAsync<Guid?>(new CommandDefinition(@"
SELECT id FROM comercial_cobranca WHERE id_assinatura = @A AND tipo = 'ciclo' AND status IN ('aberta', 'processando')
 ORDER BY vencimento LIMIT 1", new { A = a.Id }, tx, cancellationToken: ct));
            EfeitoPagamento r;
            if (alvo.HasValue)
            {
                r = await AssinaturaSql.ConfirmarPagamentoAsync(cx, tx, alvo.Value, ap.TransactionAmount, "mp_recorrente", null, ap.Payment.Id, ator, null, ct, ap.Id);
            }
            else
            {
                // Nada em aberto (o ciclo ja foi pago por Pix): o valor fica como excedente na ultima cobranca de ciclo, para a Gestao devolver.
                var ultima = await cx.ExecuteScalarAsync<Guid?>(new CommandDefinition(
                    "SELECT id FROM comercial_cobranca WHERE id_assinatura = @A AND tipo = 'ciclo' ORDER BY vencimento DESC LIMIT 1", new { A = a.Id }, tx, cancellationToken: ct));
                if (ultima == null) { await tx.CommitAsync(ct); return ("ignorado", "sem_ciclo"); }
                r = await AssinaturaSql.ConfirmarPagamentoAsync(cx, tx, ultima.Value, ap.TransactionAmount, "mp_recorrente", null, ap.Payment.Id, ator, null, ct, ap.Id);
                await cx.ExecuteAsync(new CommandDefinition("UPDATE comercial_cobranca SET mp_authorized_payment_id = COALESCE(mp_authorized_payment_id, @Ap) WHERE id = @Id",
                    new { Ap = ap.Id, Id = ultima }, tx, cancellationToken: ct));
            }
            await tx.CommitAsync(ct);
            if (r.Aplicado) _cache.Remove(SituacaoComercialService.CacheKey(a.IdEstabelecimento));
            _logger.LogInformation("[assinatura] ev=cobranca_recorrente assinatura={A} ap={Ap} aplicado={Ok} excedente={Ex}", a.Id, ap.Id, r.Aplicado, r.Excedente);
            return ("processado", r.Aplicado ? null : "excedente");
        }
    }
}
