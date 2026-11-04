using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace APIBack.Acompanhamento.Relacionamento
{
    /// <summary>Regras puras do relacionamento: normalizacao, similaridade, datas e politicas de saida. Sem banco, sem relogio proprio.</summary>
    public static class RelacionamentoRegras
    {
        public const int VisaoAnteriorDias = 7;
        public const int PausaMaximaDias = 90;
        public const int AvisoSaidaDias = 30;
        public const int CobrancaIntervaloHoras = 24;
        public const int LoteMaximo = 100;
        public const int MotivoMinimo = 5;
        public const double LimiarParecido = 0.6;

        public const int CategoriaMaxima = 80;
        public const int AcaoEsperadaMaxima = 300;

        private static readonly JsonSerializerOptions JsonDetalhe = new(JsonSerializerDefaults.Web);

        private static readonly Regex NaoAlfanumerico = new("[^a-z0-9]+", RegexOptions.Compiled);
        private static readonly Regex E164 = new(@"^\+[1-9][0-9]{7,14}$", RegexOptions.Compiled);

        /// <summary>Minusculo, sem acento e sem pontuacao. Espelha o translate() da migration (nome_normalizado).</summary>
        public static string NormalizarNome(string? nome)
        {
            if (string.IsNullOrWhiteSpace(nome)) return string.Empty;
            var decomposto = nome.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(decomposto.Length);
            foreach (var c in decomposto)
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
            return NaoAlfanumerico.Replace(sb.ToString().Normalize(NormalizationForm.FormC), " ").Trim();
        }

        /// <summary>E.164 estrito: exige o "+" e o codigo do pais. Aceita espacos, parenteses, pontos e hifens; nao adivinha pais.</summary>
        public static string? TelefoneE164(string? valor)
        {
            if (string.IsNullOrWhiteSpace(valor)) return null;
            var limpo = Regex.Replace(valor.Trim(), @"[\s().\-]", string.Empty);
            return E164.IsMatch(limpo) ? limpo : null;
        }

        public static string Digitos(string? valor) => new((valor ?? string.Empty).Where(char.IsDigit).ToArray());

        public static bool MesmoTelefone(string? e164, string? outro)
        {
            var a = Digitos(e164);
            var b = Digitos(outro);
            if (a.Length < 10 || b.Length < 10) return false;
            return a[^10..] == b[^10..];
        }

        /// <summary>0..1. 1 = nomes iguais; contencao de um no outro vale pelo menos 0.8; senao, Jaccard das palavras.</summary>
        public static double Similaridade(string? a, string? b)
        {
            var na = NormalizarNome(a);
            var nb = NormalizarNome(b);
            if (na.Length == 0 || nb.Length == 0) return 0;
            if (na == nb) return 1;
            var ta = na.Split(' ').ToHashSet();
            var tb = nb.Split(' ').ToHashSet();
            var jaccard = (double)ta.Intersect(tb).Count() / ta.Union(tb).Count();
            var contem = (na.Length >= 4 && nb.Contains(na, StringComparison.Ordinal)) || (nb.Length >= 4 && na.Contains(nb, StringComparison.Ordinal));
            return contem ? Math.Max(jaccard, 0.8) : jaccard;
        }

        /// <summary>Dia civil no Brasil (UTC-3, sem horario de verao).</summary>
        public static DateOnly Hoje(DateTimeOffset agora) => DateOnly.FromDateTime(agora.ToOffset(TimeSpan.FromHours(-3)).DateTime);

        public static void ExigirMotivo(string? motivo, string codigo = "MOTIVO_OBRIGATORIO")
        {
            if (string.IsNullOrWhiteSpace(motivo) || motivo.Trim().Length < MotivoMinimo)
                throw new RelacionamentoException(422, codigo, $"Explique o motivo (minimo {MotivoMinimo} caracteres).");
        }

        /// <summary>Detalhe de evento sempre em camelCase: e o que as telas leem.</summary>
        public static string SerializarDetalhe(object detalhe) => JsonSerializer.Serialize(detalhe, JsonDetalhe);

        /// <summary>
        /// Detalhe gravado para a tela. Eventos antigos sairam em PascalCase em parte (Resultado, Tipo): as chaves de primeiro nivel
        /// sao lidas em camelCase, sem reescrever o que esta gravado.
        /// </summary>
        public static JsonElement? DetalheEmCamelCase(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return doc.RootElement.Clone();

            var saida = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                var nome = JsonNamingPolicy.CamelCase.ConvertName(p.Name);
                // Se a chave ja existe em camelCase, ela vale: e a escrita mais nova.
                if (nome == p.Name || !saida.ContainsKey(nome)) saida[nome] = p.Value.Clone();
            }
            return JsonSerializer.SerializeToElement(saida);
        }

        /// <summary>Nomes antigos dos eventos (a tela sempre esperou estes). "cobrou_responsavel" fica: a Equipe confere a cobranca por ele.</summary>
        public static string TipoEvento(string tipo) => tipo switch
        {
            "marcou" => "marcada",
            "desmarcou" => "desmarcada",
            _ => tipo
        };

        public static void ValidarProximoPasso(string? tipo, DateOnly? data, DateOnly hoje)
        {
            if (string.IsNullOrWhiteSpace(tipo) || !data.HasValue)
                throw new RelacionamentoException(422, "PROXIMO_PASSO_OBRIGATORIO", "Todo lead e toda loja precisam de um proximo passo com tipo e data.",
                    new { tipos = TiposProximoPasso.Todos });
            if (!TiposProximoPasso.Todos.Contains(tipo.Trim().ToLowerInvariant()))
                throw new RelacionamentoException(422, "PROXIMO_PASSO_TIPO_INVALIDO", "Tipo de proximo passo invalido.", new { tipos = TiposProximoPasso.Todos });
            if (data.Value < hoje)
                throw new RelacionamentoException(422, "PROXIMO_PASSO_NO_PASSADO", "A data do proximo passo nao pode estar no passado.");
        }

        /// <summary>Aguardando = ultima mensagem efetivamente enviada e nenhum recebimento depois dela.</summary>
        public static EsperaRespostaDto CalcularEspera(DateTimeOffset? ultimaEnviada, DateTimeOffset? ultimaRecebida)
        {
            var aguardando = ultimaEnviada.HasValue && (!ultimaRecebida.HasValue || ultimaRecebida.Value < ultimaEnviada.Value);
            return new EsperaRespostaDto(aguardando, aguardando ? ultimaEnviada : null, ultimaEnviada, ultimaRecebida);
        }

        /// <summary>
        /// Data de saida pela politica escolhida. agora = hoje; aviso_30_dias = pedido + 30 dias; fim_do_ciclo depende do contrato
        /// (que o sistema nao calcula aqui): a data vem do operador e nao pode ser anterior ao pedido.
        /// </summary>
        public static DateOnly CalcularSaida(string? quando, DateOnly pedido, DateOnly? saidaEm, DateOnly hoje)
        {
            switch (quando)
            {
                case SaidasQuando.Agora:
                    return hoje;
                case SaidasQuando.Aviso30Dias:
                    var data = pedido.AddDays(AvisoSaidaDias);
                    if (saidaEm.HasValue && saidaEm.Value < data)
                        throw new RelacionamentoException(422, "SAIDA_ANTES_DO_AVISO", $"Com aviso de {AvisoSaidaDias} dias a saida nao pode ser antes de {data:dd/MM/yyyy}.");
                    return saidaEm ?? data;
                case SaidasQuando.FimDoCiclo:
                    if (!saidaEm.HasValue)
                        throw new RelacionamentoException(422, "SAIDA_SEM_DATA", "Informe a data do fim do ciclo (saidaEm).");
                    if (saidaEm.Value < pedido)
                        throw new RelacionamentoException(422, "SAIDA_ANTES_DO_PEDIDO", "A saida nao pode ser antes do pedido.");
                    return saidaEm.Value;
                default:
                    throw new RelacionamentoException(422, "SAIDA_QUANDO_INVALIDO", "Informe saidaQuando: aviso_30_dias, fim_do_ciclo ou agora.");
            }
        }

        public static void ValidarPausa(DateOnly? pausaAte, DateOnly hoje)
        {
            if (!pausaAte.HasValue) throw new RelacionamentoException(422, "PAUSA_SEM_DATA", "Informe ate quando a loja fica em pausa (pausaAte).");
            if (pausaAte.Value <= hoje) throw new RelacionamentoException(422, "PAUSA_NO_PASSADO", "A pausa precisa terminar depois de hoje.");
            if (pausaAte.Value > hoje.AddDays(PausaMaximaDias))
                throw new RelacionamentoException(422, "PAUSA_ACIMA_DA_POLITICA", $"A pausa nao pode passar de {PausaMaximaDias} dias.");
        }
    }
}
