using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Payments.MercadoPago;
using APIBack.Service;
using Dapper;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace APIBack.Payments.Assinaturas
{
    /// <summary>
    /// Rotina das assinaturas (uma instancia por vez, advisory lock): gera a fatura do proximo ciclo, vira o ciclo no fim do periodo
    /// (aplicando o downgrade agendado ou encerrando a cancelada), aplica o plano pago no teste quando o ciclo comeca, marca o atraso,
    /// manda os avisos, expira tentativas e compras avulsas e acerta o preapproval no MP. Nunca bloqueia loja: só a Gestão bloqueia.
    /// Cada passo e idempotente.
    /// </summary>
    public sealed class AssinaturaWorker : BackgroundService
    {
        private const long Trava = 75442340;
        private static readonly TimeSpan Intervalo = TimeSpan.FromMinutes(5);
        private readonly IServiceScopeFactory _scopes;
        private readonly NpgsqlDataSource _db;
        private readonly IMemoryCache _cache;
        private readonly ILogger<AssinaturaWorker> _logger;

        public AssinaturaWorker(IServiceScopeFactory scopes, NpgsqlDataSource db, IMemoryCache cache, ILogger<AssinaturaWorker> logger)
        {
            _scopes = scopes;
            _db = db;
            _cache = cache;
            _logger = logger;
        }

        /// <summary>Antecedencia da fatura do proximo ciclo.</summary>
        public static int DiasAntecedencia(string periodicidade) => AssinaturaOperacoes.DiasAntecedencia(periodicidade);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(40), stoppingToken); } catch (OperationCanceledException) { return; }
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await RodarAsync(DateTimeOffset.UtcNow, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                // Migration pendente ou banco fora: tenta na proxima volta.
                catch (Exception ex) { _logger.LogWarning("[assinatura.worker] ev=ciclo_falhou erro={Erro} msg={Msg}", ex.GetType().Name, ex.Message); }
                try { await Task.Delay(Intervalo, stoppingToken); } catch (OperationCanceledException) { break; }
            }
        }

        public async Task RodarAsync(DateTimeOffset agora, CancellationToken ct)
        {
            await using var trava = await _db.OpenConnectionAsync(ct);
            if (!await trava.ExecuteScalarAsync<bool>("SELECT pg_try_advisory_lock(@L)", new { L = Trava })) return;
            try
            {
                await using var cx = await _db.OpenConnectionAsync(ct);
                var politica = await AssinaturaSql.PoliticaAsync(cx, ct);

                foreach (var loja in await cx.QueryAsync<Guid>(new CommandDefinition(@"
SELECT a.id_estabelecimento FROM assinatura a JOIN estabelecimentos e ON e.id = a.id_estabelecimento
 WHERE a.status = 'pendente' AND a.id_ciclo_atual IS NULL AND e.situacao_comercial IN ('teste', 'ativo')", cancellationToken: ct)))
                    await AssinaturaOperacoes.GarantirPrimeiroCicloAsync(cx, loja, ct);

                using (var scope = _scopes.CreateScope())
                    await AssinaturaOperacoes.ExpirarTentativasAsync(_db, scope.ServiceProvider.GetRequiredService<IPixChargeService>(), _logger, null, agora, ct);

                foreach (var id in await cx.QueryAsync<Guid>(new CommandDefinition(
                    "SELECT id FROM assinatura WHERE status = 'ativa' AND periodo_fim IS NOT NULL ORDER BY periodo_fim", cancellationToken: ct)))
                {
                    await GerarProximoCicloAsync(cx, id, agora, ct);
                    await VirarCicloAsync(cx, id, agora, ct);
                }

                await AplicarCiclosPagosAsync(cx, agora, ct);
                await AtrasoEAvisosAsync(cx, politica, agora, ct);
                await ExpirarComprasAsync(cx, agora, ct);
                using (var scope = _scopes.CreateScope())
                    await AssinaturaOperacoes.CancelarPagamentosDeCanceladasAsync(_db, scope.ServiceProvider.GetRequiredService<IPixChargeService>(), _logger, null, ct);
                await SincronizarPreapprovalsAsync(cx, agora, ct);
                await AtualizarSaudeAsync(cx, agora, ct);
            }
            finally
            {
                await trava.ExecuteAsync("SELECT pg_advisory_unlock(@L)", new { L = Trava });
            }
        }

        public static async Task GerarProximoCicloAsync(NpgsqlConnection cx, Guid assinaturaId, DateTimeOffset agora, CancellationToken ct)
        {
            await using var tx = await cx.BeginTransactionAsync(ct);
            await AssinaturaOperacoes.GerarProximoCicloAsync(cx, tx, assinaturaId, agora, ct);
            await tx.CommitAsync(ct);
        }

        public async Task VirarCicloAsync(NpgsqlConnection cx, Guid assinaturaId, DateTimeOffset agora, CancellationToken ct)
        {
            await using var tx = await cx.BeginTransactionAsync(ct);
            var mudou = await AssinaturaOperacoes.VirarCicloAsync(cx, tx, assinaturaId, agora, ct);
            var loja = mudou ? (await AssinaturaSql.PorIdAsync(cx, tx, assinaturaId, false, ct))?.IdEstabelecimento : null;
            await tx.CommitAsync(ct);
            if (loja.HasValue) _cache.Remove(SituacaoComercialService.CacheKey(loja.Value));
        }

        /// <summary>Primeiro ciclo pago durante o teste: o plano entra quando o ciclo comeca (o teste vira ativo).</summary>
        private async Task AplicarCiclosPagosAsync(NpgsqlConnection cx, DateTimeOffset agora, CancellationToken ct)
        {
            var pendentes = (await cx.QueryAsync<(Guid Ciclo, Guid Loja, string Plano)>(new CommandDefinition(@"
SELECT ci.id, ci.id_estabelecimento, ci.plano_chave FROM assinatura_ciclo ci JOIN assinatura a ON a.id = ci.id_assinatura
 WHERE ci.status = 'pago' AND ci.aplicado_em IS NULL AND ci.inicio <= @Agora AND a.status = 'ativa' AND a.id_ciclo_atual = ci.id",
                new { Agora = agora }, cancellationToken: ct))).ToList();
            foreach (var p in pendentes)
            {
                await using var tx = await cx.BeginTransactionAsync(ct);
                var marcou = await cx.ExecuteAsync(new CommandDefinition("UPDATE assinatura_ciclo SET aplicado_em = NOW() WHERE id = @Id AND aplicado_em IS NULL",
                    new { Id = p.Ciclo }, tx, cancellationToken: ct));
                if (marcou == 1) await AssinaturaSql.AplicarPlanoSeMudouAsync(cx, tx, p.Loja, p.Plano, "sistema", null, ct);
                await tx.CommitAsync(ct);
                _cache.Remove(SituacaoComercialService.CacheKey(p.Loja));
            }
        }

        private async Task AtrasoEAvisosAsync(NpgsqlConnection cx, PoliticaInadimplencia politica, DateTimeOffset agora, CancellationToken ct)
        {
            var assinaturas = (await cx.QueryAsync<(Guid Id, Guid Loja)>(new CommandDefinition(@"
SELECT DISTINCT a.id, a.id_estabelecimento FROM assinatura a
  LEFT JOIN comercial_cobranca c ON c.id_assinatura = a.id AND c.tipo = 'ciclo' AND c.status IN ('aberta', 'processando')
 WHERE c.id IS NOT NULL OR a.financeiro <> 'em_dia'", cancellationToken: ct))).ToList();
            foreach (var (id, loja) in assinaturas)
            {
                await using var tx = await cx.BeginTransactionAsync(ct);
                var mudou = await AssinaturaSql.RecalcularFinanceiroAsync(cx, tx, id, agora, ct);
                await tx.CommitAsync(ct);
                if (mudou) _cache.Remove(SituacaoComercialService.CacheKey(loja));
            }

            var hoje = FusoComercial.Data(agora);
            var abertas = await cx.QueryAsync<(Guid Id, Guid Loja, string Descricao, decimal Valor, DateTimeOffset Vencimento, string? Meio)>(new CommandDefinition(@"
SELECT id, id_estabelecimento, descricao, valor, vencimento, meio FROM comercial_cobranca
 WHERE tipo = 'ciclo' AND status IN ('aberta', 'processando') AND vencimento < @Ate", new { Ate = agora.AddDays(10) }, cancellationToken: ct));
            foreach (var c in abertas)
            {
                // Cartao recorrente antes do vencimento: o MP vai cobrar, nao ha o que pedir a loja.
                if (c.Meio == "cartao_recorrente" && hoje < FusoComercial.Data(c.Vencimento)) continue;
                foreach (var aviso in Inadimplencia.AvisosDevidos(c.Id, c.Descricao, c.Valor, FusoComercial.Data(c.Vencimento), hoje, politica))
                    await AssinaturaSql.AvisarAsync(cx, null, c.Loja, c.Id, aviso.Chave, aviso.Tipo, aviso.Mensagem, ct);
            }
        }

        /// <summary>
        /// Saude das lojas, depois de todos os passos de pagamento (so le assinatura e cobranca; nunca altera a verdade do pagamento nem
        /// bloqueia por prazo). Grava a faixa e o historico quando ela muda e registra os marcos do fim do teste sem repetir.
        /// Falha aqui so e registrada: o ciclo das assinaturas ja terminou.
        /// </summary>
        private async Task AtualizarSaudeAsync(NpgsqlConnection cx, DateTimeOffset agora, CancellationToken ct)
        {
            try
            {
                if (!await Acompanhamento.Saude.SaudeSql.ExisteAsync(cx, "acompanhamento_saude_regra", ct)) return;
                var avaliadas = await Acompanhamento.Saude.SaudeService.AvaliarLojasAsync(cx, agora, null, comOportunidades: false, ct);
                foreach (var (entrada, saude) in avaliadas)
                    await Acompanhamento.Saude.SaudeSql.RegistrarAvisosDoTesteAsync(cx, entrada, saude, ct);
                var mudaram = await Acompanhamento.Saude.SaudeSql.PersistirFaixasAsync(cx, avaliadas, ct);
                if (mudaram > 0) _logger.LogInformation("[assinatura.worker] ev=saude_faixas_atualizadas total={Total}", mudaram);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("[assinatura.worker] ev=saude_falhou erro={Erro} msg={Msg}", ex.GetType().Name, ex.Message);
            }
        }

        /// <summary>Compra avulsa vale um mes: vencida deixa de contar (motoboys pela propria vigencia do pacote; modulos saem se o plano nao tem).</summary>
        private async Task ExpirarComprasAsync(NpgsqlConnection cx, DateTimeOffset agora, CancellationToken ct)
        {
            var vencidas = (await cx.QueryAsync<(Guid Id, Guid Loja, string[] Modulos)>(new CommandDefinition(
                "SELECT id, id_estabelecimento, modulos_ativos FROM compra_extra WHERE status = 'ativa' AND vigencia_fim <= @Agora", new { Agora = agora }, cancellationToken: ct))).ToList();
            foreach (var v in vencidas)
            {
                await using var tx = await cx.BeginTransactionAsync(ct);
                await cx.ExecuteAsync(new CommandDefinition("UPDATE compra_extra SET status = 'expirada', versao = versao + 1, updated_at = NOW() WHERE id = @Id AND status = 'ativa'",
                    new { v.Id }, tx, cancellationToken: ct));
                if (v.Modulos.Length > 0)
                    await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE estabelecimentos e SET modulos_ativos = ARRAY(
       SELECT m FROM unnest(e.modulos_ativos) m
        WHERE NOT (m::text = ANY(@Mods))
           OR m = ANY(COALESCE((SELECT p.modulos_ativos FROM pacotes_comerciais p WHERE p.chave = e.plano_comercial), ARRAY[]::modulo_enum[]))
           OR EXISTS (SELECT 1 FROM compra_extra o WHERE o.id_estabelecimento = e.id AND o.status = 'ativa' AND m::text = ANY(o.modulos_ativos)))
 WHERE e.id = @Loja AND e.situacao_comercial <> 'teste'", new { Mods = v.Modulos, Loja = v.Loja }, tx, cancellationToken: ct));
                await PagamentoAuditoria.RegistrarAsync(cx, tx, v.Loja, "compra_extra", v.Id.ToString(), "expirada", "sistema", null, null, ct);
                await tx.CommitAsync(ct);
                _cache.Remove(SituacaoComercialService.CacheKey(v.Loja));
            }
        }

        /// <summary>Preapproval no MP segue a assinatura: cancelado quando ela foi cancelada, valor novo depois de troca de plano, e conciliacao das cobrancas.</summary>
        private async Task SincronizarPreapprovalsAsync(NpgsqlConnection cx, DateTimeOffset agora, CancellationToken ct)
        {
            // O valor a cobrar é o da próxima mensalidade em aberto no cartão; sem ela, o da troca marcada ou o atual.
            var lista = (await cx.QueryAsync<(Guid Id, string Preapproval, string? Status, decimal Preco, decimal? Valor, bool Cancelada, bool Renova)>(new CommandDefinition(@"
SELECT a.id, a.mp_preapproval_id, a.mp_preapproval_status,
       COALESCE((SELECT c.valor FROM comercial_cobranca c WHERE c.id_assinatura = a.id AND c.meio = 'cartao_recorrente' AND c.status = 'processando'
                  ORDER BY c.vencimento LIMIT 1), a.mudanca_preco, a.preco),
       a.preapproval_valor, (a.cancelamento_solicitado_em IS NOT NULL OR a.status = 'encerrada'), a.renovacao_automatica
  FROM assinatura a WHERE a.mp_preapproval_id IS NOT NULL AND COALESCE(a.mp_preapproval_status, '') IN ('authorized', 'pending', 'paused')", cancellationToken: ct))).ToList();
            if (lista.Count == 0) return;
            using var scope = _scopes.CreateScope();
            var mp = scope.ServiceProvider.GetRequiredService<IMercadoPagoPreapprovalClient>();
            var handler = scope.ServiceProvider.GetRequiredService<AssinaturaWebhookHandler>();
            CredencialRecebedora cred;
            try { cred = await scope.ServiceProvider.GetRequiredService<IMercadoPagoCredencialResolver>().RecebedorZippyAsync(null, ct); }
            catch (PagamentoException) { return; }

            foreach (var a in lista)
            {
                try
                {
                    // Cortesia (R$ 0) não tem o que cobrar no cartão.
                    if (a.Cancelada || !a.Renova || a.Preco <= 0)
                    {
                        var pre = await mp.CancelarAsync(cred, a.Preapproval, ct);
                        await cx.ExecuteAsync(new CommandDefinition("UPDATE assinatura SET mp_preapproval_status = @S, updated_at = NOW() WHERE id = @Id",
                            new { S = pre.Status ?? "cancelled", a.Id }, cancellationToken: ct));
                        continue;
                    }
                    if (a.Status == "authorized" && a.Valor != a.Preco)
                    {
                        await mp.AlterarValorAsync(cred, a.Preapproval, a.Preco, ct);
                        await cx.ExecuteAsync(new CommandDefinition("UPDATE assinatura SET preapproval_valor = @V, updated_at = NOW() WHERE id = @Id",
                            new { V = a.Preco, a.Id }, cancellationToken: ct));
                    }
                    // Webhook perdido: confere as cobrancas do MP das faturas que ja deveriam ter sido pagas no cartao.
                    var esperando = await cx.ExecuteScalarAsync<bool>(new CommandDefinition(@"
SELECT EXISTS (SELECT 1 FROM comercial_cobranca WHERE id_assinatura = @Id AND meio = 'cartao_recorrente' AND status = 'processando' AND vencimento < @Limite)",
                        new { a.Id, Limite = agora.AddHours(-2) }, cancellationToken: ct));
                    if (esperando)
                        foreach (var ap in (await mp.ListarCobrancasAsync(cred, a.Preapproval, ct)).Where(x => x.Payment?.Status == "approved"))
                            await handler.AplicarCobrancaRecorrenteAsync(cred, ap, "conciliador", ct);
                    var atual = await mp.ObterAsync(cred, a.Preapproval, ct);
                    if (atual.Status != a.Status) await handler.AplicarPreapprovalAsync(atual, "conciliador", ct);
                }
                catch (MercadoPagoApiException ex)
                {
                    _logger.LogWarning("[assinatura.worker] ev=preapproval_falhou assinatura={A} http={Http}", a.Id, ex.StatusHttp);
                }
            }
        }
    }
}
