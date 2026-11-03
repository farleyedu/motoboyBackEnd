using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace APIBack.AssistenteComercial.Inteligencia
{
    /// <summary>
    /// Treinador por responsavel. So apresenta contagens reais com amostra e exemplos. Abaixo de <see cref="InteligenciaOptions.AmostraMinima"/> o padrao
    /// vem como "sem evidencia suficiente", sem percentual e sem exercicio. Nao compara pessoas, nao pontua e nao atribui causa.
    /// </summary>
    public static class CoachingMotor
    {
        public const string Aviso = "Padroes descrevem o que foi registrado, nao causas. Percentuais so aparecem quando a amostra atinge o minimo.";

        public static CoachingDto Montar(int responsavelUsuarioId, IReadOnlyList<ObjecaoDoResponsavel> objecoes, IReadOnlyList<RecomendacaoDoResponsavel> recomendacoes,
            InteligenciaOptions o, DateTime agora)
        {
            var padroes = new List<PadraoCoaching>();
            var declaradas = objecoes.Where(x => x.Certeza == CertezaObjecao.Declarada).ToList();

            if (declaradas.Count > 0)
            {
                var top = declaradas.GroupBy(x => x.Tipo).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).First();
                padroes.Add(Padrao("tipo_mais_frequente", "Objecao mais registrada", top.Count(), declaradas.Count, o, Exemplos(top, o),
                    (n, m) => $"{n} de {m} objecoes declaradas sao de {top.Key}.", null));
            }

            foreach (var g in declaradas.GroupBy(x => x.Tipo).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var resolvidas = g.Count(x => x.Estado == EstadoObjecao.Resolvida);
                var exemplos = Exemplos(g.Where(x => x.Estado == EstadoObjecao.Resolvida && !string.IsNullOrWhiteSpace(x.Abordagem)), o);
                padroes.Add(Padrao($"resolucao_{g.Key}", $"Objecoes de {g.Key} resolvidas", resolvidas, g.Count(), o, exemplos,
                    (n, m) => $"{n} de {m} objecoes de {g.Key} constam como resolvidas.",
                    $"Na proxima conversa com objecao de {g.Key}, comece pela pergunta de diagnostico: {RecomendacaoMotor.PerguntaDe(g.Key)} Depois registre a abordagem usada."));
            }

            var comPrazo = declaradas.Where(x => x.RespostaPrometidaAte.HasValue).ToList();
            if (comPrazo.Count > 0)
            {
                var vencidas = comPrazo.Where(x => x.RespostaPrometidaAte < agora && x.Estado is EstadoObjecao.Levantada or EstadoObjecao.Investigando).ToList();
                padroes.Add(Padrao("compromissos_vencidos", "Respostas prometidas vencidas e abertas", vencidas.Count, comPrazo.Count, o, Exemplos(vencidas, o),
                    (n, m) => $"{n} de {m} respostas prometidas estao vencidas sem resposta registrada.",
                    "Ao prometer uma resposta, registre o prazo na objecao e reveja a lista de vencidas no inicio de cada dia."));
            }

            if (declaradas.Count > 0)
            {
                var semImpacto = declaradas.Where(x => x.Impacto == ImpactoObjecao.Desconhecido && x.Estado != EstadoObjecao.Resolvida).ToList();
                padroes.Add(Padrao("sem_impacto_avaliado", "Objecoes abertas sem impacto avaliado", semImpacto.Count, declaradas.Count, o, Exemplos(semImpacto, o),
                    (n, m) => $"{n} de {m} objecoes declaradas seguem abertas com impacto desconhecido.",
                    "Para cada objecao aberta, pergunte ao cliente o quanto ela pesa na decisao e registre o impacto."));
            }

            var executadas = recomendacoes.Where(x => x.Estado == EstadoRecomendacao.Executada && x.Resultado != null).ToList();
            if (executadas.Count > 0)
            {
                var avancou = executadas.Count(x => x.Resultado == "avancou");
                padroes.Add(Padrao("recomendacoes_executadas", "Resultado registrado das recomendacoes executadas", avancou, executadas.Count, o,
                    executadas.Where(x => x.Resultado == "avancou").Take(o.MaxExemplos).Select(x => new ExemploCoaching(x.OportunidadeId, x.Regra, $"Recomendacao {x.Regra} executada; resultado: avancou.")).ToList(),
                    (n, m) => $"{n} de {m} recomendacoes executadas tiveram resultado 'avancou' (associacao observada, nao causa).", null));
            }

            if (padroes.Count == 0)
                padroes.Add(new PadraoCoaching("sem_dados", "Sem evidencia suficiente", "Sem evidencia suficiente: nao ha objecoes registradas no periodo.", false, 0, 0, null, Array.Empty<ExemploCoaching>(), null));

            return new CoachingDto(responsavelUsuarioId, o.JanelaCoachingDias, objecoes.Count, recomendacoes.Count, o.AmostraMinima, padroes, Aviso);
        }

        private static PadraoCoaching Padrao(string id, string titulo, int observados, int amostra, InteligenciaOptions o, IReadOnlyList<ExemploCoaching> exemplos,
            Func<int, int, string> descricao, string? exercicio)
        {
            var suficiente = amostra >= o.AmostraMinima;
            return suficiente
                ? new PadraoCoaching(id, titulo, descricao(observados, amostra), true, observados, amostra,
                    Math.Round(100.0 * observados / amostra, 1, MidpointRounding.AwayFromZero), exemplos, exercicio)
                : new PadraoCoaching(id, titulo, string.Create(CultureInfo.InvariantCulture,
                        $"Sem evidencia suficiente: {amostra} registro(s); minimo de {o.AmostraMinima} para analisar."), false, observados, amostra, null, exemplos, null);
        }

        private static IReadOnlyList<ExemploCoaching> Exemplos(IEnumerable<ObjecaoDoResponsavel> fonte, InteligenciaOptions o) =>
            fonte.OrderByDescending(x => x.OcorridaEm).Take(o.MaxExemplos).Select(x => new ExemploCoaching(x.OportunidadeId, x.Tipo, Trecho(x.Evidencia))).ToList();

        private static string Trecho(string s) => s.Length <= 160 ? s : s[..160] + "...";
    }
}
