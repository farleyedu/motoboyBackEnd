using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using APIBack.Acompanhamento.Fases;
using APIBack.Payments.Assinaturas;
using APIBack.Service;
using F = APIBack.Acompanhamento.Fases.FasesAcompanhamento;
using Fx = APIBack.Acompanhamento.Fases.FaixasAcompanhamento;

namespace APIBack.Acompanhamento.Saude
{
    /// <summary>
    /// Calculo unico da saude (versionado). Funcao pura: o Quadro, a fila Hoje e o worker chamam esta mesma. O pior sinal decide a faixa;
    /// cada motivo diz o que foi visto, de onde veio e o que faz sair da faixa. Dias sao de calendario em America/Sao_Paulo.
    /// O fim do teste nunca corta acesso: sem pagamento a loja continua operando e fica em Precisa de ajuda, com contador.
    /// </summary>
    public static class SaudeCalculador
    {
        /// <summary>Muda quando uma regra do calculo muda (nao os limites, que tem versao propria).</summary>
        public const int Versao = 1;

        private static readonly CultureInfo Br = new("pt-BR");

        public static SaudeAvaliacao Avaliar(SaudeEntrada e, LimitesSaude l, DateTimeOffset agora)
        {
            var hoje = FusoComercial.Data(agora);
            var fase = e.FaseEfetiva;
            var indisponiveis = new List<string>();

            if (e.Situacao == SituacoesComerciais.Bloqueado || fase is F.Perdido or F.Encerrado)
                return Montar(l, new List<MotivoSaude>(), neutra: true, "fora do acompanhamento ativo", false, indisponiveis);

            var motivos = new List<MotivoSaude>();
            var pausa = fase == F.Pausa || (e.PausaAte is { } ate && ate >= hoje);
            var situacao = e.Situacao;
            var inicioOperacao = e.OperacaoIniciadaEm ?? e.CriadaEm;
            var escalar = false;

            // ---- pagamento (fonte: assinatura e cobranca da assinatura)
            if (e.ValorAtrasado > 0)
            {
                var atraso = e.AtrasadoDesde is { } desde ? hoje.DayNumber - desde.DayNumber : (int?)null;
                motivos.Add(new MotivoSaude("pagamento_atrasado", Fx.PrecisaDeAjuda, "pagamento",
                    $"{e.ValorAtrasado.ToString("C", Br)} atrasado{(atraso > 0 ? $" há {Dias(atraso.Value)}" : "")}",
                    "cobranca da assinatura", "a cobranca atrasada ser paga", atraso));
            }
            else if (e.ProximoVencimento is { } venc)
            {
                var faltam = venc.DayNumber - hoje.DayNumber;
                if (faltam >= 0 && faltam <= l.VencimentoDias)
                    motivos.Add(new MotivoSaude("vencimento_proximo", Fx.Atencao, "pagamento",
                        faltam == 0 ? "cobrança vence hoje" : $"cobrança vence em {Dias(faltam)} ({venc:dd/MM})",
                        "cobranca da assinatura", "a cobranca ser paga", faltam));
            }

            // ---- fim do teste (nunca suspende; so informa e escala)
            if (situacao == SituacoesComerciais.Teste)
            {
                if (e.TesteTerminaEm is not { } fimTeste) indisponiveis.Add("teste_termina_em");
                else
                {
                    var faltam = FusoComercial.Data(fimTeste).DayNumber - hoje.DayNumber;
                    var pagou = e.PlanoContratado != null && e.PagamentoConfirmado;
                    if (!pagou)
                    {
                        if (faltam < 0)
                        {
                            var n = -faltam;
                            motivos.Add(new MotivoSaude("teste_sem_pagamento", Fx.PrecisaDeAjuda, "situacao",
                                $"operando há {Dias(n)} depois do fim do teste, sem pagamento",
                                "teste e assinatura", "contratar e pagar um plano, ou a equipe decidir (prorrogar, pausar ou encerrar)", n));
                            if (l.EscalonarSemPagamentoDias is { } limite && n >= limite)
                            {
                                escalar = true;
                                motivos.Add(new MotivoSaude("escalonar_sem_pagamento", Fx.PrecisaDeAjuda, "situacao",
                                    $"{Dias(n)} sem pagamento: escalar ao gestor (limite {limite})",
                                    "regra de escalonamento", "a decisao humana sobre a loja", n));
                            }
                        }
                        else if (faltam <= l.TesteAjudaAteDias)
                            motivos.Add(new MotivoSaude("teste_fim_proximo", Fx.PrecisaDeAjuda, "situacao", FimDoTeste(faltam),
                                "teste e assinatura", "pagar um plano ou a equipe prorrogar o teste", faltam));
                        else if (faltam <= l.TesteAtencaoAteDias)
                            motivos.Add(new MotivoSaude("teste_fim_proximo", Fx.Atencao, "situacao", FimDoTeste(faltam),
                                "teste e assinatura", "pagar um plano ou a equipe prorrogar o teste", faltam));
                    }
                    else if (faltam < 0)
                    {
                        motivos.Add(new MotivoSaude("ativacao_pendente", Fx.Atencao, "situacao",
                            "plano pago, mas a ativação depois do teste ainda não aconteceu",
                            "assinatura", "o ciclo pago comecar (o plano entra sozinho)", -faltam));
                    }
                }
            }

            // ---- pendencia de resposta (chamado esperando a Zippy)
            if (e.ChamadoPendenteDesde is { } pendente)
            {
                var dias = hoje.DayNumber - FusoComercial.Data(pendente).DayNumber;
                if (e.ChamadoPrazoVencido)
                    motivos.Add(new MotivoSaude("chamado_fora_do_prazo", Fx.PrecisaDeAjuda, "atendimento",
                        $"chamado fora do prazo, esperando resposta há {Dias(Math.Max(dias, 0))}", "chamados", "responder o chamado", dias));
                else if (dias >= l.RespostaPendenteDias)
                    motivos.Add(new MotivoSaude("chamado_sem_resposta", Fx.Atencao, "atendimento",
                        $"chamado esperando resposta há {Dias(dias)}", "chamados", "responder o chamado", dias));
            }

            // ---- uso (historico da propria loja). Pausa declarada elimina o falso positivo por inatividade.
            var emOperacao = situacao is SituacoesComerciais.Teste or SituacoesComerciais.Ativo;
            var semPedidosAchado = false;
            if (emOperacao && !pausa)
            {
                if (inicioOperacao == null) indisponiveis.Add("inicio_da_operacao");
                else
                {
                    var referencia = FusoComercial.Data(e.UltimoPedidoEm ?? inicioOperacao.Value);
                    var semPedido = hoje.DayNumber - referencia.DayNumber;
                    var diasDeOperacao = hoje.DayNumber - FusoComercial.Data(inicioOperacao.Value).DayNumber;
                    if (semPedido >= l.DiasSemPedido && diasDeOperacao >= l.DiasSemPedido)
                    {
                        semPedidosAchado = true;
                        motivos.Add(new MotivoSaude("sem_pedidos", Fx.PrecisaDeAjuda, "uso",
                            e.UltimoPedidoEm == null ? $"nenhum pedido em {Dias(semPedido)}" : $"sem pedido há {Dias(semPedido)}",
                            "pedidos", "a loja registrar um pedido, ou declarar uma pausa", semPedido));
                    }
                    else
                    {
                        var media = Math.Max(0, e.Pedidos28d - e.Pedidos7d) / 3m;
                        if (media >= l.QuedaBaseSemanalMinima && media > 0 && (media - e.Pedidos7d) * 100 >= media * l.QuedaPercentual)
                        {
                            var queda = (int)Math.Round((media - e.Pedidos7d) / media * 100, MidpointRounding.AwayFromZero);
                            motivos.Add(new MotivoSaude("queda_de_pedidos", Fx.Atencao, "uso",
                                $"pedidos caíram {queda}% ({Math.Round(media):0} → {e.Pedidos7d} na semana)",
                                "pedidos", "o ritmo voltar a menos de " + l.QuedaPercentual + "% abaixo da propria media, ou declarar uma pausa", queda));
                        }
                    }

                    if (situacao == SituacoesComerciais.Teste && !semPedidosAchado && diasDeOperacao >= l.TesteUsoAposDias && e.Pedidos28d < l.TesteUsoMinimoPedidos)
                        motivos.Add(new MotivoSaude("teste_uso_insuficiente", Fx.Atencao, "uso",
                            $"{Pl(e.Pedidos28d, "pedido", "pedidos")} em {Dias(diasDeOperacao)} de teste (esperado: {l.TesteUsoMinimoPedidos})",
                            "pedidos", $"chegar a {l.TesteUsoMinimoPedidos} pedidos no teste", e.Pedidos28d));
                }
            }

            // ---- passos da equipe (so onde a equipe deve algo: conquista, implantacao, teste, saida)
            if (!pausa && fase is F.Conversa or F.Proposta or F.Implantacao or F.Teste or F.Saindo)
            {
                if (e.AcompanhamentoId == null) indisponiveis.Add("acompanhamento");
                else
                {
                    var ultimo = e.UltimoPassoEm ?? e.FaseDesde;
                    if (ultimo is { } quando)
                    {
                        var dias = hoje.DayNumber - FusoComercial.Data(quando).DayNumber;
                        if (dias >= l.DiasSemPasso)
                            motivos.Add(new MotivoSaude("sem_passo", Fx.Atencao, "acompanhamento",
                                $"sem nenhum passo da equipe há {Dias(dias)}", "passagens, notas e historico", "registrar um passo (passagem, nota ou conversa)", dias));
                    }
                    else indisponiveis.Add("ultimo_passo");
                }
            }

            if (fase == F.Implantacao && e.FaseDesde is { } entrou)
            {
                var dias = hoje.DayNumber - FusoComercial.Data(entrou).DayNumber;
                if (dias >= l.ImplantacaoTravadaDias)
                    motivos.Add(new MotivoSaude("implantacao_travada", Fx.Atencao, "acompanhamento",
                        $"em implantação há {Dias(dias)}", "fase do acompanhamento", "a loja comecar a operar ou a equipe mudar a fase", dias));
            }

            if (fase == F.Proposta)
            {
                if (e.PropostaValidadeAte is not { } validade) indisponiveis.Add("validade_da_proposta");
                else
                {
                    var faltam = validade.DayNumber - hoje.DayNumber;
                    if (faltam < 0)
                        motivos.Add(new MotivoSaude("proposta_vencida", Fx.PrecisaDeAjuda, "acompanhamento",
                            $"proposta venceu há {Dias(-faltam)}", "versao da proposta", "enviar nova versao ou perder a oportunidade", -faltam));
                    else if (faltam <= l.PropostaVenceEmDias)
                        motivos.Add(new MotivoSaude("proposta_vencendo", Fx.Atencao, "acompanhamento",
                            faltam == 0 ? "proposta vence hoje" : $"proposta vence em {Dias(faltam)}", "versao da proposta", "obter a resposta ou renovar a validade", faltam));
                }
            }

            var neutra = motivos.Count == 0 && situacao == SituacoesComerciais.Implantacao;
            return Montar(l, motivos, neutra, neutra ? "ainda em implantação" : "tudo certo", escalar, indisponiveis);
        }

        private static SaudeAvaliacao Montar(LimitesSaude l, List<MotivoSaude> motivos, bool neutra, string vazio, bool escalar, List<string> indisponiveis)
        {
            // Ordem estavel por gravidade: o primeiro motivo da pior faixa (na ordem em que as regras rodam) e o principal.
            var ordenados = motivos.Select((m, i) => (m, i)).OrderByDescending(x => Peso(x.m.Faixa)).ThenBy(x => x.i).Select(x => x.m).ToList();
            var principal = ordenados.FirstOrDefault();
            return new SaudeAvaliacao(Versao, l.Versao, principal?.Faixa ?? Fx.Bem, neutra && principal == null, principal?.Texto ?? vazio,
                principal, ordenados, escalar, indisponiveis);
        }

        public static int Peso(string faixa) => faixa switch { Fx.PrecisaDeAjuda => 2, Fx.Atencao => 1, _ => 0 };

        private static string FimDoTeste(int faltam) => faltam == 0 ? "teste acaba hoje" : $"teste acaba em {Dias(faltam)}";
        private static string Dias(int n) => Pl(n, "dia", "dias");
        private static string Pl(int n, string um, string varios) => $"{n} {(n == 1 ? um : varios)}";
    }
}
