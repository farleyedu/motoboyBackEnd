using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace APIBack.AssistenteComercial.Inteligencia
{
    public sealed record RecomendacaoCandidata(string Regra, string ImpressaoDigital, string Sugestao, string Hipotese, string Acao, IReadOnlyList<EvidenciaDto> Evidencias);

    /// <summary>
    /// Parte deterministica do motor hibrido. Ordem fixa de prioridade: 1) barreira real / capacidade do produto, 2) pergunta de diagnostico,
    /// 3) compromisso vencido, 4) abordagens comparaveis. Tudo aqui vem de fatos registrados; nao ha probabilidade, desconto nem causa inferida.
    /// </summary>
    public static class RecomendacaoMotor
    {
        private static readonly Dictionary<string, string> Perguntas = new()
        {
            [TipoObjecao.Preco] = "O que o cliente compara com o valor: outro fornecedor, o custo de hoje ou o orcamento disponivel?",
            [TipoObjecao.Concorrente] = "Qual solucao ele usa hoje, o que ele considera que ela resolve melhor e o que o faria mudar?",
            [TipoObjecao.Funcionalidade] = "Qual tarefa do dia a dia ele nao consegue fazer e com que frequencia isso acontece?",
            [TipoObjecao.Integracao] = "Com qual sistema ele precisa integrar e o que passa por essa integracao hoje?",
            [TipoObjecao.Prazo] = "Ate quando ele precisa estar funcionando e o que depende dessa data?",
            [TipoObjecao.Decisor] = "Quem decide, o que essa pessoa precisa ver e quando ela pode participar de uma conversa?",
            [TipoObjecao.Confianca] = "O que aconteceu antes (com a Zippy ou com outro fornecedor) que gerou a duvida?",
            [TipoObjecao.Timing] = "O que precisa acontecer para ele considerar o momento certo?",
            [TipoObjecao.Suporte] = "Que situacao de atendimento ele teme e como lida com ela hoje?",
            [TipoObjecao.Outro] = "Em uma frase, qual e a duvida e o que resolveria ela para ele?",
        };

        public static string PerguntaDe(string tipo) => Perguntas.TryGetValue(tipo, out var p) ? p : Perguntas[TipoObjecao.Outro];

        /// <param name="catalogoModulos">Modulos dos pacotes ativos do catalogo comercial; null = catalogo indisponivel (nao se conclui nada sobre capacidade).</param>
        public static IReadOnlyList<RecomendacaoCandidata> Gerar(IReadOnlyList<Objecao> objecoes, IReadOnlyCollection<string>? catalogoModulos,
            IReadOnlyList<ObjecaoHistorica> historico, InteligenciaOptions opcoes, DateTime agora)
        {
            var saida = new List<RecomendacaoCandidata>();
            var emBarreira = new HashSet<Guid>();

            foreach (var o in objecoes.Where(o => o.Estado != EstadoObjecao.Resolvida && o.Certeza == CertezaObjecao.Declarada))
            {
                var ev = EvidenciaObjecao(o);

                if (catalogoModulos != null && !string.IsNullOrWhiteSpace(o.ModuloCitado) && !NoCatalogo(o.ModuloCitado!, catalogoModulos))
                {
                    emBarreira.Add(o.Id);
                    saida.Add(new RecomendacaoCandidata(RegraRecomendacao.BarreiraReal, $"barreira:capacidade:{o.Id}:{Normalizar(o.ModuloCitado!)}",
                        $"\"{o.ModuloCitado}\" nao consta no catalogo comercial atual: nao prometer.",
                        $"A falta de \"{o.ModuloCitado}\" pode ser o motivo real da objecao (a confirmar com o cliente).",
                        "Registrar a lacuna, dizer ao cliente o que o catalogo cobre hoje, perguntar se ele segue sem isso e nao informar prazo nem previsao de entrega.",
                        new[] { ev, new EvidenciaDto("catalogo", "pacotes_comerciais", $"Modulos no catalogo: {(catalogoModulos.Count == 0 ? "(nenhum)" : string.Join(", ", catalogoModulos.OrderBy(m => m)))}") }));
                }
                else if (o.Estado == EstadoObjecao.Impeditiva)
                {
                    emBarreira.Add(o.Id);
                    saida.Add(new RecomendacaoCandidata(RegraRecomendacao.BarreiraReal, $"barreira:impeditiva:{o.Id}",
                        $"Objecao de {o.Tipo} esta marcada como impeditiva.",
                        "Enquanto esta objecao seguir impeditiva, avancar a proposta tem pouco efeito observavel.",
                        "Confirmar com quem decide se a objecao continua impeditiva; se sim, registrar o motivo de saida; se nao, atualizar o estado da objecao.",
                        new[] { ev }));
                }
            }

            foreach (var o in objecoes.Where(o => !emBarreira.Contains(o.Id) && (o.Estado is EstadoObjecao.Levantada or EstadoObjecao.Investigando)
                                                 && (o.Impacto == ImpactoObjecao.Desconhecido || o.Certeza == CertezaObjecao.Hipotese)))
            {
                var hip = o.Certeza == CertezaObjecao.Hipotese
                    ? $"Possivel objecao de {o.Tipo}; ainda e hipotese, sem confirmacao do cliente."
                    : $"Impacto da objecao de {o.Tipo} ainda desconhecido.";
                saida.Add(new RecomendacaoCandidata(RegraRecomendacao.Diagnostico, $"diagnostico:{o.Id}",
                    "Fazer uma pergunta de diagnostico antes de responder.", hip, PerguntaDe(o.Tipo), new[] { EvidenciaObjecao(o) }));
            }

            foreach (var o in objecoes.Where(o => o.RespostaPrometidaAte.HasValue && o.RespostaPrometidaAte.Value < agora
                                                 && o.Estado is EstadoObjecao.Levantada or EstadoObjecao.Investigando))
            {
                var dias = (int)Math.Max(1, Math.Floor((agora - o.RespostaPrometidaAte!.Value).TotalDays));
                saida.Add(new RecomendacaoCandidata(RegraRecomendacao.CompromissoVencido, $"compromisso:{o.Id}:{o.RespostaPrometidaAte.Value:yyyyMMdd}",
                    $"A resposta prometida sobre a objecao de {o.Tipo} esta vencida desde {o.RespostaPrometidaAte.Value:dd/MM/yyyy}.",
                    "Compromisso nao cumprido tende a ser lido pelo cliente como falta de retorno (a confirmar na proxima conversa).",
                    "Entrar em contato, assumir o atraso, entregar a resposta ou combinar uma nova data e registrar a objecao como respondida ou o novo prazo.",
                    new[] { EvidenciaObjecao(o), new EvidenciaDto("compromisso", o.Id.ToString(), $"Prazo prometido {o.RespostaPrometidaAte.Value:dd/MM/yyyy}; {dias} dia(s) de atraso.") }));
            }

            foreach (var porTipo in objecoes.Where(o => o.Certeza == CertezaObjecao.Declarada && o.Estado is EstadoObjecao.Levantada or EstadoObjecao.Investigando).GroupBy(o => o.Tipo))
            {
                var casos = historico.Where(h => h.Tipo == porTipo.Key && !string.IsNullOrWhiteSpace(h.Abordagem)).ToList();
                var distintas = casos.Select(c => c.OportunidadeId).Distinct().Count();
                if (distintas < opcoes.AmostraMinima) continue; // sem evidencia suficiente: nao se sugere abordagem

                var exemplos = casos.OrderByDescending(c => c.AtualizadaEm).GroupBy(c => c.OportunidadeId).Select(g => g.First()).Take(opcoes.MaxExemplos).ToList();
                var evid = exemplos.Select(e => new EvidenciaDto("caso_comparavel", e.ObjecaoId.ToString(), $"Abordagem registrada: {e.Abordagem}")).ToList();
                evid.Add(new EvidenciaDto("amostra", porTipo.Key, $"{distintas} oportunidade(s) com objecao de {porTipo.Key} resolvida e abordagem registrada."));
                saida.Add(new RecomendacaoCandidata(RegraRecomendacao.AbordagemComparavel, $"comparavel:{porTipo.Key}:{porTipo.First().OportunidadeId}",
                    $"Ha {distintas} caso(s) resolvidos de objecao de {porTipo.Key} com abordagem registrada para consultar.",
                    "Os casos mostram associacao entre a abordagem e a resolucao; nao provam que a abordagem causou o resultado.",
                    "Ler as abordagens dos casos abaixo e escolher a que se aplica a esta conversa, adaptando ao que o cliente disse.", evid));
            }

            return saida.GroupBy(c => c.ImpressaoDigital).Select(g => g.First()).OrderBy(c => RegraRecomendacao.Prioridade(c.Regra)).ToList();
        }

        public static EvidenciaDto EvidenciaObjecao(Objecao o) =>
            new("objecao", o.Id.ToString(), $"[{o.Origem}/{o.Certeza}] {o.Evidencia}");

        /// <summary>Casamento tolerante (um contem o outro, sem acento/caixa/separador): na duvida o modulo conta como existente, para nunca afirmar falta que nao e certa.</summary>
        public static bool NoCatalogo(string modulo, IEnumerable<string> catalogo)
        {
            var m = Normalizar(modulo);
            var tm = Tokens(m);
            return catalogo.Select(Normalizar).Any(c =>
            {
                if (c.Length == 0 || m.Length == 0) return false;
                if (c == m || c.Contains(m) || m.Contains(c)) return true;
                var tc = Tokens(c);
                return tc.Count > 0 && tm.Count > 0 && (tc.IsSubsetOf(tm) || tm.IsSubsetOf(tc));
            });
        }

        private static readonly HashSet<string> Ligacoes = new() { "de", "da", "do", "das", "dos", "e", "a", "o", "para", "com" };

        private static HashSet<string> Tokens(string normalizado) =>
            normalizado.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => !Ligacoes.Contains(t)).ToHashSet();

        public static string Normalizar(string s)
        {
            var d = s.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder();
            foreach (var c in d)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
                sb.Append(c is '_' or '-' ? ' ' : c);
            }
            return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
        }
    }

    // ---- Redacao por IA: so interpreta/redige, e o resultado so passa pela guarda ----

    public sealed record FatoParaRedacao(string Id, string Texto);

    /// <summary>Saida estruturada da IA: texto, quais fatos (ids) usou e quais modulos do catalogo citou.</summary>
    public sealed record RedacaoIa(string Texto, IReadOnlyList<string> FatosUsados, IReadOnlyList<string> ModulosCitados);

    public interface IRedatorRecomendacao
    {
        /// <summary>null = sem redacao (IA desligada ou indisponivel); a recomendacao segue com o texto deterministico.</summary>
        Task<RedacaoIa?> RedigirAsync(RecomendacaoCandidata candidata, IReadOnlyList<FatoParaRedacao> fatos, IReadOnlyCollection<string> catalogoModulos, CancellationToken ct);
    }

    /// <summary>Padrao: nenhum fornecedor de IA configurado. A IA fica desligada ate existir um redator real.</summary>
    public sealed class RedatorDesligado : IRedatorRecomendacao
    {
        public Task<RedacaoIa?> RedigirAsync(RecomendacaoCandidata candidata, IReadOnlyList<FatoParaRedacao> fatos, IReadOnlyCollection<string> catalogoModulos, CancellationToken ct) =>
            Task.FromResult<RedacaoIa?>(null);
    }

    /// <summary>Rejeita texto de IA que invente probabilidade, desconto, causa, funcionalidade fora do catalogo, numero fora dos fatos ou fato sem referencia.</summary>
    public static class GuardaRedacao
    {
        private const int MaxCaracteres = 600;

        private static readonly Regex Proibidas = new(
            @"\b(desconto|gratis|gratuit\w*|promocao|cortesia|isencao|probabilidade|chance|garant\w*|certeza|vai fechar|com certeza|por causa d\w*|devido a\w*|causou|provoc\w+|culpa)\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex Numeros = new(@"\d+(?:[.,]\d+)*", RegexOptions.Compiled);

        public static bool Valida(RedacaoIa? r, IReadOnlyList<FatoParaRedacao> fatos, IReadOnlyCollection<string> catalogoModulos, out string motivo)
        {
            motivo = string.Empty;
            if (r == null || string.IsNullOrWhiteSpace(r.Texto)) { motivo = "texto vazio"; return false; }
            if (r.Texto.Length > MaxCaracteres) { motivo = "texto longo demais"; return false; }
            if (r.Texto.Contains('%')) { motivo = "percentual nao permitido"; return false; }
            var norm = RecomendacaoMotor.Normalizar(r.Texto);
            if (Proibidas.IsMatch(norm)) { motivo = "promessa, desconto, probabilidade ou causalidade nao permitidos"; return false; }

            if (r.FatosUsados == null || r.FatosUsados.Count == 0) { motivo = "sem referencia a fatos"; return false; }
            var ids = fatos.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
            if (r.FatosUsados.Any(f => !ids.Contains(f))) { motivo = "referencia a fato inexistente"; return false; }

            if ((r.ModulosCitados ?? Array.Empty<string>()).Any(m => !RecomendacaoMotor.NoCatalogo(m, catalogoModulos))) { motivo = "modulo fora do catalogo"; return false; }

            var permitidos = fatos.SelectMany(f => Numeros.Matches(f.Texto).Select(m => m.Value)).ToHashSet();
            if (Numeros.Matches(r.Texto).Any(m => !permitidos.Contains(m.Value))) { motivo = "numero que nao esta nos fatos"; return false; }
            return true;
        }
    }
}
