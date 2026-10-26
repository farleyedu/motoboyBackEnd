using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace APIBack.Payments.MercadoPago
{
    /// <summary>Validacao do header x-signature do MP: HMAC-SHA256 do manifesto "id:{data.id};request-id:{x-request-id};ts:{ts};".</summary>
    public static class MercadoPagoAssinatura
    {
        public sealed record Resultado(bool Valida, string? Motivo, long? Ts);

        public static string Manifesto(string? dataId, string? requestId, string ts)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(dataId))
            {
                // O MP assina o data.id em minusculas quando ele e alfanumerico.
                var id = dataId.All(char.IsLetterOrDigit) ? dataId.ToLowerInvariant() : dataId;
                sb.Append("id:").Append(id).Append(';');
            }
            if (!string.IsNullOrEmpty(requestId)) sb.Append("request-id:").Append(requestId).Append(';');
            sb.Append("ts:").Append(ts).Append(';');
            return sb.ToString();
        }

        public static string Assinar(string segredo, string manifesto) =>
            Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(segredo), Encoding.UTF8.GetBytes(manifesto))).ToLowerInvariant();

        public static Resultado Validar(string? header, string? requestId, string? dataId, string? segredo, DateTimeOffset agora, int toleranciaSegundos)
        {
            if (string.IsNullOrEmpty(segredo)) return new(false, "segredo_ausente", null);
            if (string.IsNullOrWhiteSpace(header)) return new(false, "assinatura_ausente", null);

            string? ts = null;
            var v1 = new List<string>();
            foreach (var parte in header.Split(','))
            {
                var kv = parte.Split('=', 2);
                if (kv.Length != 2) continue;
                var chave = kv[0].Trim();
                if (chave == "ts") ts = kv[1].Trim();
                else if (chave == "v1") v1.Add(kv[1].Trim());
            }
            if (ts == null || v1.Count == 0 || !long.TryParse(ts, NumberStyles.None, CultureInfo.InvariantCulture, out var tsNum))
                return new(false, "assinatura_malformada", null);

            // ts chega em segundos ou milissegundos conforme o produto do MP.
            var quando = tsNum > 100_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(tsNum) : DateTimeOffset.FromUnixTimeSeconds(tsNum);
            if (Math.Abs((agora - quando).TotalSeconds) > toleranciaSegundos) return new(false, "timestamp_fora_da_janela", tsNum);

            var esperado = Encoding.ASCII.GetBytes(Assinar(segredo, Manifesto(dataId, requestId, ts)));
            var ok = v1.Any(a => CryptographicOperations.FixedTimeEquals(esperado, Encoding.ASCII.GetBytes(a.ToLowerInvariant())));
            return new(ok, ok ? null : "assinatura_invalida", tsNum);
        }
    }

    public sealed class MpWebhookEntrada
    {
        public string? Assinatura { get; set; }
        public string? RequestId { get; set; }
        public string? QueryDataId { get; set; }
        public string? QueryTipo { get; set; }
        public string CorpoBruto { get; set; } = string.Empty;
    }

    public enum WebhookDesfecho
    {
        /// <summary>Gravado (e processado ou agendado). Responder 200.</summary>
        Aceito,
        /// <summary>Mesmo aviso de novo: nada a fazer. Responder 200.</summary>
        Duplicado,
        /// <summary>Assinatura/ts invalido. Responder 401 (nada gravado).</summary>
        NaoAutorizado,
        /// <summary>Corpo ilegivel ou sem data.id. Responder 400.</summary>
        Invalido,
        /// <summary>Servidor sem o segredo do webhook. Responder 503 (o MP tenta de novo).</summary>
        NaoConfigurado
    }

    public sealed record WebhookRecebimento(WebhookDesfecho Desfecho, Guid? EventoId, string? Motivo, string? StatusProcessamento);

    /// <summary>Hook para topicos que nao sao de pagamento (assinatura: sessao 4; mp-connect/OAuth: sessao 2).</summary>
    public interface IMercadoPagoTopicoHandler
    {
        bool Atende(string topico);
        /// <summary>Devolve 'processado' ou 'ignorado' (com motivo). Excecao = 'falhou' e reprocessamento.</summary>
        Task<(string Status, string? Motivo)> ProcessarAsync(MpWebhookEvento evento, CancellationToken ct);
    }

    public sealed class MpWebhookEvento
    {
        public Guid Id { get; set; }
        public string Ambiente { get; set; } = Ambientes.Sandbox;
        public string Topico { get; set; } = string.Empty;
        public string? Acao { get; set; }
        public string DataId { get; set; } = string.Empty;
        public long? MpUserId { get; set; }
        public bool? LiveMode { get; set; }
        public int Tentativas { get; set; }
        public string Payload { get; set; } = "{}";
    }

    public interface IMercadoPagoWebhookService
    {
        /// <summary>Valida, grava (antes de qualquer 2xx) e tenta processar na hora.</summary>
        Task<WebhookRecebimento> ReceberAsync(MpWebhookEntrada entrada, CancellationToken ct = default);
        /// <summary>Processa um evento gravado (webhook na hora, ou reprocessamento pelo conciliador).</summary>
        Task<string> ProcessarAsync(Guid eventoId, CancellationToken ct = default);
    }

    public sealed class MercadoPagoWebhookService : IMercadoPagoWebhookService
    {
        public static readonly string[] TopicosPagamento = { "payment", "payments" };
        public static readonly string[] TopicosChargeback = { "chargebacks", "topic_chargebacks_wh" };

        private readonly NpgsqlDataSource _db;
        private readonly IMercadoPagoCredencialResolver _credenciais;
        private readonly IPagamentoSincronizador _sync;
        private readonly IMercadoPagoClient _client;
        private readonly IEnumerable<IMercadoPagoTopicoHandler> _handlers;
        private readonly MercadoPagoOptions _options;
        private readonly ILogger<MercadoPagoWebhookService> _logger;
        private readonly Func<DateTimeOffset> _agora;

        public MercadoPagoWebhookService(NpgsqlDataSource db, IMercadoPagoCredencialResolver credenciais, IPagamentoSincronizador sync, IMercadoPagoClient client,
            IEnumerable<IMercadoPagoTopicoHandler> handlers, IOptions<MercadoPagoOptions> options, ILogger<MercadoPagoWebhookService> logger)
            : this(db, credenciais, sync, client, handlers, options, logger, () => DateTimeOffset.UtcNow)
        {
        }

        internal MercadoPagoWebhookService(NpgsqlDataSource db, IMercadoPagoCredencialResolver credenciais, IPagamentoSincronizador sync, IMercadoPagoClient client,
            IEnumerable<IMercadoPagoTopicoHandler> handlers, IOptions<MercadoPagoOptions> options, ILogger<MercadoPagoWebhookService> logger, Func<DateTimeOffset> agora)
        {
            _db = db;
            _credenciais = credenciais;
            _sync = sync;
            _client = client;
            _handlers = handlers;
            _options = options.Value;
            _logger = logger;
            _agora = agora;
        }

        public async Task<WebhookRecebimento> ReceberAsync(MpWebhookEntrada e, CancellationToken ct = default)
        {
            MpWebhookNotificacao? corpo = null;
            if (!string.IsNullOrWhiteSpace(e.CorpoBruto))
            {
                try { corpo = JsonSerializer.Deserialize<MpWebhookNotificacao>(e.CorpoBruto); }
                catch (JsonException) { return new(WebhookDesfecho.Invalido, null, "json_invalido", null); }
            }

            var dataId = Texto(e.QueryDataId) ?? Texto(corpo?.Data?.Id?.ToString());
            var topico = (Texto(corpo?.Type) ?? Texto(e.QueryTipo) ?? Texto(corpo?.Topic))?.ToLowerInvariant();
            if (dataId == null || topico == null || dataId.Length > 64 || topico.Length > 64) return new(WebhookDesfecho.Invalido, null, "sem_data_id_ou_topico", null);

            var ambiente = _credenciais.AmbienteAtivo;
            CredencialAplicacao app;
            try { app = await _credenciais.AplicacaoAsync(ambiente, ct); }
            catch (PagamentoException) { return new(WebhookDesfecho.NaoConfigurado, null, "aplicacao_nao_configurada", null); }
            if (!app.TemWebhookSecret) return new(WebhookDesfecho.NaoConfigurado, null, "segredo_webhook_ausente", null);

            // O manifesto usa o data.id da URL (query); sem ele, o do corpo.
            var assinatura = MercadoPagoAssinatura.Validar(e.Assinatura, e.RequestId, Texto(e.QueryDataId) ?? dataId, app.WebhookSecret, _agora(), _options.WebhookToleranciaSegundos);
            if (!assinatura.Valida)
            {
                _logger.LogWarning("[mp.webhook] ev=nao_autorizado motivo={Motivo} topico={Topico}", assinatura.Motivo, topico);
                return new(WebhookDesfecho.NaoAutorizado, null, assinatura.Motivo, null);
            }

            var notificacaoId = Texto(corpo?.Id?.ToString());
            var acao = Texto(corpo?.Action);
            long? userId = long.TryParse(corpo?.UserId?.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var u) ? u : null;
            var chave = $"{topico}|{dataId}|{acao}|{notificacaoId ?? e.RequestId}";
            var id = Guid.NewGuid();

            await using var cx = await _db.OpenConnectionAsync(ct);
            var gravado = await cx.ExecuteScalarAsync<Guid?>(new CommandDefinition(@"
INSERT INTO mp_webhook_evento (id, ambiente, topico, acao, data_id, mp_notification_id, mp_request_id, mp_user_id, live_mode, assinatura_ts, chave_dedup,
                               payload, proxima_tentativa_em)
VALUES (@Id, @Amb, @Topico, @Acao, @DataId, @Notif, @Req, @User, @Live, @Ts, @Chave, CAST(@Payload AS jsonb), NOW())
ON CONFLICT (ambiente, chave_dedup) DO NOTHING
RETURNING id", new
            {
                Id = id, Amb = ambiente, Topico = topico, Acao = acao, DataId = dataId, Notif = notificacaoId, Req = Cortar(e.RequestId, 120), User = userId,
                Live = corpo?.LiveMode, Ts = assinatura.Ts, Chave = chave, Payload = string.IsNullOrWhiteSpace(e.CorpoBruto) ? "{}" : e.CorpoBruto
            }, cancellationToken: ct));
            if (gravado == null) return new(WebhookDesfecho.Duplicado, null, "duplicado", null);

            // Gravado: daqui em diante sempre 200. Falha no processamento fica para o reprocessamento.
            string status;
            try
            {
                status = await ProcessarAsync(id, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "[mp.webhook] ev=processamento_adiado evento={Evento}", id);
                status = "falhou";
            }
            return new(WebhookDesfecho.Aceito, id, null, status);
        }

        public async Task<string> ProcessarAsync(Guid eventoId, CancellationToken ct = default)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            // Reserva o evento (5 min); um 'processando' abandonado volta a ser elegivel depois disso.
            var evento = await cx.QueryFirstOrDefaultAsync<MpWebhookEvento>(new CommandDefinition(@"
UPDATE mp_webhook_evento SET status = 'processando', tentativas = tentativas + 1, proxima_tentativa_em = NOW() + INTERVAL '5 minutes'
 WHERE id = @Id AND (status IN ('recebido', 'falhou') OR (status = 'processando' AND proxima_tentativa_em < NOW()))
RETURNING id, ambiente, topico, acao, data_id, mp_user_id, live_mode, tentativas, payload::text AS payload", new { Id = eventoId }, cancellationToken: ct));
            if (evento == null) return "em_uso";

            string status;
            string? motivo;
            try
            {
                (status, motivo) = await DespacharAsync(evento, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var atraso = TimeSpan.FromSeconds(Math.Min(3600, 30 * Math.Pow(2, Math.Max(0, evento.Tentativas - 1))));
                var desistir = evento.Tentativas >= _options.WebhookMaxTentativas;
                await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE mp_webhook_evento SET status = 'falhou', ultimo_erro = @Erro, proxima_tentativa_em = @Prox WHERE id = @Id", new
                {
                    Id = eventoId, Erro = Cortar($"{ex.GetType().Name}: {ex.Message}", 500), Prox = desistir ? (DateTime?)null : DateTime.UtcNow.Add(atraso)
                }, cancellationToken: ct));
                _logger.LogWarning("[mp.webhook] ev=falhou evento={Evento} tentativa={N} desistiu={Desistiu} erro={Erro}", eventoId, evento.Tentativas, desistir, ex.GetType().Name);
                throw;
            }

            await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE mp_webhook_evento SET status = @S, ultimo_erro = @M, processado_em = NOW(), proxima_tentativa_em = NULL WHERE id = @Id",
                new { Id = eventoId, S = status, M = motivo }, cancellationToken: ct));
            _logger.LogInformation("[mp.webhook] ev={Status} evento={Evento} topico={Topico} motivo={Motivo}", status, eventoId, evento.Topico, motivo);
            return status;
        }

        private async Task<(string, string?)> DespacharAsync(MpWebhookEvento evento, CancellationToken ct)
        {
            if (_options.ExigirLiveModeDoAmbiente && evento.LiveMode.HasValue && evento.LiveMode.Value != Ambientes.LiveMode(evento.Ambiente))
                return ("rejeitado", "live_mode_divergente");

            if (TopicosPagamento.Contains(evento.Topico))
            {
                if (!long.TryParse(evento.DataId, NumberStyles.None, CultureInfo.InvariantCulture, out var mpId)) return ("ignorado", "data_id_invalido");
                var cred = await CredencialDoPagamentoAsync(evento, mpId, ct);
                if (cred == null) return ("ignorado", "conta_desconhecida");
                var r = await _sync.SincronizarAsync(cred, mpId, "webhook", ct);
                return r.Resultado switch
                {
                    ResultadoSincronizacao.Aplicado => ("processado", null),
                    ResultadoSincronizacao.SemMudanca => ("processado", "sem_mudanca"),
                    ResultadoSincronizacao.Rejeitado => ("rejeitado", r.Motivo),
                    _ => ("ignorado", r.Motivo)
                };
            }

            if (TopicosChargeback.Contains(evento.Topico))
                return await ChargebackAsync(evento, ct);

            var handler = _handlers.FirstOrDefault(h => h.Atende(evento.Topico));
            return handler == null ? ("ignorado", "sem_handler") : await handler.ProcessarAsync(evento, ct);
        }

        /// <summary>Pagamento conhecido: a conta gravada na intencao. Desconhecido: a conta dona do user_id do aviso.</summary>
        private async Task<CredencialRecebedora?> CredencialDoPagamentoAsync(MpWebhookEvento evento, long mpId, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            var intentId = await cx.ExecuteScalarAsync<Guid?>(new CommandDefinition(
                "SELECT id_intent FROM pagamento WHERE ambiente = @Amb AND mp_payment_id = @Mp", new { Amb = evento.Ambiente, Mp = mpId }, cancellationToken: ct));
            if (intentId.HasValue)
            {
                var intent = await PagamentoIntentSql.ObterAsync(cx, null, intentId.Value, false, ct);
                return await _credenciais.ParaIntentAsync(intent!, ct);
            }
            return evento.MpUserId.HasValue ? await _credenciais.PorMpUserIdAsync(evento.Ambiente, evento.MpUserId.Value, ct) : null;
        }

        private async Task<(string, string?)> ChargebackAsync(MpWebhookEvento evento, CancellationToken ct)
        {
            if (!evento.MpUserId.HasValue) return ("ignorado", "sem_user_id");
            var cred = await _credenciais.PorMpUserIdAsync(evento.Ambiente, evento.MpUserId.Value, ct);
            if (cred == null) return ("ignorado", "conta_desconhecida");
            var cb = await _client.ObterChargebackAsync(cred, evento.DataId, ct);
            foreach (var mpId in cb.Payments ?? new List<long>())
            {
                await _sync.SincronizarAsync(cred, mpId, "webhook", ct);
                await using var cx = await _db.OpenConnectionAsync(ct);
                await cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO pagamento_disputa (id, id_pagamento, id_estabelecimento, tipo, mp_referencia, status, valor, prazo_resposta_em, detalhe)
SELECT @Id, p.id, p.id_estabelecimento, 'chargeback', @Ref, 'aberta', @Valor, @Prazo, CAST(@Detalhe AS jsonb)
  FROM pagamento p WHERE p.ambiente = @Amb AND p.mp_payment_id = @Mp
ON CONFLICT (tipo, mp_referencia) DO UPDATE SET prazo_resposta_em = EXCLUDED.prazo_resposta_em, detalhe = EXCLUDED.detalhe, updated_at = NOW()", new
                {
                    Id = Guid.NewGuid(), Ref = $"chargeback:{evento.Ambiente}:{cb.Id}", Valor = cb.Amount, Prazo = cb.DateDocumentationDeadline?.UtcDateTime,
                    Detalhe = JsonSerializer.Serialize(new { cobertura = cb.CoverageApplied, documentacao = cb.DocumentationStatus }), Amb = evento.Ambiente, Mp = mpId
                }, cancellationToken: ct));
            }
            return ("processado", null);
        }

        private static string? Texto(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        private static string? Cortar(string? s, int max) => s == null ? null : s.Length <= max ? s : s[..max];
    }
}
