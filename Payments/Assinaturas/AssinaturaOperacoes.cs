using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Payments.MercadoPago;
using APIBack.Service;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace APIBack.Payments.Assinaturas
{
    /// <summary>
    /// As mudanças de uma assinatura, usadas pela loja, pela Gestão, pelo pagamento e pela rotina. Quem chama abre a transação e trava a
    /// assinatura; nada aqui fala com o Mercado Pago, exceto a limpeza de Pix (fora de transação).
    /// </summary>
    public static class AssinaturaOperacoes
    {
        /// <summary>Antecedencia da fatura do proximo ciclo.</summary>
        public static int DiasAntecedencia(string periodicidade) => periodicidade == Periodicidades.Anual ? 15 : 7;

        // ---------- leitura ----------

        public static async Task<CicloRow?> CicloAtualAsync(NpgsqlConnection cx, NpgsqlTransaction? tx, AssinaturaRow a, CancellationToken ct) =>
            a.IdCicloAtual.HasValue ? await AssinaturaSql.CicloAsync(cx, tx, a.IdCicloAtual.Value, ct) : null;

        /// <summary>Ciclos já gerados depois do atual (a próxima fatura), sem os cancelados.</summary>
        public static async Task<List<CicloRow>> CiclosSeguintesAsync(NpgsqlConnection cx, NpgsqlTransaction? tx, AssinaturaRow a, CancellationToken ct) =>
            (await cx.QueryAsync<CicloRow>(new CommandDefinition($@"
SELECT {AssinaturaSql.ColunasCiclo} FROM assinatura_ciclo
 WHERE id_assinatura = @A AND status <> 'cancelado'
   AND numero > COALESCE((SELECT numero FROM assinatura_ciclo WHERE id = @C), 0)
 ORDER BY numero FOR UPDATE", new { A = a.Id, C = a.IdCicloAtual }, tx, cancellationToken: ct))).ToList();

        public static Task<CobrancaRow?> CobrancaDoCicloAsync(NpgsqlConnection cx, NpgsqlTransaction? tx, Guid ciclo, CancellationToken ct) =>
            cx.QueryFirstOrDefaultAsync<CobrancaRow>(new CommandDefinition($@"
SELECT {AssinaturaSql.ColunasCobranca} FROM comercial_cobranca WHERE id_ciclo = @C AND tipo = 'ciclo' AND status <> 'cancelada' FOR UPDATE",
                new { C = ciclo }, tx, cancellationToken: ct));

        private static async Task<string> NomeDoPlanoAsync(NpgsqlConnection cx, NpgsqlTransaction tx, string chave, CancellationToken ct) =>
            (await AssinaturaSql.PlanoAsync(cx, tx, chave, ct))?.Nome ?? chave;

        // ---------- cobranças ----------

        /// <summary>
        /// Cancela uma cobrança em aberto (nada é apagado). A de quem voltou a assinar devolve as dívidas antigas que ela somava,
        /// a não ser que outra vá ocupar o lugar dela. A de extra cancela a compra.
        /// </summary>
        public static async Task CancelarCobrancaAsync(NpgsqlConnection cx, NpgsqlTransaction tx, CobrancaRow c, string motivo, int? usuarioId, CancellationToken ct,
            bool devolverAgrupadas = true)
        {
            await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE comercial_cobranca SET status = 'cancelada', cancelada_em = NOW(), cancelada_por = @U, cancelamento_motivo = @M, versao = versao + 1, updated_at = NOW()
 WHERE id = @Id AND status IN ('aberta', 'processando')", new { c.Id, U = usuarioId, M = motivo }, tx, cancellationToken: ct));
            if (devolverAgrupadas && c.DividaValor > 0)
                await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE comercial_cobranca SET status = 'aberta', meio = NULL, cancelada_em = NULL, cancelada_por = NULL, cancelamento_motivo = NULL, agrupada_em = NULL,
       versao = versao + 1, updated_at = NOW()
 WHERE agrupada_em = @Id AND status = 'cancelada'", new { c.Id }, tx, cancellationToken: ct));
            if (c.IdCompra.HasValue)
                await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE compra_extra SET status = 'cancelada', cancelada_em = NOW(), versao = versao + 1, updated_at = NOW() WHERE id = @C AND status = 'aguardando_pagamento'",
                    new { C = c.IdCompra }, tx, cancellationToken: ct));
        }

        public static Task CancelarTentativasDeUpgradeAsync(NpgsqlConnection cx, NpgsqlTransaction tx, Guid assinaturaId, string motivo, int? usuarioId,
            CancellationToken ct) =>
            cx.ExecuteAsync(new CommandDefinition(@"
UPDATE comercial_cobranca SET status = 'cancelada', cancelada_em = NOW(), cancelada_por = @U, cancelamento_motivo = @M, versao = versao + 1, updated_at = NOW()
 WHERE id_assinatura = @A AND tipo = 'upgrade' AND status IN ('aberta', 'processando')", new { A = assinaturaId, U = usuarioId, M = motivo }, tx, cancellationToken: ct));

        /// <summary>
        /// Refaz um ciclo ainda não pago com outro plano ou preço: a cobrança antiga é cancelada (um pagamento atrasado dela vira
        /// excedente) e nasce outra com o mesmo vencimento. Devolve false quando o ciclo já foi pago.
        /// </summary>
        public static async Task<bool> RefazerCicloAsync(NpgsqlConnection cx, NpgsqlTransaction tx, AssinaturaRow a, CicloRow ciclo, string plano,
            string periodicidade, decimal preco, string motivo, int? usuarioId, CancellationToken ct)
        {
            if (ciclo.Quitado) return false;
            var antiga = await CobrancaDoCicloAsync(cx, tx, ciclo.Id, ct);
            var divida = antiga?.DividaValor ?? 0;
            var hoje = FusoComercial.InicioDeHoje(DateTimeOffset.UtcNow);
            var vencimento = antiga?.Vencimento ?? (ciclo.Inicio > hoje ? ciclo.Inicio : hoje);
            if (antiga != null) await CancelarCobrancaAsync(cx, tx, antiga, motivo, usuarioId, ct, devolverAgrupadas: false);

            var fim = periodicidade == ciclo.Periodicidade ? ciclo.Fim
                : FusoComercial.InicioDoDia(CicloDatas.Ciclo(FusoComercial.Data(ciclo.Inicio), 1, periodicidade).Fim);
            var mesmaPeriodicidade = periodicidade == a.Periodicidade && periodicidade == ciclo.Periodicidade;
            ciclo.PlanoChave = plano;
            ciclo.Periodicidade = periodicidade;
            ciclo.Preco = preco;
            ciclo.Fim = fim;
            ciclo.Status = preco + divida > 0 ? "a_pagar" : "pago";
            await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE assinatura_ciclo SET plano_chave = @PlanoChave, periodicidade = @Periodicidade, preco = @Preco, fim = @Fim, status = @Status, updated_at = NOW()
 WHERE id = @Id", new { ciclo.Id, ciclo.PlanoChave, ciclo.Periodicidade, ciclo.Preco, ciclo.Fim, ciclo.Status }, tx, cancellationToken: ct));
            if (preco + divida <= 0) return true;

            var meio = mesmaPeriodicidade && a.RenovacaoAutomatica && a.MpPreapprovalStatus == "authorized" ? "cartao_recorrente" : null;
            var nova = await AssinaturaSql.NovaCobrancaDoCicloAsync(cx, tx, a, ciclo, await NomeDoPlanoAsync(cx, tx, plano, ct), preco + divida, vencimento, meio, ct,
                divida, antiga?.DividaDesde);
            if (divida > 0)
                await cx.ExecuteAsync(new CommandDefinition("UPDATE comercial_cobranca SET agrupada_em = @Nova WHERE agrupada_em = @Antiga",
                    new { Nova = nova, Antiga = antiga!.Id }, tx, cancellationToken: ct));
            return true;
        }

        // ---------- primeiro ciclo ----------

        /// <summary>
        /// Primeiro ciclo: começa no fim do teste (a fatura fica para a loja pagar) ou hoje, se o teste já acabou ou a loja já é ativa.
        /// Quem volta a assinar devendo recebe uma cobrança só, com a dívida e o primeiro período, para pagar hoje.
        /// Em implantação ainda não há ciclo: só a escolha do plano (o teste começa ao "Começar a operar").
        /// </summary>
        public static async Task GarantirPrimeiroCicloAsync(NpgsqlConnection cx, Guid loja, CancellationToken ct)
        {
            var a = await AssinaturaSql.VivaAsync(cx, null, loja, false, ct);
            if (a == null || a.Status != StatusAssinatura.Pendente || a.IdCicloAtual != null) return;
            var situ = await cx.QueryFirstOrDefaultAsync<(string? Situacao, DateTimeOffset? Fim)>(new CommandDefinition(
                "SELECT situacao_comercial, teste_termina_em FROM estabelecimentos WHERE id = @Loja", new { Loja = loja }, cancellationToken: ct));
            if (situ.Situacao is null or SituacoesComerciais.Implantacao) return;

            await using var tx = await cx.BeginTransactionAsync(ct);
            a = await AssinaturaSql.PorIdAsync(cx, tx, a.Id, true, ct);
            if (a == null || !a.Viva || a.IdCicloAtual != null) return;
            var antigas = (await cx.QueryAsync<(Guid Id, decimal Valor, DateTimeOffset Desde)>(new CommandDefinition(@"
SELECT c.id, CASE WHEN c.divida_valor > 0 THEN c.divida_valor ELSE c.valor END, COALESCE(c.divida_desde, c.vencimento)
  FROM comercial_cobranca c JOIN assinatura x ON x.id = c.id_assinatura
 WHERE c.id_estabelecimento = @Loja AND c.tipo = 'ciclo' AND c.status IN ('aberta', 'processando') AND x.status = 'encerrada'
   FOR UPDATE OF c", new { Loja = loja }, tx, cancellationToken: ct))).ToList();
            var divida = antigas.Sum(x => x.Valor);

            var agora = DateTimeOffset.UtcNow;
            var inicio = divida == 0 && situ.Situacao == SituacoesComerciais.Teste && situ.Fim > agora ? situ.Fim.Value : agora;
            var ancora = FusoComercial.Data(inicio);
            var (ini, fim) = CicloDatas.Ciclo(ancora, 1, a.Periodicidade);
            var numero = await cx.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COALESCE(MAX(numero), 0) + 1 FROM assinatura_ciclo WHERE id_assinatura = @A",
                new { A = a.Id }, tx, cancellationToken: ct));
            var nome = await NomeDoPlanoAsync(cx, tx, a.PlanoChave, ct);
            var ciclo = await AssinaturaSql.CriarCicloAsync(cx, tx, a, numero, a.PlanoChave, nome, a.Periodicidade, a.Preco, ini, fim, null, ct,
                dividaValor: divida, dividaDesde: divida > 0 ? antigas.Min(x => x.Desde) : null);
            if (divida > 0)
            {
                var nova = await CobrancaDoCicloAsync(cx, tx, ciclo.Id, ct);
                await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE comercial_cobranca SET status = 'cancelada', cancelada_em = NOW(), cancelamento_motivo = 'agrupada', agrupada_em = @Nova, versao = versao + 1, updated_at = NOW()
 WHERE id = ANY(@Ids)", new { Nova = nova!.Id, Ids = antigas.Select(x => x.Id).ToArray() }, tx, cancellationToken: ct));
                await PagamentoAuditoria.RegistrarAsync(cx, tx, loja, "assinatura", a.Id.ToString(), "divida_somada", "sistema", null,
                    new { divida, cobranca = nova.Id }, ct);
            }
            await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE assinatura SET ancora = @Ancora, ancora_ciclo = @N, id_ciclo_atual = @C, periodo_inicio = @Ini, periodo_fim = @Fim, versao = versao + 1, updated_at = NOW()
 WHERE id = @A", new { Ancora = ancora, N = ciclo.Numero, C = ciclo.Id, Ini = ciclo.Inicio, Fim = ciclo.Fim, A = a.Id }, tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
        }

        // ---------- próximo ciclo e virada ----------

        /// <summary>Fatura do ciclo seguinte, alguns dias antes do fim. Cancelada não renova; a troca marcada vale no ciclo novo.</summary>
        public static async Task GerarProximoCicloAsync(NpgsqlConnection cx, NpgsqlTransaction tx, Guid assinaturaId, DateTimeOffset agora, CancellationToken ct)
        {
            var a = await AssinaturaSql.PorIdAsync(cx, tx, assinaturaId, true, ct);
            if (a is not { Status: StatusAssinatura.Ativa, PeriodoFim: { } fimAtual } || a.CancelamentoSolicitadoEm != null || a.Ancora == null) return;
            var periodicidade = a.MudancaPeriodicidade ?? a.Periodicidade;
            if (fimAtual - agora > TimeSpan.FromDays(DiasAntecedencia(a.Periodicidade))) return;
            var atual = await CicloAtualAsync(cx, tx, a, ct);
            if (atual == null || (await CiclosSeguintesAsync(cx, tx, a, ct)).Count > 0) return;

            var numero = atual.Numero + 1;
            // Um ciclo cancelado que já tinha recebido pagamento não volta: usa o número seguinte a ele.
            var pagoCancelado = await cx.ExecuteScalarAsync<int?>(new CommandDefinition(@"
SELECT MAX(ci.numero) FROM assinatura_ciclo ci WHERE ci.id_assinatura = @A AND ci.numero >= @N
   AND EXISTS (SELECT 1 FROM comercial_cobranca c WHERE c.id_ciclo = ci.id AND c.status = 'paga')", new { A = a.Id, N = numero }, tx, cancellationToken: ct));
            if (pagoCancelado.HasValue) numero = pagoCancelado.Value + 1;

            var temTroca = a.MudancaPlano != null;
            var plano = a.MudancaPlano ?? a.PlanoChave;
            var preco = temTroca ? a.MudancaPreco ?? a.Preco : a.Preco;
            (DateOnly Inicio, DateOnly Fim) datas = periodicidade == a.Periodicidade
                ? CicloDatas.Ciclo(a.Ancora.Value, numero - a.AncoraCiclo + 1, periodicidade)
                : CicloDatas.Ciclo(FusoComercial.Data(fimAtual), 1, periodicidade);
            var hoje = FusoComercial.Data(agora);
            if (datas.Fim <= hoje)
            {
                // Ficou um período inteiro sem fatura (assinatura parada): recomeça de hoje, sem cobrar meses passados.
                datas = CicloDatas.Ciclo(hoje, 1, periodicidade);
                await cx.ExecuteAsync(new CommandDefinition("UPDATE assinatura SET ancora = @Hoje, ancora_ciclo = @N, updated_at = NOW() WHERE id = @A",
                    new { Hoje = hoje, N = numero, A = a.Id }, tx, cancellationToken: ct));
            }
            // Cartao recorrente so cobra o mesmo intervalo: trocar mensal/anual volta para pagamento manual.
            var meio = a.MpPreapprovalStatus == "authorized" && a.RenovacaoAutomatica && periodicidade == a.Periodicidade ? "cartao_recorrente" : null;
            var vencimento = FusoComercial.InicioDoDia(datas.Inicio > hoje ? datas.Inicio : hoje);
            await AssinaturaSql.CriarCicloAsync(cx, tx, a, numero, plano, await NomeDoPlanoAsync(cx, tx, plano, ct), periodicidade, preco, datas.Inicio, datas.Fim,
                meio, ct, vencimento);
        }

        /// <summary>
        /// Fim do período: passa para o ciclo seguinte (pago ou não: atraso só informa) ou encerra a cancelada, depois de consumir
        /// o que já foi pago à frente. Devolve true quando algo mudou.
        /// </summary>
        public static async Task<bool> VirarCicloAsync(NpgsqlConnection cx, NpgsqlTransaction tx, Guid assinaturaId, DateTimeOffset agora, CancellationToken ct)
        {
            var a = await AssinaturaSql.PorIdAsync(cx, tx, assinaturaId, true, ct);
            if (a is not { Status: StatusAssinatura.Ativa, PeriodoFim: { } fim, IdCicloAtual: not null } || fim > agora) return false;
            var proximo = (await CiclosSeguintesAsync(cx, tx, a, ct)).FirstOrDefault();

            if (a.CancelamentoSolicitadoEm != null && proximo is not { Status: "pago" })
            {
                await CancelarTentativasDeUpgradeAsync(cx, tx, a.Id, MotivosCancelamentoCobranca.AssinaturaCancelada, null, ct);
                await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE assinatura SET status = 'encerrada', encerrada_em = NOW(), encerramento_motivo = 'fim_do_periodo', renovacao_automatica = FALSE,
       mudanca_plano = NULL, mudanca_periodicidade = NULL, mudanca_preco = NULL, versao = versao + 1, updated_at = NOW() WHERE id = @Id",
                    new { a.Id }, tx, cancellationToken: ct));
                await PagamentoAuditoria.RegistrarAsync(cx, tx, a.IdEstabelecimento, "assinatura", a.Id.ToString(), "encerrada", "sistema", null,
                    new { motivo = a.CancelamentoMotivo, fim }, ct);
                await AssinaturaSql.AvisarAsync(cx, tx, a.IdEstabelecimento, null, $"encerrada:{a.Id:N}", "encerrada",
                    "Sua assinatura terminou. O histórico continua aqui; assine de novo quando quiser.", ct);
                return true;
            }
            if (proximo == null) return false; // a geracao roda antes; tenta de novo na proxima volta

            var trocouPeriodicidade = proximo.Periodicidade != a.Periodicidade;
            // A troca marcada só sai quando o ciclo que começa já é o dela; com a próxima paga no plano antigo, vale no seguinte.
            var cumpriuTroca = a.MudancaPlano != null && proximo.PlanoChave == a.MudancaPlano
                && proximo.Periodicidade == (a.MudancaPeriodicidade ?? a.Periodicidade) && proximo.Preco == (a.MudancaPreco ?? proximo.Preco);
            await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE assinatura SET id_ciclo_atual = @C, periodo_inicio = @Ini, periodo_fim = @Fim, plano_chave = @Plano, periodicidade = @Per, preco = @Preco,
       ancora = CASE WHEN @Troca THEN @Ancora ELSE ancora END, ancora_ciclo = CASE WHEN @Troca THEN @N ELSE ancora_ciclo END,
       -- O cartao recorrente cobra no intervalo antigo: trocar mensal/anual desliga (o sincronizador cancela no MP).
       renovacao_automatica = CASE WHEN @Troca THEN FALSE ELSE renovacao_automatica END,
       mudanca_plano = CASE WHEN @Cumpriu THEN NULL ELSE mudanca_plano END,
       mudanca_periodicidade = CASE WHEN @Cumpriu THEN NULL ELSE mudanca_periodicidade END,
       mudanca_preco = CASE WHEN @Cumpriu THEN NULL ELSE mudanca_preco END,
       mudanca_pedida_em = CASE WHEN @Cumpriu THEN NULL ELSE mudanca_pedida_em END,
       mudanca_pedida_por = CASE WHEN @Cumpriu THEN NULL ELSE mudanca_pedida_por END,
       versao = versao + 1, updated_at = NOW()
 WHERE id = @A", new
            {
                C = proximo.Id, Ini = proximo.Inicio, Fim = proximo.Fim, Plano = proximo.PlanoChave, Per = proximo.Periodicidade, Preco = proximo.Preco,
                Troca = trocouPeriodicidade, Ancora = FusoComercial.Data(proximo.Inicio), N = proximo.Numero, Cumpriu = cumpriuTroca, A = a.Id
            }, tx, cancellationToken: ct));
            if (proximo.PlanoChave != a.PlanoChave)
            {
                // Downgrade agendado: modulos do plano novo a partir de agora. Nada e apagado; o que saiu so fica travado.
                await AssinaturaSql.AplicarPlanoAsync(cx, tx, a.IdEstabelecimento, proximo.PlanoChave, "sistema", null, ct);
                await PagamentoAuditoria.RegistrarAsync(cx, tx, a.IdEstabelecimento, "assinatura", a.Id.ToString(), "downgrade_aplicado", "sistema", null,
                    new { de = a.PlanoChave, para = proximo.PlanoChave, proximo.Periodicidade, proximo.Preco }, ct);
            }
            await cx.ExecuteAsync(new CommandDefinition("UPDATE assinatura_ciclo SET aplicado_em = COALESCE(aplicado_em, NOW()) WHERE id = @Id",
                new { proximo.Id }, tx, cancellationToken: ct));
            await PagamentoAuditoria.RegistrarAsync(cx, tx, a.IdEstabelecimento, "assinatura", a.Id.ToString(), "ciclo_virado", "sistema", null,
                new { ciclo = proximo.Numero, proximo.Inicio, proximo.Fim }, ct);
            return true;
        }

        // ---------- trocas ----------

        /// <summary>Troca de plano que vale já (upgrade pago, de graça por ser pequena, ou feita pela Gestão). Mensal para anual começa um ano novo.</summary>
        public static async Task AplicarMudancaAgoraAsync(NpgsqlConnection cx, NpgsqlTransaction tx, AssinaturaRow a, string tipo, string plano, string periodicidade,
            decimal preco, string ator, int? usuarioId, CancellationToken ct)
        {
            var atual = await CicloAtualAsync(cx, tx, a, ct);
            var agora = DateTimeOffset.UtcNow;
            var seguintes = await CiclosSeguintesAsync(cx, tx, a, ct);
            if (tipo == TiposMudanca.ParaAnual && atual != null)
            {
                var hoje = FusoComercial.Data(agora);
                var comeca = FusoComercial.Data(atual.Inicio) > hoje ? FusoComercial.Data(atual.Inicio) : hoje;
                // O mês acaba agora. A fatura em aberto deixa de valer; a próxima já paga entrou como crédito na conta da troca.
                foreach (var ciclo in seguintes.Prepend(atual))
                {
                    var cobranca = await CobrancaDoCicloAsync(cx, tx, ciclo.Id, ct);
                    if (cobranca != null && StatusCobrancaComercial.EmAberto(cobranca.Status))
                        await CancelarCobrancaAsync(cx, tx, cobranca, MotivosCancelamentoCobranca.TrocaParaAnual, usuarioId, ct);
                    if (ciclo.Id != atual.Id)
                        await cx.ExecuteAsync(new CommandDefinition("UPDATE assinatura_ciclo SET status = 'cancelado', updated_at = NOW() WHERE id = @Id",
                            new { ciclo.Id }, tx, cancellationToken: ct));
                }
                await EncerrarCicloAgoraAsync(cx, tx, atual.Id, ct);
                var numero = await ProximoNumeroAsync(cx, tx, a.Id, ct);
                var (ini, fim) = CicloDatas.Ciclo(comeca, 1, Periodicidades.Anual);
                // Preco 0 no ciclo: ele e pago pela cobranca de upgrade (sem divida nova).
                var novo = await AssinaturaSql.CriarCicloAsync(cx, tx, a, numero, plano, await NomeDoPlanoAsync(cx, tx, plano, ct), Periodicidades.Anual, 0,
                    ini, fim, null, ct);
                var vale = novo.Inicio <= agora;
                await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE assinatura_ciclo SET preco = @Preco, status = 'pago', aplicado_em = CASE WHEN @Vale THEN NOW() END WHERE id = @C;
UPDATE assinatura SET plano_chave = @Plano, periodicidade = 'anual', preco = @Preco, ancora = @Ancora, ancora_ciclo = @N, id_ciclo_atual = @C,
       periodo_inicio = @Ini, periodo_fim = @Fim, renovacao_automatica = FALSE, mudanca_plano = NULL, mudanca_periodicidade = NULL, mudanca_preco = NULL,
       mudanca_pedida_em = NULL, versao = versao + 1, updated_at = NOW()
 WHERE id = @A", new { C = novo.Id, Preco = preco, Plano = plano, Ancora = comeca, N = numero, Ini = novo.Inicio, Fim = novo.Fim, Vale = vale, A = a.Id },
                    tx, cancellationToken: ct));
                if (vale) await AssinaturaSql.AplicarPlanoAsync(cx, tx, a.IdEstabelecimento, plano, ator, usuarioId, ct);
            }
            else
            {
                await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE assinatura SET plano_chave = @Plano, preco = @Preco, mudanca_plano = NULL, mudanca_periodicidade = NULL, mudanca_preco = NULL, mudanca_pedida_em = NULL,
       versao = versao + 1, updated_at = NOW() WHERE id = @A;
UPDATE assinatura_ciclo SET plano_chave = @Plano, preco = CASE WHEN preco = 0 THEN @Preco ELSE preco END, updated_at = NOW() WHERE id = @C;",
                    new { Plano = plano, Preco = preco, A = a.Id, C = a.IdCicloAtual }, tx, cancellationToken: ct));
                // A próxima fatura: em aberto é refeita com o valor novo; paga, a diferença já entrou na conta da troca.
                foreach (var ciclo in seguintes)
                    if (!await RefazerCicloAsync(cx, tx, a, ciclo, plano, a.Periodicidade, preco, MotivosCancelamentoCobranca.PlanoTrocado, usuarioId, ct))
                        await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE assinatura_ciclo SET plano_chave = @Plano, periodicidade = @Per, preco = @Preco, updated_at = NOW() WHERE id = @Id",
                            new { Plano = plano, Per = a.Periodicidade, Preco = preco, ciclo.Id }, tx, cancellationToken: ct));
                // Quem ainda está no teste continua nele: o plano entra quando o ciclo começar.
                if (atual == null || atual.Inicio <= agora)
                    await AssinaturaSql.AplicarPlanoAsync(cx, tx, a.IdEstabelecimento, plano, ator, usuarioId, ct);
            }
            await PagamentoAuditoria.RegistrarAsync(cx, tx, a.IdEstabelecimento, "assinatura", a.Id.ToString(), "plano_alterado", ator, usuarioId,
                new { de = a.PlanoChave, para = plano, tipo, periodicidade, preco }, ct);
        }

        private static Task EncerrarCicloAgoraAsync(NpgsqlConnection cx, NpgsqlTransaction tx, Guid ciclo, CancellationToken ct) =>
            cx.ExecuteAsync(new CommandDefinition(
                "UPDATE assinatura_ciclo SET fim = GREATEST(inicio + INTERVAL '1 minute', LEAST(fim, NOW())), updated_at = NOW() WHERE id = @Id",
                new { Id = ciclo }, tx, cancellationToken: ct));

        private static Task<int> ProximoNumeroAsync(NpgsqlConnection cx, NpgsqlTransaction tx, Guid assinatura, CancellationToken ct) =>
            cx.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COALESCE(MAX(numero), 0) + 1 FROM assinatura_ciclo WHERE id_assinatura = @A",
                new { A = assinatura }, tx, cancellationToken: ct));

        /// <summary>Redução (ou troca para o fim do período): vale no próximo vencimento. A próxima fatura em aberto já é refeita.</summary>
        public static async Task AgendarMudancaAsync(NpgsqlConnection cx, NpgsqlTransaction tx, AssinaturaRow a, string plano, string periodicidade, decimal preco,
            int? usuarioId, CancellationToken ct)
        {
            await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE assinatura SET mudanca_plano = @Plano, mudanca_periodicidade = @Per, mudanca_preco = @Preco, mudanca_pedida_em = NOW(), mudanca_pedida_por = @U,
       versao = versao + 1, updated_at = NOW() WHERE id = @A", new { Plano = plano, Per = periodicidade, Preco = preco, U = usuarioId, A = a.Id }, tx, cancellationToken: ct));
            var proximo = (await CiclosSeguintesAsync(cx, tx, a, ct)).FirstOrDefault();
            if (proximo != null)
                await RefazerCicloAsync(cx, tx, a, proximo, plano, periodicidade, preco, MotivosCancelamentoCobranca.PlanoTrocado, usuarioId, ct);
            await CancelarTentativasDeUpgradeAsync(cx, tx, a.Id, MotivosCancelamentoCobranca.Substituida, usuarioId, ct);
            await PagamentoAuditoria.RegistrarAsync(cx, tx, a.IdEstabelecimento, "assinatura", a.Id.ToString(), "downgrade_agendado", "usuario", usuarioId,
                new { de = a.PlanoChave, para = plano, periodicidade, precoNovo = preco }, ct);
        }

        public static async Task DesfazerMudancaAsync(NpgsqlConnection cx, NpgsqlTransaction tx, AssinaturaRow a, int? usuarioId, CancellationToken ct)
        {
            var proximo = (await CiclosSeguintesAsync(cx, tx, a, ct)).FirstOrDefault();
            if (proximo != null && proximo.PlanoChave == a.MudancaPlano && proximo.Periodicidade == (a.MudancaPeriodicidade ?? a.Periodicidade))
            {
                if (!await RefazerCicloAsync(cx, tx, a, proximo, a.PlanoChave, a.Periodicidade, a.Preco, MotivosCancelamentoCobranca.PlanoTrocado, usuarioId, ct))
                    throw new PagamentoException(AssinaturaCodigos.SemMudanca, 409,
                        "A próxima fatura já foi paga com o plano novo. Quando ele começar, faça a troca de volta.");
            }
            await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE assinatura SET mudanca_plano = NULL, mudanca_periodicidade = NULL, mudanca_preco = NULL, mudanca_pedida_em = NULL, mudanca_pedida_por = NULL,
       versao = versao + 1, updated_at = NOW() WHERE id = @A", new { A = a.Id }, tx, cancellationToken: ct));
            await PagamentoAuditoria.RegistrarAsync(cx, tx, a.IdEstabelecimento, "assinatura", a.Id.ToString(), "mudanca_desfeita", "usuario", usuarioId,
                new { a.MudancaPlano }, ct);
        }

        /// <summary>Antes do primeiro pagamento a troca é livre: muda o valor e mantém o vencimento.</summary>
        public static async Task TrocarPendenteAsync(NpgsqlConnection cx, NpgsqlTransaction tx, AssinaturaRow a, string plano, string periodicidade, decimal preco,
            int? usuarioId, CancellationToken ct)
        {
            var atual = await CicloAtualAsync(cx, tx, a, ct);
            if (atual != null)
                await RefazerCicloAsync(cx, tx, a, atual, plano, periodicidade, preco, MotivosCancelamentoCobranca.PlanoTrocado, usuarioId, ct);
            await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE assinatura SET plano_chave = @Plano, periodicidade = @Per, preco = @Preco, periodo_fim = COALESCE(@Fim, periodo_fim),
       ancora = COALESCE(@Ancora, ancora), ancora_ciclo = COALESCE(@N, ancora_ciclo),
       renovacao_automatica = renovacao_automatica AND periodicidade = @Per, mudanca_plano = NULL, mudanca_periodicidade = NULL, mudanca_preco = NULL,
       versao = versao + 1, updated_at = NOW() WHERE id = @A",
                new
                {
                    A = a.Id, Plano = plano, Per = periodicidade, Preco = preco, Fim = atual?.Fim,
                    Ancora = atual == null ? (DateOnly?)null : FusoComercial.Data(atual.Inicio), N = atual?.Numero
                }, tx, cancellationToken: ct));
            await PagamentoAuditoria.RegistrarAsync(cx, tx, a.IdEstabelecimento, "assinatura", a.Id.ToString(), "plano_alterado", "usuario", usuarioId,
                new { de = a.PlanoChave, para = plano, periodicidade, precoNovo = preco, pendente = true }, ct);
        }

        // ---------- cancelar / retomar / encerrar ----------

        /// <summary>
        /// Para as renovações e mantém o que já foi pago. Sem estorno. A fatura de um período que não começou deixa de existir;
        /// a que está vencida continua devida.
        /// </summary>
        public static async Task CancelarNoFimAsync(NpgsqlConnection cx, NpgsqlTransaction tx, AssinaturaRow a, string? motivo, int? usuarioId, DateTimeOffset agora,
            CancellationToken ct)
        {
            var hoje = FusoComercial.InicioDeHoje(agora);
            foreach (var ciclo in await CiclosSeguintesAsync(cx, tx, a, ct))
            {
                if (ciclo.Quitado) continue;
                var cobranca = await CobrancaDoCicloAsync(cx, tx, ciclo.Id, ct);
                if (cobranca != null && cobranca.Vencimento < hoje) continue;
                if (cobranca != null) await CancelarCobrancaAsync(cx, tx, cobranca, MotivosCancelamentoCobranca.AssinaturaCancelada, usuarioId, ct);
                await cx.ExecuteAsync(new CommandDefinition("UPDATE assinatura_ciclo SET status = 'cancelado', updated_at = NOW() WHERE id = @Id",
                    new { ciclo.Id }, tx, cancellationToken: ct));
            }
            await CancelarTentativasDeUpgradeAsync(cx, tx, a.Id, MotivosCancelamentoCobranca.AssinaturaCancelada, usuarioId, ct);
            await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE assinatura SET cancelamento_solicitado_em = NOW(), cancelamento_por = @U, cancelamento_motivo = @M, renovacao_automatica = FALSE,
       mudanca_plano = NULL, mudanca_periodicidade = NULL, mudanca_preco = NULL, versao = versao + 1, updated_at = NOW() WHERE id = @A",
                new { A = a.Id, U = usuarioId, M = motivo }, tx, cancellationToken: ct));
        }

        /// <summary>
        /// Encerra já (desistência antes de pagar, ou decisão da Gestão): a cobrança de período que não venceu some; a vencida continua
        /// devida, inclusive a antiga somada na volta.
        /// </summary>
        public static async Task EncerrarAgoraAsync(NpgsqlConnection cx, NpgsqlTransaction tx, AssinaturaRow a, string encerramento, string? motivo, int? usuarioId,
            DateTimeOffset agora, CancellationToken ct)
        {
            var hoje = FusoComercial.InicioDeHoje(agora);
            var abertas = (await cx.QueryAsync<CobrancaRow>(new CommandDefinition($@"
SELECT {AssinaturaSql.ColunasCobranca} FROM comercial_cobranca WHERE id_assinatura = @A AND tipo = 'ciclo' AND status IN ('aberta', 'processando') FOR UPDATE",
                new { A = a.Id }, tx, cancellationToken: ct))).ToList();
            foreach (var c in abertas.Where(c => c.DividaValor > 0 || c.Vencimento >= hoje))
                await CancelarCobrancaAsync(cx, tx, c, MotivosCancelamentoCobranca.AssinaturaCancelada, usuarioId, ct);
            await CancelarTentativasDeUpgradeAsync(cx, tx, a.Id, MotivosCancelamentoCobranca.AssinaturaCancelada, usuarioId, ct);
            await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE assinatura_ciclo ci SET status = 'cancelado', updated_at = NOW()
 WHERE ci.id_assinatura = @A AND ci.status = 'a_pagar'
   AND NOT EXISTS (SELECT 1 FROM comercial_cobranca c WHERE c.id_ciclo = ci.id AND c.status <> 'cancelada');
UPDATE assinatura SET status = 'encerrada', encerrada_em = NOW(), encerramento_motivo = @E,
       cancelamento_solicitado_em = COALESCE(cancelamento_solicitado_em, NOW()), cancelamento_por = COALESCE(cancelamento_por, @U),
       cancelamento_motivo = COALESCE(@M, cancelamento_motivo), renovacao_automatica = FALSE,
       mudanca_plano = NULL, mudanca_periodicidade = NULL, mudanca_preco = NULL, versao = versao + 1, updated_at = NOW()
 WHERE id = @A;", new { A = a.Id, E = a.AtivadaEm == null ? Encerramentos.AntesDoPagamento : encerramento, U = usuarioId, M = motivo }, tx, cancellationToken: ct));
            await AssinaturaSql.RecalcularFinanceiroAsync(cx, tx, a.Id, agora, ct);
        }

        /// <summary>Desfaz o cancelamento e já gera a próxima fatura, se estiver na hora.</summary>
        public static async Task RetomarAsync(NpgsqlConnection cx, NpgsqlTransaction tx, AssinaturaRow a, int? usuarioId, DateTimeOffset agora, CancellationToken ct)
        {
            await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE assinatura SET cancelamento_solicitado_em = NULL, cancelamento_por = NULL, cancelamento_motivo = NULL, versao = versao + 1, updated_at = NOW() WHERE id = @A",
                new { A = a.Id }, tx, cancellationToken: ct));
            await PagamentoAuditoria.RegistrarAsync(cx, tx, a.IdEstabelecimento, "assinatura", a.Id.ToString(), "cancelamento_desfeito", "usuario", usuarioId,
                new { a.CancelamentoMotivo }, ct);
            await GerarProximoCicloAsync(cx, tx, a.Id, agora, ct);
        }

        // ---------- Gestão (ativar, mudar e parar de cobrar) ----------

        public static Task<bool> TemAssinaturasAsync(NpgsqlConnection cx, NpgsqlTransaction? tx, CancellationToken ct) =>
            cx.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT to_regclass('assinatura') IS NOT NULL", transaction: tx, cancellationToken: ct));

        /// <summary>
        /// A Gestão ativou ou mudou o plano: a assinatura acompanha (cria, ou atualiza a que existe), para nada ser desfeito depois.
        /// Cortesia vale R$ 0. Plano sem preço no catálogo (personalizado) só vira assinatura nova quando é cortesia.
        /// </summary>
        public static async Task AtivarPelaGestaoAsync(NpgsqlConnection cx, NpgsqlTransaction tx, Guid loja, string planoChave, string? periodicidade, bool cortesia,
            DateOnly primeiraCobranca, int? usuarioId, CancellationToken ct)
        {
            if (!await TemAssinaturasAsync(cx, tx, ct)) return;
            var plano = await AssinaturaSql.PlanoAsync(cx, tx, planoChave, ct);
            var a = await AssinaturaSql.VivaAsync(cx, tx, loja, true, ct);
            if (plano == null && a == null && !cortesia) return;

            var per = periodicidade ?? a?.Periodicidade ?? Periodicidades.Mensal;
            if (plano != null && plano.PrecoDe(per) == null && !cortesia) per = Periodicidades.Mensal;
            var mantemPreco = a != null && a.PlanoChave == planoChave && a.Periodicidade == per && a.Preco > 0;
            var preco = cortesia ? 0 : mantemPreco ? a!.Preco : plano?.PrecoDe(per) ?? a?.Preco ?? 0;
            var nome = plano?.Nome ?? planoChave;

            if (a == null)
            {
                await CriarPelaGestaoAsync(cx, tx, loja, planoChave, nome, per, preco, primeiraCobranca, usuarioId, ct);
                return;
            }
            if (a.Status == StatusAssinatura.Pendente)
            {
                await AtivarPendenteAsync(cx, tx, a, planoChave, nome, per, preco, primeiraCobranca, usuarioId, ct);
                return;
            }
            if (a.CancelamentoSolicitadoEm != null) await RetomarAsync(cx, tx, a, usuarioId, DateTimeOffset.UtcNow, ct);
            a = (await AssinaturaSql.PorIdAsync(cx, tx, a.Id, true, ct))!;
            if (a.PlanoChave == planoChave && a.Periodicidade == per && a.Preco == preco) return;
            await MudarAgoraPelaGestaoAsync(cx, tx, a, planoChave, per, preco, usuarioId, ct);
        }

        /// <summary>Assinatura nova já ativa; a primeira cobrança vence no dia pedido.</summary>
        public static async Task<Guid> CriarPelaGestaoAsync(NpgsqlConnection cx, NpgsqlTransaction tx, Guid loja, string plano, string nomePlano, string periodicidade,
            decimal preco, DateOnly primeiraCobranca, int? usuarioId, CancellationToken ct)
        {
            var id = Guid.NewGuid();
            await cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO assinatura (id, id_estabelecimento, plano_chave, periodicidade, preco, status, contratada_por, ativada_em)
VALUES (@Id, @Loja, @Plano, @Per, @Preco, 'ativa', @U, NOW())", new { Id = id, Loja = loja, Plano = plano, Per = periodicidade, Preco = preco, U = usuarioId },
                tx, cancellationToken: ct));
            var a = (await AssinaturaSql.PorIdAsync(cx, tx, id, true, ct))!;
            await IniciarCicloAsync(cx, tx, a, nomePlano, 1, primeiraCobranca, ct);
            await PagamentoAuditoria.RegistrarAsync(cx, tx, loja, "assinatura", id.ToString(), "criada_pela_gestao", "usuario", usuarioId,
                new { plano, periodicidade, preco, primeiraCobranca }, ct);
            return id;
        }

        /// <summary>A Gestão ativa uma assinatura que ainda não tinha sido paga: a cobrança antiga dá lugar à do plano ativado.</summary>
        public static async Task AtivarPendenteAsync(NpgsqlConnection cx, NpgsqlTransaction tx, AssinaturaRow a, string plano, string nomePlano, string periodicidade,
            decimal preco, DateOnly primeiraCobranca, int? usuarioId, CancellationToken ct)
        {
            var atual = await CicloAtualAsync(cx, tx, a, ct);
            if (atual != null)
            {
                var cobranca = await CobrancaDoCicloAsync(cx, tx, atual.Id, ct);
                if (cobranca != null) await CancelarCobrancaAsync(cx, tx, cobranca, MotivosCancelamentoCobranca.Substituida, usuarioId, ct);
                await cx.ExecuteAsync(new CommandDefinition("UPDATE assinatura_ciclo SET status = 'cancelado', updated_at = NOW() WHERE id = @Id",
                    new { atual.Id }, tx, cancellationToken: ct));
            }
            await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE assinatura SET status = 'ativa', ativada_em = COALESCE(ativada_em, NOW()), plano_chave = @Plano, periodicidade = @Per, preco = @Preco,
       renovacao_automatica = renovacao_automatica AND periodicidade = @Per, cancelamento_solicitado_em = NULL, cancelamento_por = NULL,
       cancelamento_motivo = NULL, versao = versao + 1, updated_at = NOW() WHERE id = @A",
                new { A = a.Id, Plano = plano, Per = periodicidade, Preco = preco }, tx, cancellationToken: ct));
            a.PlanoChave = plano;
            a.Periodicidade = periodicidade;
            a.Preco = preco;
            await IniciarCicloAsync(cx, tx, a, nomePlano, await ProximoNumeroAsync(cx, tx, a.Id, ct), primeiraCobranca, ct);
            await PagamentoAuditoria.RegistrarAsync(cx, tx, a.IdEstabelecimento, "assinatura", a.Id.ToString(), "ativada_pela_gestao", "usuario", usuarioId,
                new { plano, periodicidade, preco, primeiraCobranca }, ct);
        }

        private static async Task IniciarCicloAsync(NpgsqlConnection cx, NpgsqlTransaction tx, AssinaturaRow a, string nomePlano, int numero, DateOnly inicio,
            CancellationToken ct)
        {
            var (ini, fim) = CicloDatas.Ciclo(inicio, 1, a.Periodicidade);
            var ciclo = await AssinaturaSql.CriarCicloAsync(cx, tx, a, numero, a.PlanoChave, nomePlano, a.Periodicidade, a.Preco, ini, fim, null, ct);
            await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE assinatura_ciclo SET aplicado_em = NOW() WHERE id = @C;
UPDATE assinatura SET ancora = @Ancora, ancora_ciclo = @N, id_ciclo_atual = @C, periodo_inicio = @Ini, periodo_fim = @Fim, versao = versao + 1, updated_at = NOW()
 WHERE id = @A;", new { Ancora = inicio, N = ciclo.Numero, C = ciclo.Id, Ini = ciclo.Inicio, Fim = ciclo.Fim, A = a.Id }, tx, cancellationToken: ct));
        }

        /// <summary>Mudança imediata numa assinatura ativa, sem cobrar diferença: o que ainda não venceu passa a ter o plano novo.</summary>
        public static async Task MudarAgoraPelaGestaoAsync(NpgsqlConnection cx, NpgsqlTransaction tx, AssinaturaRow a, string plano, string periodicidade, decimal preco,
            int? usuarioId, CancellationToken ct)
        {
            if (periodicidade != a.Periodicidade)
            {
                await TrocarCicloAgoraAsync(cx, tx, a, plano, periodicidade, preco, usuarioId, ct);
                return;
            }
            var atual = await CicloAtualAsync(cx, tx, a, ct);
            if (atual != null && !atual.Quitado)
            {
                var cobranca = await CobrancaDoCicloAsync(cx, tx, atual.Id, ct);
                if (cobranca != null && cobranca.Vencimento >= FusoComercial.InicioDeHoje(DateTimeOffset.UtcNow))
                    await RefazerCicloAsync(cx, tx, a, atual, plano, periodicidade, preco, MotivosCancelamentoCobranca.PlanoTrocado, usuarioId, ct);
            }
            await CancelarTentativasDeUpgradeAsync(cx, tx, a.Id, MotivosCancelamentoCobranca.Substituida, usuarioId, ct);
            await AplicarMudancaAgoraAsync(cx, tx, a, TiposMudanca.Upgrade, plano, periodicidade, preco, "usuario", usuarioId, ct);
        }

        /// <summary>Troca de mensal/anual agora pela Gestão: o período atual termina e começa um novo hoje. Nada pago é devolvido sozinho.</summary>
        public static async Task TrocarCicloAgoraAsync(NpgsqlConnection cx, NpgsqlTransaction tx, AssinaturaRow a, string plano, string periodicidade, decimal preco,
            int? usuarioId, CancellationToken ct)
        {
            var agora = DateTimeOffset.UtcNow;
            var hoje = FusoComercial.InicioDeHoje(agora);
            var atual = await CicloAtualAsync(cx, tx, a, ct);
            var seguintes = await CiclosSeguintesAsync(cx, tx, a, ct);
            foreach (var ciclo in atual == null ? seguintes : seguintes.Prepend(atual).ToList())
            {
                var cobranca = await CobrancaDoCicloAsync(cx, tx, ciclo.Id, ct);
                var aberta = cobranca != null && StatusCobrancaComercial.EmAberto(cobranca.Status);
                if (aberta && cobranca!.Vencimento >= hoje)
                    await CancelarCobrancaAsync(cx, tx, cobranca, MotivosCancelamentoCobranca.PlanoTrocado, usuarioId, ct);
                if (ciclo.Id != atual?.Id)
                    await cx.ExecuteAsync(new CommandDefinition("UPDATE assinatura_ciclo SET status = 'cancelado', updated_at = NOW() WHERE id = @Id",
                        new { ciclo.Id }, tx, cancellationToken: ct));
            }
            if (atual != null) await EncerrarCicloAgoraAsync(cx, tx, atual.Id, ct);
            await CancelarTentativasDeUpgradeAsync(cx, tx, a.Id, MotivosCancelamentoCobranca.Substituida, usuarioId, ct);
            await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE assinatura SET plano_chave = @Plano, periodicidade = @Per, preco = @Preco, renovacao_automatica = FALSE,
       mudanca_plano = NULL, mudanca_periodicidade = NULL, mudanca_preco = NULL, mudanca_pedida_em = NULL, versao = versao + 1, updated_at = NOW() WHERE id = @A",
                new { A = a.Id, Plano = plano, Per = periodicidade, Preco = preco }, tx, cancellationToken: ct));
            var de = a.PlanoChave;
            a.PlanoChave = plano;
            a.Periodicidade = periodicidade;
            a.Preco = preco;
            await IniciarCicloAsync(cx, tx, a, await NomeDoPlanoAsync(cx, tx, plano, ct), await ProximoNumeroAsync(cx, tx, a.Id, ct), FusoComercial.Data(agora), ct);
            await AssinaturaSql.AplicarPlanoAsync(cx, tx, a.IdEstabelecimento, plano, "usuario", usuarioId, ct);
            await PagamentoAuditoria.RegistrarAsync(cx, tx, a.IdEstabelecimento, "assinatura", a.Id.ToString(), "plano_alterado", "usuario", usuarioId,
                new { de, para = plano, periodicidade, preco, quando = "agora" }, ct);
        }

        /// <summary>A Gestão parou a cobrança (bloqueio, saída): nada novo é gerado e o que já venceu continua devido.</summary>
        public static async Task PararCobrancaAsync(NpgsqlConnection cx, NpgsqlTransaction tx, Guid loja, int? usuarioId, string? motivo, CancellationToken ct)
        {
            if (!await TemAssinaturasAsync(cx, tx, ct)) return;
            var a = await AssinaturaSql.VivaAsync(cx, tx, loja, true, ct);
            if (a == null) return;
            await EncerrarAgoraAsync(cx, tx, a, Encerramentos.CobrancaParada, motivo, usuarioId, DateTimeOffset.UtcNow, ct);
            await PagamentoAuditoria.RegistrarAsync(cx, tx, loja, "assinatura", a.Id.ToString(), "cobranca_parada", "usuario", usuarioId, new { motivo }, ct);
        }

        // ---------- limpeza no Mercado Pago (fora de transação) ----------

        /// <summary>
        /// Upgrade e extra não pagos somem quando o prazo (o Pix de 1 hora) acaba: sem cobrança, sem dívida, sem mudar vencimento.
        /// Cartão em análise espera a resposta. Cobranças antigas sem prazo não são tocadas.
        /// </summary>
        public static async Task ExpirarTentativasAsync(NpgsqlDataSource db, IPixChargeService pix, ILogger logger, Guid? loja, DateTimeOffset agora, CancellationToken ct)
        {
            await using var cx = await db.OpenConnectionAsync(ct);
            var vencidas = (await cx.QueryAsync<(Guid Id, Guid Loja, DateTimeOffset Expira)>(new CommandDefinition(@"
SELECT c.id, c.id_estabelecimento, c.expira_em FROM comercial_cobranca c
 WHERE c.tipo IN ('upgrade', 'compra_extra') AND c.status IN ('aberta', 'processando') AND c.expira_em <= @Agora
   AND (@Loja::uuid IS NULL OR c.id_estabelecimento = @Loja)
   AND NOT EXISTS (SELECT 1 FROM pagamento_intent i WHERE i.origem_ref_id = c.id AND i.status = 'processando')",
                new { Agora = agora, Loja = loja }, cancellationToken: ct))).ToList();
            foreach (var v in vencidas)
            {
                // Se o MP não responde, tenta de novo depois; passado um dia, cancela assim mesmo (um pagamento tardio vira excedente).
                var cancelou = await CancelarPagamentosDaCobrancaAsync(cx, pix, logger, v.Id, v.Loja, ct);
                if (!cancelou && agora - v.Expira < TimeSpan.FromDays(1)) continue;
                await using var tx = await cx.BeginTransactionAsync(ct);
                var c = await AssinaturaSql.CobrancaAsync(cx, tx, v.Id, true, ct);
                if (c == null || !StatusCobrancaComercial.EmAberto(c.Status) || c.ExpiraEm > agora) continue;
                await CancelarCobrancaAsync(cx, tx, c, MotivosCancelamentoCobranca.Expirada, null, ct);
                await PagamentoAuditoria.RegistrarAsync(cx, tx, v.Loja, "comercial_cobranca", v.Id.ToString(), "expirada", "sistema", null, new { c.Tipo, c.Valor }, ct);
                await tx.CommitAsync(ct);
            }
        }

        /// <summary>Pix ainda aberto de uma cobrança cancelada: cancelado no MP. Se já foi pago, o efeito registra como excedente.</summary>
        public static async Task CancelarPagamentosDeCanceladasAsync(NpgsqlDataSource db, IPixChargeService pix, ILogger logger, Guid? loja, CancellationToken ct)
        {
            await using var cx = await db.OpenConnectionAsync(ct);
            var abertas = (await cx.QueryAsync<(Guid Intent, Guid Loja)>(new CommandDefinition(@"
SELECT i.id, i.id_estabelecimento FROM pagamento_intent i JOIN comercial_cobranca c ON c.id = i.origem_ref_id
 WHERE i.status IN ('criado', 'aguardando_pagamento') AND c.status = 'cancelada' AND (@Loja::uuid IS NULL OR c.id_estabelecimento = @Loja)",
                new { Loja = loja }, cancellationToken: ct))).ToList();
            foreach (var i in abertas) await CancelarIntentAsync(pix, logger, i.Intent, i.Loja, ct);
        }

        public static async Task<bool> CancelarPagamentosDaCobrancaAsync(NpgsqlConnection cx, IPixChargeService pix, ILogger logger, Guid cobranca, Guid loja,
            CancellationToken ct)
        {
            var intents = (await cx.QueryAsync<Guid>(new CommandDefinition(
                "SELECT id FROM pagamento_intent WHERE origem_ref_id = @C AND status IN ('criado', 'aguardando_pagamento')", new { C = cobranca }, cancellationToken: ct))).ToList();
            var todos = true;
            foreach (var i in intents) todos &= await CancelarIntentAsync(pix, logger, i, loja, ct);
            return todos;
        }

        private static async Task<bool> CancelarIntentAsync(IPixChargeService pix, ILogger logger, Guid intent, Guid loja, CancellationToken ct)
        {
            try
            {
                await pix.CancelarAsync(intent, loja, "sistema", null, ct);
                return true;
            }
            catch (Exception ex) when (ex is PagamentoException or MercadoPagoApiException)
            {
                logger.LogWarning("[assinatura] ev=cancelar_pix_falhou intent={Intent} erro={Erro}", intent, ex.Message);
                return false;
            }
        }
    }
}
