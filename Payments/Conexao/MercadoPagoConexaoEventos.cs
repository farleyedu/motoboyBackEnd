using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Payments.MercadoPago;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace APIBack.Payments.Conexao
{
    /// <summary>
    /// Webhook <c>mp-connect</c>: a loja retirou a autorizacao da Zippy no Mercado Pago. Toda conta viva com aquele user_id no ambiente
    /// vira <c>revogada</c> (tokens apagados, linha e pagamentos preservados). Autorizacao nova e tratada pelo callback, nao aqui.
    /// </summary>
    public sealed class MpConnectTopicoHandler : IMercadoPagoTopicoHandler
    {
        private readonly NpgsqlDataSource _db;
        private readonly ILogger<MpConnectTopicoHandler> _logger;

        public MpConnectTopicoHandler(NpgsqlDataSource db, ILogger<MpConnectTopicoHandler> logger)
        {
            _db = db;
            _logger = logger;
        }

        public bool Atende(string topico) => topico is "mp-connect" or "mp_connect";

        public async Task<(string Status, string? Motivo)> ProcessarAsync(MpWebhookEvento evento, CancellationToken ct)
        {
            if (!string.Equals(evento.Acao, "application.deauthorized", StringComparison.OrdinalIgnoreCase)) return ("ignorado", "acao_sem_efeito");
            if (evento.MpUserId is not { } userId) return ("ignorado", "sem_user_id");

            await using var cx = await _db.OpenConnectionAsync(ct);
            await using var tx = await cx.BeginTransactionAsync(ct);
            var revogadas = (await cx.QueryAsync<(Guid Id, Guid Loja)>(new CommandDefinition(@"
UPDATE mp_conta_loja SET estado = 'revogada', access_token_cifrado = NULL, refresh_token_cifrado = NULL, ultimo_erro = 'desautorizada_no_mp',
       desconectado_em = NOW(), updated_at = NOW()
 WHERE ambiente = @Amb AND mp_user_id = @U AND estado IN ('ativa', 'expirada', 'erro')
RETURNING id, id_estabelecimento", new { Amb = evento.Ambiente, U = userId }, tx, cancellationToken: ct))).ToList();
            foreach (var (id, loja) in revogadas)
                await PagamentoAuditoria.RegistrarAsync(cx, tx, loja, "conta_loja", id.ToString(), "conta_revogada", "webhook", null,
                    new { evento = evento.Id, mp = CredencialRecebedora.MascararId(userId) }, ct);
            await tx.CommitAsync(ct);

            _logger.LogInformation("[mp.oauth] ev=desautorizada contas={N} mp={Mp}", revogadas.Count, CredencialRecebedora.MascararId(userId));
            return revogadas.Count == 0 ? ("ignorado", "conta_desconhecida") : ("processado", null);
        }
    }

    /// <summary>
    /// Renova antes do vencimento os tokens das lojas que vendem pouco (quem vende renova no uso, pelo resolvedor). Expira tentativas OAuth
    /// abandonadas. A renovacao trava a linha da conta, entao duas instancias nao giram o refresh token ao mesmo tempo.
    /// </summary>
    public sealed class MercadoPagoContasLojaWorker : BackgroundService
    {
        private static readonly TimeSpan Intervalo = TimeSpan.FromHours(6);
        private readonly IServiceScopeFactory _scopes;
        private readonly NpgsqlDataSource _db;
        private readonly ILogger<MercadoPagoContasLojaWorker> _logger;

        public MercadoPagoContasLojaWorker(IServiceScopeFactory scopes, NpgsqlDataSource db, ILogger<MercadoPagoContasLojaWorker> logger)
        {
            _scopes = scopes;
            _db = db;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); } catch (OperationCanceledException) { return; }
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await RodarAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { _logger.LogWarning("[mp.contas] ev=ciclo_falhou erro={Erro}", ex.GetType().Name); }
                try { await Task.Delay(Intervalo, stoppingToken); } catch (OperationCanceledException) { break; }
            }
        }

        public async Task RodarAsync(CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            await cx.ExecuteAsync(new CommandDefinition(
                "UPDATE mp_oauth_tentativa SET estado = 'expirada', erro_codigo = 'abandonada', code_verifier_cifrado = NULL WHERE estado = 'iniciada' AND expira_em < NOW()",
                cancellationToken: ct));
            var vencendo = (await cx.QueryAsync<Guid>(new CommandDefinition(@"
SELECT id FROM mp_conta_loja WHERE estado = 'ativa' AND token_expira_em < NOW() + INTERVAL '7 days' ORDER BY token_expira_em LIMIT 100",
                cancellationToken: ct))).ToList();
            if (vencendo.Count == 0) return;

            using var scope = _scopes.CreateScope();
            var renovador = scope.ServiceProvider.GetRequiredService<IContaLojaTokenRenovador>();
            foreach (var id in vencendo)
            {
                try
                {
                    var ok = await renovador.RenovarAsync(id, ct) != null;
                    if (!ok) _logger.LogWarning("[mp.contas] ev=renovacao_recusada conta={Conta}", id);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning("[mp.contas] ev=renovacao_falhou conta={Conta} erro={Erro}", id, ex.GetType().Name);
                }
            }
        }
    }
}
