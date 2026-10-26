using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Hubs;
using Dapper;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace APIBack.Payments.MercadoPago
{
    /// <summary>
    /// Conciliador periodico (um por vez entre instancias, por advisory lock): reprocessa webhooks que falharam, consulta no MP os
    /// pagamentos abertos sem noticia (webhook perdido) e recupera tentativas que ficaram sem resposta do MP.
    /// </summary>
    public sealed class PagamentoConciliadorWorker : BackgroundService
    {
        private const long TravaConciliador = 75442310;
        private readonly IServiceScopeFactory _scopes;
        private readonly NpgsqlDataSource _db;
        private readonly MercadoPagoOptions _options;
        private readonly ILogger<PagamentoConciliadorWorker> _logger;

        public PagamentoConciliadorWorker(IServiceScopeFactory scopes, NpgsqlDataSource db, IOptions<MercadoPagoOptions> options, ILogger<PagamentoConciliadorWorker> logger)
        {
            _scopes = scopes;
            _db = db;
            _options = options.Value;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var intervalo = TimeSpan.FromSeconds(Math.Max(30, _options.ConciliadorIntervaloSegundos));
            try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); } catch (OperationCanceledException) { return; }
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await RodarAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                // Tabela ainda nao criada (migration pendente) ou banco fora: tenta no proximo ciclo.
                catch (Exception ex) { _logger.LogWarning("[mp.conciliador] ev=ciclo_falhou erro={Erro}", ex.GetType().Name); }
                try { await Task.Delay(intervalo, stoppingToken); } catch (OperationCanceledException) { break; }
            }
        }

        public async Task RodarAsync(CancellationToken ct)
        {
            await using var trava = await _db.OpenConnectionAsync(ct);
            if (!await trava.ExecuteScalarAsync<bool>("SELECT pg_try_advisory_lock(@L)", new { L = TravaConciliador })) return;
            try
            {
                using var scope = _scopes.CreateScope();
                var webhook = scope.ServiceProvider.GetRequiredService<IMercadoPagoWebhookService>();
                var credenciais = scope.ServiceProvider.GetRequiredService<IMercadoPagoCredencialResolver>();
                var sync = scope.ServiceProvider.GetRequiredService<IPagamentoSincronizador>();
                var client = scope.ServiceProvider.GetRequiredService<IMercadoPagoClient>();

                await ReprocessarWebhooksAsync(webhook, ct);
                await ConsultarAbertosAsync(credenciais, sync, ct);
                await RecuperarTentativasAsync(credenciais, sync, client, ct);
            }
            finally
            {
                await trava.ExecuteAsync("SELECT pg_advisory_unlock(@L)", new { L = TravaConciliador });
            }
        }

        private async Task ReprocessarWebhooksAsync(IMercadoPagoWebhookService webhook, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            var ids = await cx.QueryAsync<Guid>(new CommandDefinition(@"
SELECT id FROM mp_webhook_evento
 WHERE ((status IN ('recebido', 'falhou') AND proxima_tentativa_em <= NOW()) OR (status = 'processando' AND proxima_tentativa_em < NOW()))
   AND tentativas < @Max
 ORDER BY recebido_em LIMIT 50", new { Max = _options.WebhookMaxTentativas }, cancellationToken: ct));
            foreach (var id in ids)
            {
                try { await webhook.ProcessarAsync(id, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogDebug("[mp.conciliador] ev=reprocesso_falhou evento={Id}", id); }
            }
        }

        private async Task ConsultarAbertosAsync(IMercadoPagoCredencialResolver credenciais, IPagamentoSincronizador sync, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            var abertos = (await cx.QueryAsync<(Guid IntentId, long MpPaymentId)>(new CommandDefinition(@"
SELECT p.id_intent, p.mp_payment_id FROM pagamento p
 WHERE p.status IN ('pendente', 'em_processo', 'autorizado', 'em_mediacao')
   AND COALESCE(p.conciliado_em, p.created_at) < NOW() - make_interval(secs => @Idade)
 ORDER BY p.conciliado_em NULLS FIRST LIMIT 100", new { Idade = _options.ConciliadorIdadeMinimaSegundos }, cancellationToken: ct))).ToList();
            foreach (var (intentId, mpId) in abertos)
            {
                try
                {
                    var intent = await PagamentoIntentSql.ObterAsync(cx, null, intentId, false, ct);
                    var cred = await credenciais.ParaIntentAsync(intent!, ct);
                    await sync.SincronizarAsync(cred, mpId, "conciliador", ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Conta desconectada ou MP fora: marca como consultado para nao travar a fila; tenta de novo no proximo intervalo.
                    await cx.ExecuteAsync(new CommandDefinition("UPDATE pagamento SET conciliado_em = NOW() WHERE mp_payment_id = @Mp AND id_intent = @I",
                        new { Mp = mpId, I = intentId }, cancellationToken: ct));
                    _logger.LogWarning("[mp.conciliador] ev=consulta_falhou intent={Intent} erro={Erro}", intentId, ex.GetType().Name);
                }
            }
        }

        private async Task RecuperarTentativasAsync(IMercadoPagoCredencialResolver credenciais, IPagamentoSincronizador sync, IMercadoPagoClient client, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            var presas = (await cx.QueryAsync<(Guid Id, Guid IntentId, DateTime CreatedAt)>(new CommandDefinition(@"
SELECT id, id_intent, created_at FROM pagamento_tentativa
 WHERE status = 'criando' AND created_at < NOW() - make_interval(secs => @Idade) ORDER BY created_at LIMIT 50",
                new { Idade = _options.ConciliadorIdadeMinimaSegundos }, cancellationToken: ct))).ToList();
            foreach (var (id, intentId, criada) in presas)
            {
                try
                {
                    var intent = await PagamentoIntentSql.ObterAsync(cx, null, intentId, false, ct);
                    var cred = await credenciais.ParaIntentAsync(intent!, ct);
                    var achados = await client.BuscarPorReferenciaAsync(cred, intent!.ExternalReference, ct);
                    var conhecidos = (await cx.QueryAsync<long>(new CommandDefinition(
                        "SELECT mp_payment_id FROM pagamento WHERE id_intent = @I", new { I = intentId }, cancellationToken: ct))).ToHashSet();
                    var novo = achados.FirstOrDefault(p => !conhecidos.Contains(p.Id));
                    if (novo != null)
                    {
                        await sync.AplicarAsync(cred, novo, id, "conciliador", ct);
                    }
                    else if (criada < DateTime.UtcNow.AddMinutes(-30))
                    {
                        await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE pagamento_tentativa SET status = 'falhou', erro_codigo = 'sem_resposta', concluida_em = NOW() WHERE id = @Id AND status = 'criando'",
                            new { Id = id }, cancellationToken: ct));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning("[mp.conciliador] ev=tentativa_falhou tentativa={Id} erro={Erro}", id, ex.GetType().Name);
                }
            }
        }
    }

    /// <summary>Publica a pagamento_outbox no SignalR (grupo chat:{loja}) depois do commit. Pelo menos uma vez; o front busca o estado autorizado.</summary>
    public sealed class PagamentoOutboxPublisher : BackgroundService
    {
        private readonly NpgsqlDataSource _db;
        private readonly IHubContext<DeliveryHub> _hub;
        private readonly MercadoPagoOptions _options;
        private readonly ILogger<PagamentoOutboxPublisher> _logger;

        public PagamentoOutboxPublisher(NpgsqlDataSource db, IHubContext<DeliveryHub> hub, IOptions<MercadoPagoOptions> options, ILogger<PagamentoOutboxPublisher> logger)
        {
            _db = db;
            _hub = hub;
            _options = options.Value;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var intervalo = TimeSpan.FromMilliseconds(Math.Max(250, _options.OutboxIntervaloMilissegundos));
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await PublicarLoteAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { _logger.LogDebug("[mp.outbox] ev=lote_falhou erro={Erro}", ex.GetType().Name); }
                try { await Task.Delay(intervalo, stoppingToken); } catch (OperationCanceledException) { break; }
            }
        }

        public async Task<int> PublicarLoteAsync(CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            await using var tx = await cx.BeginTransactionAsync(ct);
            var lote = (await cx.QueryAsync<(long Id, string Evento, string Grupo, string Payload)>(new CommandDefinition(@"
SELECT id, evento, grupo, payload::text FROM pagamento_outbox
 WHERE publicado_em IS NULL AND tentativas < 20 ORDER BY id LIMIT 50 FOR UPDATE SKIP LOCKED", transaction: tx, cancellationToken: ct))).ToList();
            var publicados = 0;
            foreach (var (id, evento, grupo, payload) in lote)
            {
                string? erro = null;
                try
                {
                    using var doc = JsonDocument.Parse(payload);
                    using var tempo = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    tempo.CancelAfter(TimeSpan.FromSeconds(5));
                    await _hub.Clients.Group(grupo).SendAsync(evento, doc.RootElement.Clone(), tempo.Token);
                    publicados++;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    erro = ex.GetType().Name;
                }
                await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE pagamento_outbox SET tentativas = tentativas + 1, ultimo_erro = @E, publicado_em = CASE WHEN @E IS NULL THEN NOW() END WHERE id = @Id",
                    new { Id = id, E = erro }, tx, cancellationToken: ct));
            }
            await tx.CommitAsync(ct);
            return publicados;
        }
    }
}
