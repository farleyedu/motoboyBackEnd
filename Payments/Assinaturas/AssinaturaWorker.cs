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
    /// (aplicando o downgrade agendado ou encerrando a cancelada), aplica o plano pago no teste quando o ciclo comeca, recalcula a
    /// inadimplencia, manda os avisos, expira compras avulsas e acerta o preapproval no MP. Cada passo e idempotente.
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
        public static int DiasAntecedencia(string periodicidade) => periodicidade == Periodicidades.Anual ? 15 : 7;

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
                    await AssinaturaService.GarantirPrimeiroCicloAsync(cx, loja, ct);

                foreach (var id in await cx.QueryAsync<Guid>(new CommandDefinition(
                    "SELECT id FROM assinatura WHERE status = 'ativa' AND periodo_fim IS NOT NULL ORDER BY periodo_fim", cancellationToken: ct)))
                {
                    await GerarProximoCicloAsync(cx, id, agora, ct);
                    await VirarCicloAsync(cx, id, agora, ct);
                }

                await AplicarCiclosPagosAsync(cx, agora, ct);
                await InadimplenciaEAvisosAsync(cx, politica, agora, ct);
                await ExpirarComprasAsync(cx, agora, ct);
                await SincronizarPreapprovalsAsync(cx, agora, ct);
            }
            finally
            {
                await trava.ExecuteAsync("SELECT pg_advisory_unlock(@L)", new { L = Trava });
            }
        }

        /// <summary>Fatura do ciclo seguinte, alguns dias antes do fim. Cancelada nao renova; downgrade agendado vale no ciclo novo.</summary>
        public static async Task GerarProximoCicloAsync(NpgsqlConnection cx, Guid assinaturaId, DateTimeOffset agora, CancellationToken ct)
        {
            await using var tx = await cx.BeginTransactionAsync(ct);
            var a = await AssinaturaSql.PorIdAsync(cx, tx, assinaturaId, true, ct);
            if (a is not { Status: StatusAssinatura.Ativa, PeriodoFim: { } fimAtual, IdCicloAtual: { } cicloAtualId } || a.CancelamentoSolicitadoEm != null || a.Ancora == null)
                return;
            var periodicidade = a.MudancaPeriodicidade ?? a.Periodicidade;
            if (fimAtual - agora > TimeSpan.FromDays(DiasAntecedencia(a.Periodicidade))) return;
            var atual = await AssinaturaSql.CicloAsync(cx, tx, cicloAtualId, ct);
            if (atual == null) return;
            var numero = atual.Numero + 1;
            var existe = await cx.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS (SELECT 1 FROM assinatura_ciclo WHERE id_assinatura = @A AND numero >= @N)", new { A = a.Id, N = numero }, tx, cancellationToken: ct));
            if (existe) return;

            var plano = a.MudancaPlano ?? a.PlanoChave;
            var preco = a.MudancaPlano != null ? a.MudancaPreco ?? a.Preco : a.Preco;
            (DateOnly Inicio, DateOnly Fim) datas = periodicidade == a.Periodicidade
                ? CicloDatas.Ciclo(a.Ancora.Value, numero - a.AncoraCiclo + 1, periodicidade)
                : CicloDatas.Ciclo(FusoComercial.Data(fimAtual), 1, periodicidade);
            var nome = (await AssinaturaSql.PlanoAsync(cx, tx, plano, ct))?.Nome ?? plano;
            // Cartao recorrente so cobra o mesmo intervalo: trocar mensal/anual volta para pagamento manual.
            var meio = a.MpPreapprovalStatus == "authorized" && a.RenovacaoAutomatica && periodicidade == a.Periodicidade ? "cartao_recorrente" : null;
            await AssinaturaSql.CriarCicloAsync(cx, tx, a, numero, plano, nome, periodicidade, preco, datas.Inicio, datas.Fim, meio, ct);
            await tx.CommitAsync(ct);
        }

        /// <summary>Fim do periodo: encerra a cancelada ou passa para o ciclo seguinte (pago ou nao: a inadimplencia cuida do resto).</summary>
        public async Task VirarCicloAsync(NpgsqlConnection cx, Guid assinaturaId, DateTimeOffset agora, CancellationToken ct)
        {
            await using var tx = await cx.BeginTransactionAsync(ct);
            var a = await AssinaturaSql.PorIdAsync(cx, tx, assinaturaId, true, ct);
            if (a is not { Status: StatusAssinatura.Ativa, PeriodoFim: { } fim, IdCicloAtual: { } cicloAtualId } || fim > agora) return;

            if (a.CancelamentoSolicitadoEm != null)
            {
                await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE assinatura SET status = 'encerrada', encerrada_em = NOW(), encerramento_motivo = 'fim_do_periodo', renovacao_automatica = FALSE,
       versao = versao + 1, updated_at = NOW() WHERE id = @Id", new { a.Id }, tx, cancellationToken: ct));
                await PagamentoAuditoria.RegistrarAsync(cx, tx, a.IdEstabelecimento, "assinatura", a.Id.ToString(), "encerrada", "sistema", null,
                    new { motivo = a.CancelamentoMotivo, fim }, ct);
                await AssinaturaSql.AvisarAsync(cx, tx, a.IdEstabelecimento, null, $"encerrada:{a.Id:N}", "encerrada",
                    "Sua assinatura terminou. O historico continua aqui; contrate um plano para voltar a criar pedidos.", ct);
                await tx.CommitAsync(ct);
                _cache.Remove(SituacaoComercialService.CacheKey(a.IdEstabelecimento));
                return;
            }

            var atual = await AssinaturaSql.CicloAsync(cx, tx, cicloAtualId, ct);
            var proximo = await cx.QueryFirstOrDefaultAsync<CicloRow>(new CommandDefinition(
                $"SELECT {AssinaturaSql.ColunasCiclo} FROM assinatura_ciclo WHERE id_assinatura = @A AND numero > @N AND status <> 'cancelado' ORDER BY numero LIMIT 1 FOR UPDATE",
                new { A = a.Id, N = atual?.Numero ?? 0 }, tx, cancellationToken: ct));
            if (proximo == null) { await tx.CommitAsync(ct); return; } // a geracao roda antes; tenta de novo na proxima volta

            var trocouPeriodicidade = proximo.Periodicidade != a.Periodicidade;
            await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE assinatura SET id_ciclo_atual = @C, periodo_inicio = @Ini, periodo_fim = @Fim, plano_chave = @Plano, periodicidade = @Per, preco = @Preco,
       ancora = CASE WHEN @Troca THEN @Ancora ELSE ancora END, ancora_ciclo = CASE WHEN @Troca THEN @N ELSE ancora_ciclo END,
       -- O cartao recorrente cobra no intervalo antigo: trocar mensal/anual desliga (o sincronizador cancela no MP).
       renovacao_automatica = CASE WHEN @Troca THEN FALSE ELSE renovacao_automatica END,
       mudanca_plano = NULL, mudanca_periodicidade = NULL, mudanca_preco = NULL, mudanca_pedida_em = NULL, mudanca_pedida_por = NULL,
       versao = versao + 1, updated_at = NOW()
 WHERE id = @A", new
            {
                C = proximo.Id, Ini = proximo.Inicio, Fim = proximo.Fim, Plano = proximo.PlanoChave, Per = proximo.Periodicidade, Preco = proximo.Preco,
                Troca = trocouPeriodicidade, Ancora = FusoComercial.Data(proximo.Inicio), N = proximo.Numero, A = a.Id
            }, tx, cancellationToken: ct));
            if (proximo.PlanoChave != a.PlanoChave)
            {
                // Downgrade agendado: modulos do plano novo a partir de agora. Nada e apagado; o que saiu so fica travado.
                await AssinaturaSql.AplicarPlanoAsync(cx, tx, a.IdEstabelecimento, proximo.PlanoChave, "sistema", null, ct);
                await cx.ExecuteAsync(new CommandDefinition("UPDATE assinatura_ciclo SET aplicado_em = NOW() WHERE id = @Id", new { proximo.Id }, tx, cancellationToken: ct));
                await PagamentoAuditoria.RegistrarAsync(cx, tx, a.IdEstabelecimento, "assinatura", a.Id.ToString(), "downgrade_aplicado", "sistema", null,
                    new { de = a.PlanoChave, para = proximo.PlanoChave, proximo.Periodicidade, proximo.Preco }, ct);
            }
            else
            {
                await cx.ExecuteAsync(new CommandDefinition("UPDATE assinatura_ciclo SET aplicado_em = COALESCE(aplicado_em, NOW()) WHERE id = @Id", new { proximo.Id }, tx, cancellationToken: ct));
            }
            await PagamentoAuditoria.RegistrarAsync(cx, tx, a.IdEstabelecimento, "assinatura", a.Id.ToString(), "ciclo_virado", "sistema", null,
                new { ciclo = proximo.Numero, proximo.Inicio, proximo.Fim }, ct);
            await tx.CommitAsync(ct);
            _cache.Remove(SituacaoComercialService.CacheKey(a.IdEstabelecimento));
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
                if (marcou == 1) await AssinaturaSql.AplicarPlanoAsync(cx, tx, p.Loja, p.Plano, "sistema", null, ct);
                await tx.CommitAsync(ct);
                _cache.Remove(SituacaoComercialService.CacheKey(p.Loja));
            }
        }

        private async Task InadimplenciaEAvisosAsync(NpgsqlConnection cx, PoliticaInadimplencia politica, DateTimeOffset agora, CancellationToken ct)
        {
            var assinaturas = (await cx.QueryAsync<(Guid Id, Guid Loja)>(new CommandDefinition(@"
SELECT DISTINCT a.id, a.id_estabelecimento FROM assinatura a
  LEFT JOIN comercial_cobranca c ON c.id_assinatura = a.id AND c.tipo = 'ciclo' AND c.status IN ('aberta', 'processando')
 WHERE a.status IN ('pendente', 'ativa') AND (c.id IS NOT NULL OR a.financeiro <> 'em_dia')", cancellationToken: ct))).ToList();
            foreach (var (id, loja) in assinaturas)
            {
                await using var tx = await cx.BeginTransactionAsync(ct);
                var mudou = await AssinaturaSql.RecalcularFinanceiroAsync(cx, tx, id, politica, agora, ct);
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
            var lista = (await cx.QueryAsync<(Guid Id, string Preapproval, string? Status, decimal Preco, decimal? Valor, bool Cancelada, bool Renova)>(new CommandDefinition(@"
SELECT id, mp_preapproval_id, mp_preapproval_status, preco, preapproval_valor,
       (cancelamento_solicitado_em IS NOT NULL OR status = 'encerrada'), renovacao_automatica
  FROM assinatura WHERE mp_preapproval_id IS NOT NULL AND COALESCE(mp_preapproval_status, '') IN ('authorized', 'pending', 'paused')", cancellationToken: ct))).ToList();
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
                    if (a.Cancelada || !a.Renova)
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
