using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace APIBack.AssistenteComercial.Audio
{
    /// <summary>Oportunidade que o usuario pode acessar, no minimo necessario para o match.</summary>
    public sealed record OportunidadeParaMatch(Guid Id, string Empresa, string? Contato, string? Telefone, string? Cnpj, string Historico, string? ProximaAcao);

    /// <summary>
    /// Regras puras do assistente: o servidor decide o que vale. A IA so sugere texto; qualquer item sem trecho achado na transcricao e
    /// descartado, as alteracoes sao montadas aqui (nunca copiadas da IA) e so existem dois tipos, nota no historico e proxima acao.
    /// </summary>
    public static class AudioRegras
    {
        public const int MaxTexto = 500;
        public const int MaxItens = 10;
        public const int MaxNota = 1500;

        private static readonly Regex Instrucao = new(
            @"(ignor[ea]\w*\s+(as\s+|todas\s+as\s+)?(instru|regra|ordem)|esque[cç]a\s+(as\s+|tudo)|desconsidere|"
            + @"voce\s+(agora\s+)?(e|eh|sera)\s+(um|o|a)|you\s+are\s+now|ignore\s+(all|previous)|system\s*prompt|"
            + @"novas?\s+instru|a\s+partir\s+de\s+agora\s+(voce|aplique|ative)|aplique\s+automaticamente|sem\s+(pedir\s+)?confirma)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(250));

        private static readonly (string Tipo, Regex Padrao)[] Proibidas =
        {
            ("ativar_plano", new Regex(@"\b(ativ\w+|libera\w*|habilit\w+)\b.{0,30}\b(plano|assinatura|conta|contrato)\b", Opcoes)),
            ("conceder_desconto", new Regex(@"\b(d[aeo]\w*|conced\w+|aplic\w+|fa[cz]\w*)\b.{0,25}\b(desconto|abatimento|gratuidade|isencao)\b|\bdesconto\s+de\b", Opcoes)),
            ("pagamento", new Regex(@"\b(pag[ao]\w*|pix|transfer\w+|baix\w+|estorn\w+|reembols\w+)\b.{0,30}\b(fatura|boleto|mensalidade|valor|cobranca|lancamento|pagamento)\b|\bregistr\w+\s+pagamento\b", Opcoes)),
            ("cancelamento", new Regex(@"\bcancel\w+\b.{0,30}\b(plano|assinatura|contrato|conta|cliente|negocio|loja)\b", Opcoes)),
            ("exclusao", new Regex(@"\b(exclu\w+|apag\w+|delet\w+|remov\w+)\b.{0,30}\b(cliente|lead|oportunidade|negocio|historico|registro|conta|loja)\b", Opcoes)),
        };

        private static RegexOptions Opcoes => RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

        private static readonly HashSet<string> Vazias = new(StringComparer.Ordinal)
        {
            "de", "da", "do", "das", "dos", "e", "ltda", "me", "epp", "sa", "cia", "the", "com", "para", "por", "uma", "um"
        };

        // ---- texto ----

        public static string Normalizar(string? texto)
        {
            if (string.IsNullOrWhiteSpace(texto)) return string.Empty;
            var d = texto.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(d.Length);
            var espaco = true;
            foreach (var c in d)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
                if (char.IsLetterOrDigit(c)) { sb.Append(char.ToLowerInvariant(c)); espaco = false; }
                else if (!espaco) { sb.Append(' '); espaco = true; }
            }
            return sb.ToString().Trim();
        }

        public static string Digitos(string? texto) => new((texto ?? string.Empty).Where(char.IsDigit).ToArray());

        /// <summary>Limpa texto vindo de IA ou de edicao: sem controle, sem excesso, uma linha de espacos.</summary>
        public static string? Limpar(string? texto, int max = MaxTexto)
        {
            if (string.IsNullOrWhiteSpace(texto)) return null;
            var sb = new StringBuilder(texto.Length);
            foreach (var c in texto)
                sb.Append(char.IsControl(c) && c != '\n' ? ' ' : c);
            var t = Regex.Replace(sb.ToString(), @"[ \t]+", " ").Trim();
            return t.Length == 0 ? null : (t.Length > max ? t[..max] : t);
        }

        /// <summary>O trecho precisa existir (ignorando acento, caixa e pontuacao) na transcricao. Nao basta a IA dizer que existe.</summary>
        public static bool TrechoExiste(string transcricaoNormalizada, string? trecho)
        {
            var t = Normalizar(trecho);
            return t.Length >= 3 && transcricaoNormalizada.Contains(t, StringComparison.Ordinal);
        }

        public static bool TemInstrucao(string? transcricao)
        {
            if (string.IsNullOrWhiteSpace(transcricao)) return false;
            try { return Instrucao.IsMatch(Normalizar(transcricao)); } catch (RegexMatchTimeoutException) { return true; }
        }

        public static IReadOnlyList<AcaoRecusada> AcoesProibidas(string? transcricao)
        {
            var saida = new List<AcaoRecusada>();
            var norm = Normalizar(transcricao);
            foreach (var (tipo, padrao) in Proibidas)
            {
                try
                {
                    var m = padrao.Match(norm);
                    if (m.Success)
                        saida.Add(new AcaoRecusada(tipo, "O assistente nunca executa isto por audio. Faca pelo painel, se for o caso.", Cortar(m.Value, 160)));
                }
                catch (RegexMatchTimeoutException) { }
            }
            return saida;
        }

        private static string Cortar(string t, int max) => t.Length <= max ? t : t[..max];

        /// <summary>Resposta curta do vendedor no WhatsApp: "sim" aceita, "nao" descarta, qualquer outra coisa nao e resposta.</summary>
        public static string? InterpretarResposta(string? texto)
        {
            var t = Normalizar(texto);
            if (t is "sim" or "s" or "ok" or "confirmo" or "confirmar" or "pode aplicar" or "aplicar") return "sim";
            if (t is "nao" or "n" or "descartar" or "rejeitar" or "nao aplicar") return "nao";
            return null;
        }

        // ---- validacao da saida da IA ----

        /// <summary>
        /// JSON da IA -> proposta. O servidor revalida tudo: tamanhos, trecho na transcricao, tipos. O que nao passa e descartado e vira sinal.
        /// JSON quebrado ou sem o objeto esperado e erro (nao se adivinha conteudo).
        /// </summary>
        public static PropostaVisita ValidarSaida(string json, string transcricao)
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 }); }
            catch (JsonException) { throw SaidaInvalida("A resposta da IA nao e um JSON valido."); }

            using (doc)
            {
                var r = doc.RootElement;
                if (r.ValueKind != JsonValueKind.Object) throw SaidaInvalida("A resposta da IA nao e um objeto.");
                var norm = Normalizar(transcricao);
                var p = new PropostaVisita { Resumo = Limpar(Str(r, "resumo"), MaxTexto) };
                var descartados = 0;

                string? Trecho(JsonElement e, string campo = "trecho")
                {
                    var t = Limpar(Str(e, campo), 300);
                    if (TrechoExiste(norm, t)) return t;
                    descartados++;
                    return null;
                }

                if (r.TryGetProperty("fatos", out var f) && f.ValueKind == JsonValueKind.Object)
                {
                    if (Obj(f, "empresa") is { } e1 && Limpar(Str(e1, "texto")) is { } emp && Trecho(e1) is { } te)
                        p.Fatos.Empresa = new ItemTexto(emp, te);
                    if (Obj(f, "decisor") is { } e2 && Limpar(Str(e2, "nome"), 120) is { } dn && Trecho(e2) is { } td)
                        p.Fatos.Decisor = new ItemDecisor(dn, Limpar(Str(e2, "cargo"), 120), td);
                    if (Obj(f, "preco") is { } e3 && Limpar(Str(e3, "texto")) is { } pt && Trecho(e3) is { } tp)
                        p.Fatos.Preco = new ItemPreco(pt, Num(e3, "valor"), tp);
                    if (Obj(f, "proximo_passo") is { } e4 && Limpar(Str(e4, "texto")) is { } pp && Trecho(e4) is { } tn)
                        p.Fatos.ProximoPasso = new ItemProximoPasso(pp, Limpar(Str(e4, "data"), 40), tn);
                    foreach (var o in Lista(f, "objecoes"))
                        if (Limpar(Str(o, "texto")) is { } ot && Trecho(o) is { } to) p.Fatos.Objecoes.Add(new ItemTexto(ot, to));
                    foreach (var o in Lista(f, "concorrentes"))
                        if (Limpar(Str(o, "nome"), 120) is { } cn && Trecho(o) is { } tc) p.Fatos.Concorrentes.Add(new ItemNome(cn, tc));
                    foreach (var o in Lista(f, "promessas"))
                        if (Limpar(Str(o, "texto")) is { } pr && Trecho(o) is { } tpr) p.Fatos.Promessas.Add(new ItemPromessa(pr, Limpar(Str(o, "quem"), 60), tpr));
                }

                foreach (var o in Lista(r, "inferencias"))
                {
                    var conf = Normalizar(Str(o, "confianca"));
                    if (conf is not ("alta" or "media" or "baixa")) conf = "baixa";
                    if (Limpar(Str(o, "texto")) is { } it && Trecho(o) is { } ti) p.Inferencias.Add(new Inferencia(it, conf, ti));
                }

                if (descartados > 0) p.Sinais.Add("evidencia_descartada");
                // A IA nao define alteracoes: se mandar o campo, e ignorado e registrado.
                if (r.TryGetProperty("alteracoes", out _) || r.TryGetProperty("acoes", out _)) p.Sinais.Add("item_fora_do_contrato");
                return p;
            }
        }

        private static AssistenteAudioException SaidaInvalida(string msg) =>
            new(503, CodigoAudio.IaIndisponivel, msg, tentarDepois: true);

        private static JsonElement? Obj(JsonElement e, string nome) =>
            e.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

        private static IEnumerable<JsonElement> Lista(JsonElement e, string nome) =>
            e.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.Array
                ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object).Take(MaxItens).ToList()
                : Enumerable.Empty<JsonElement>();

        private static string? Str(JsonElement e, string nome) =>
            e.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static decimal? Num(JsonElement e, string nome) =>
            e.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) && d >= 0 && d < 100_000_000m ? d : null;

        // ---- alteracoes (montadas pelo servidor) ----

        /// <summary>Nota de historico + proxima acao, compostas dos fatos ja validados. O texto da IA entra so como fato com trecho confirmado.</summary>
        public static List<AlteracaoProposta> MontarAlteracoes(PropostaVisita p, string? proximaAcaoAtual)
        {
            var alt = new List<AlteracaoProposta>();
            var partes = new List<string>();
            if (p.Resumo != null) partes.Add(p.Resumo);
            var f = p.Fatos;
            if (f.Decisor != null) partes.Add($"Decisor: {f.Decisor.Nome}{(f.Decisor.Cargo != null ? $" ({f.Decisor.Cargo})" : "")}.");
            if (f.Preco != null) partes.Add($"Preco citado: {f.Preco.Texto}.");
            if (f.Objecoes.Count > 0) partes.Add("Objecoes: " + string.Join("; ", f.Objecoes.Select(o => o.Texto)) + ".");
            if (f.Concorrentes.Count > 0) partes.Add("Concorrentes: " + string.Join(", ", f.Concorrentes.Select(o => o.Nome)) + ".");
            if (f.Promessas.Count > 0) partes.Add("Promessas: " + string.Join("; ", f.Promessas.Select(o => o.Texto)) + ".");
            if (partes.Count > 0)
            {
                var nota = string.Join(" ", partes);
                var trecho = f.Empresa?.Trecho ?? f.Preco?.Trecho ?? f.Objecoes.FirstOrDefault()?.Trecho ?? f.Decisor?.Trecho ?? string.Empty;
                alt.Add(new AlteracaoProposta("a1", TipoAlteracao.NotaHistorico, Limpar("Visita (relato por audio): " + nota, MaxNota)!, null, trecho));
            }
            if (f.ProximoPasso != null)
            {
                var valor = f.ProximoPasso.Data != null ? $"{f.ProximoPasso.Texto} ({f.ProximoPasso.Data})" : f.ProximoPasso.Texto;
                alt.Add(new AlteracaoProposta("a2", TipoAlteracao.ProximaAcao, Limpar(valor)!, proximaAcaoAtual, f.ProximoPasso.Trecho));
            }
            return alt;
        }

        /// <summary>Valida uma edicao feita pela pessoa: mesmo tamanho maximo, sem tipo novo, sem texto vazio.</summary>
        public static string ValidarEdicao(AlteracaoProposta original, string? novoValor)
        {
            var limite = original.Tipo == TipoAlteracao.NotaHistorico ? MaxNota : MaxTexto;
            return Limpar(novoValor, limite)
                ?? throw new AssistenteAudioException(422, CodigoAudio.AlteracaoInvalida, $"A alteracao {original.Id} nao pode ficar vazia.");
        }

        // ---- match de oportunidade ----

        private static readonly Regex NumeroLongo = new(@"\d[\d\.\-\/\s\(\)]{8,22}\d", RegexOptions.Compiled, TimeSpan.FromMilliseconds(250));

        public static IReadOnlyList<string> DigitosCitados(string transcricao)
        {
            try { return NumeroLongo.Matches(transcricao).Select(m => Digitos(m.Value)).Where(d => d.Length >= 9).Distinct().ToList(); }
            catch (RegexMatchTimeoutException) { return Array.Empty<string>(); }
        }

        private static HashSet<string> Tokens(string? texto) =>
            Normalizar(texto).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => t.Length >= 3 && !Vazias.Contains(t)).ToHashSet(StringComparer.Ordinal);

        /// <summary>
        /// Pontua cada oportunidade acessivel por CNPJ (100), telefone (90), nome da empresa (80 igual, 60/40 parecido), nome da empresa dito em
        /// qualquer ponto do audio (70), decisor = contato (+15) e decisor citado no historico (+10). Unico so com 70+ e 20 pontos de folga;
        /// abaixo de 40 e "nenhum". Nao ha desempate silencioso: empatou, e ambiguo.
        /// </summary>
        public static ResultadoMatch Resolver(PropostaVisita proposta, string transcricao, IReadOnlyList<OportunidadeParaMatch> oportunidades)
        {
            var norm = Normalizar(transcricao);
            var numeros = DigitosCitados(transcricao);
            var empresaDita = Tokens(proposta.Fatos.Empresa?.Texto);
            var empresaDitaNorm = Normalizar(proposta.Fatos.Empresa?.Texto);
            var decisor = Tokens(proposta.Fatos.Decisor?.Nome);
            var pontuados = new List<CandidatoOportunidade>();

            foreach (var o in oportunidades)
            {
                var pontos = 0;
                var motivos = new List<string>();
                void Dar(int p, string m)
                {
                    if (m.StartsWith('+')) { pontos += p; motivos.Add(m[1..]); }
                    else if (p > pontos) { pontos = p; motivos.Add(m); }
                }

                var cnpj = Digitos(o.Cnpj);
                if (cnpj.Length == 14 && numeros.Any(n => n.Contains(cnpj, StringComparison.Ordinal))) Dar(100, "cnpj citado");
                var fone = Digitos(o.Telefone);
                if (fone.Length >= 9 && numeros.Any(n => n.EndsWith(fone[^9..], StringComparison.Ordinal))) Dar(90, "telefone citado");

                var nomeOpp = Normalizar(o.Empresa);
                var tokOpp = Tokens(o.Empresa);
                if (empresaDitaNorm.Length > 0 && tokOpp.Count > 0)
                {
                    if (empresaDitaNorm == nomeOpp) Dar(80, "nome da empresa igual");
                    else
                    {
                        var comuns = tokOpp.Intersect(empresaDita).Count();
                        var razao = comuns / (double)Math.Max(tokOpp.Count, Math.Max(empresaDita.Count, 1));
                        if (razao >= 0.6) Dar(60, "nome da empresa parecido");
                        else if (comuns > 0 && razao >= 0.34) Dar(40, "nome da empresa parcial");
                    }
                }
                if (nomeOpp.Length >= 4 && norm.Contains(nomeOpp, StringComparison.Ordinal)) Dar(70, "empresa citada no audio");

                if (pontos > 0 && decisor.Count > 0)
                {
                    var contato = Tokens(o.Contato);
                    if (contato.Count > 0 && contato.Intersect(decisor).Any()) Dar(15, "+decisor e o contato");
                    else if (Tokens(o.Historico).Intersect(decisor).Any()) Dar(10, "+decisor citado no historico");
                }
                if (pontos >= 40) pontuados.Add(new CandidatoOportunidade(o.Id, o.Empresa, o.Contato, pontos, motivos));
            }

            var ordenados = pontuados.OrderByDescending(c => c.Pontos).ThenBy(c => c.Empresa, StringComparer.OrdinalIgnoreCase).Take(5).ToList();
            var r = new ResultadoMatch { Candidatos = ordenados };
            if (ordenados.Count == 0) return r;
            var topo = ordenados[0];
            if (topo.Pontos >= 70 && (ordenados.Count == 1 || topo.Pontos - ordenados[1].Pontos >= 20))
            {
                r.Situacao = "unico";
                r.OportunidadeId = topo.Id;
            }
            else r.Situacao = "ambiguo";
            return r;
        }

        // ---- custo ----

        public static decimal CustoTranscricao(AssistenteAudioOptions o, decimal segundos) =>
            Math.Round(segundos / 60m * (o.CustoPorMinutoTranscricao ?? 0m), 6);

        public static decimal CustoInterpretacao(AssistenteAudioOptions o, long tokensEntrada, long tokensSaida) =>
            Math.Round(tokensEntrada / 1000m * (o.CustoPorMilTokensEntrada ?? 0m) + tokensSaida / 1000m * (o.CustoPorMilTokensSaida ?? 0m), 6);

        /// <summary>
        /// Pior caso de uma entrada, reservado antes de chamar fornecedor: duracao maxima + tokens de entrada (a transcricao cabe em
        /// ~1 token a cada 3 caracteres, e o maximo de fala por duracao e limitado por MaxBytes) + saida maxima.
        /// </summary>
        public static decimal CustoReserva(AssistenteAudioOptions o) =>
            CustoTranscricao(o, o.MaxDuracaoSegundos ?? 0)
            + CustoInterpretacao(o, TokensEntradaMaximos(o), o.MaxTokensSaida ?? 0);

        /// <summary>Teto de tokens de entrada da interpretacao, pelo mesmo limite de caracteres que o servico aplica a transcricao.</summary>
        public static long TokensEntradaMaximos(AssistenteAudioOptions o) => LimiteCaracteresTranscricao(o) / 3 + 1500;

        /// <summary>Fala continua ~ 20 caracteres/segundo no maximo; passou disso e transcricao fora de controle e e cortada.</summary>
        public static int LimiteCaracteresTranscricao(AssistenteAudioOptions o) => Math.Max(1000, (o.MaxDuracaoSegundos ?? 0) * 20);

        // ---- mensagem de retorno ----

        public static string ResumoWhatsapp(EntradaAudio e, PropostaVisita p, string? empresaEscolhida)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Entendi seu audio da visita:");
            if (p.Resumo != null) sb.AppendLine(p.Resumo);
            var f = p.Fatos;
            if (f.Preco != null) sb.AppendLine($"- Preco: {f.Preco.Texto}");
            foreach (var o in f.Objecoes) sb.AppendLine($"- Objecao: {o.Texto}");
            foreach (var c in f.Concorrentes) sb.AppendLine($"- Concorrente: {c.Nome}");
            foreach (var o in f.Promessas) sb.AppendLine($"- Promessa: {o.Texto}");
            if (f.ProximoPasso != null) sb.AppendLine($"- Proximo passo: {f.ProximoPasso.Texto}");
            foreach (var a in p.AcoesRecusadas.Take(3)) sb.AppendLine($"(Nao executo por audio: {a.Tipo.Replace('_', ' ')}.)");
            sb.AppendLine();
            var m = p.Oportunidade;
            if (m.Situacao == "unico" && empresaEscolhida != null && p.Alteracoes.Count > 0)
                sb.Append($"Registrar {p.Alteracoes.Count} alteracao(oes) na oportunidade {empresaEscolhida}? Responda SIM ou NAO. Nada foi alterado ainda.");
            else if (m.Situacao == "ambiguo")
                sb.Append("Achei mais de uma oportunidade possivel. Escolha qual no painel (CRM > Assistente). Nada foi alterado.");
            else if (m.Situacao == "nenhum")
                sb.Append("Nao encontrei a oportunidade. Escolha ou crie no painel (CRM > Assistente). Nada foi alterado.");
            else
                sb.Append("Nao identifiquei nada para registrar. Revise no painel (CRM > Assistente). Nada foi alterado.");
            return sb.ToString().Trim();
        }

        public static string TextoErroWhatsapp(string codigo) => codigo switch
        {
            CodigoAudio.ConsentimentoAusente => "Para interpretar audios de visita, autorize o uso no painel (CRM > Assistente) e reenvie. Nada foi processado.",
            CodigoAudio.TetoExcedido => "O limite mensal do assistente de audio foi atingido. Seu audio ficou guardado; um gestor precisa liberar.",
            CodigoAudio.IaIndisponivel => "O assistente de audio esta indisponivel agora. Seu audio ficou guardado e sera tentado de novo.",
            CodigoAudio.MidiaInvalida => "Nao consegui usar esse audio (tamanho, duracao ou formato). Envie um audio mais curto.",
            _ => "Nao consegui processar esse audio. Ele ficou guardado no painel (CRM > Assistente) para reprocessar."
        };
    }
}
