using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace APIBack.Atendimento.Templates
{
    /// <summary>
    /// Regras da Meta para modelos de texto (cabecalho TEXT, corpo, rodape e botoes), conferidas antes de gastar uma chamada:
    /// o que passa aqui ainda vai para analise da Meta, que e quem aprova. Sem rede, para ser testado.
    /// </summary>
    public static class TemplateRegras
    {
        public const int NomeMax = 512;
        public const int CabecalhoMax = 60;
        public const int CorpoMax = 1024;
        public const int RodapeMax = 60;
        public const int ExemploMax = 200;
        public const int VariaveisCorpoMax = 20;
        public const int BotoesMax = 10;
        public const int TextoBotaoMax = 25;
        public const int UrlMax = 2000;
        public const int CodigoMax = 15;

        private static readonly Regex NomeValido = new("^[a-z0-9_]+$", RegexOptions.Compiled);
        private static readonly Regex Variavel = new(@"\{\{(\d+)\}\}", RegexOptions.Compiled);
        private static readonly Regex TerminaComVariavel = new(@"\{\{\d+\}\}\s*$", RegexOptions.Compiled);
        private static readonly Regex VariaveisColadas = new(@"\{\{\d+\}\}\s*\{\{\d+\}\}", RegexOptions.Compiled);
        private static readonly Regex Telefone = new(@"^\+[1-9][0-9]{7,19}$", RegexOptions.Compiled);
        private static readonly Regex CodigoValido = new("^[A-Za-z0-9]+$", RegexOptions.Compiled);

        /// <summary>
        /// Normaliza e valida. Pedido malformado vira <see cref="TemplateRequisicaoInvalidaException"/> (400); conteudo que a Meta
        /// recusaria vira <see cref="TemplateIncompativelException"/> (422) com todos os campos errados de uma vez.
        /// </summary>
        public static TemplateNovo Validar(CriarTemplateRequest? request)
        {
            var formato = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (request == null)
            {
                Add(formato, "conteudo", "Envie o modelo.");
                throw new TemplateRequisicaoInvalidaException(formato);
            }

            var categoria = CategoriaTemplate.Normalizar(request.Categoria);
            if (categoria == null) Add(formato, "categoria", "Escolha a categoria: Utilidade ou Marketing.");
            var idioma = (request.Idioma ?? IdiomaTemplate.PortuguesBrasil).Trim();
            if (!IdiomaTemplate.Suportados.Contains(idioma)) Add(formato, "idioma", "Idioma nao suportado. Use pt_BR.");
            if (request.Conteudo == null) Add(formato, "conteudo", "Informe o conteudo do modelo.");
            foreach (var (botao, i) in (request.Conteudo?.Botoes ?? Array.Empty<TemplateBotao>()).Select((b, i) => (b, i)))
            {
                if (botao == null || !TipoBotao.Todos.Contains((botao.Tipo ?? string.Empty).Trim().ToUpperInvariant()))
                    Add(formato, $"botoes[{i}].tipo", "Tipo de botao desconhecido.");
            }
            if (formato.Count > 0) throw new TemplateRequisicaoInvalidaException(formato);

            var erros = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var nome = (request.Nome ?? string.Empty).Trim();
            if (nome.Length == 0) Add(erros, "nome", "Informe o nome tecnico.");
            else if (nome.Length > NomeMax) Add(erros, "nome", $"O nome tecnico pode ter ate {NomeMax} caracteres.");
            else if (!NomeValido.IsMatch(nome)) Add(erros, "nome", "Use so letras minusculas sem acento, numeros e _ (ex.: pedido_confirmado).");

            var c = request.Conteudo!;
            var cabecalho = Limpo(c.Cabecalho);
            var cabecalhoExemplos = ValidarCabecalho(cabecalho, c.CabecalhoExemplos, erros);
            var corpo = (c.Corpo ?? string.Empty).Trim();
            var corpoExemplos = ValidarCorpo(corpo, c.CorpoExemplos, erros);
            var rodape = Limpo(c.Rodape);
            if (rodape != null)
            {
                if (rodape.Length > RodapeMax) Add(erros, "rodape", $"O rodape pode ter ate {RodapeMax} caracteres.");
                if (rodape.Contains("{{") || rodape.Contains("}}")) Add(erros, "rodape", "O rodape nao aceita variaveis.");
                if (rodape.Contains('\n')) Add(erros, "rodape", "O rodape deve ter uma linha so.");
            }
            var botoes = ValidarBotoes(c.Botoes, categoria!, erros);

            if (erros.Count > 0) throw new TemplateIncompativelException(erros);

            return new TemplateNovo(nome, categoria!, idioma,
                new TemplateConteudo(cabecalho, cabecalhoExemplos, corpo, corpoExemplos, rodape, botoes));
        }

        /// <summary>Numeros das variaveis {{n}} no texto, na ordem em que aparecem (com repeticoes).</summary>
        public static IReadOnlyList<int> Variaveis(string? texto) =>
            Variavel.Matches(texto ?? string.Empty).Select(m => int.TryParse(m.Groups[1].Value, out var n) ? n : 0).ToList();

        private static IReadOnlyList<string>? ValidarCabecalho(string? cabecalho, IReadOnlyList<string>? exemplos, Dictionary<string, List<string>> erros)
        {
            if (cabecalho == null) return null;

            if (cabecalho.Length > CabecalhoMax) Add(erros, "cabecalho", $"O cabecalho pode ter ate {CabecalhoMax} caracteres.");
            if (cabecalho.Contains('\n')) Add(erros, "cabecalho", "O cabecalho deve ter uma linha so.");
            if (Malformado(cabecalho)) Add(erros, "cabecalho", "Variavel malformada. Use {{1}}.");
            var vars = Variaveis(cabecalho);
            if (vars.Count > 1 || vars.Any(v => v != 1))
            {
                Add(erros, "cabecalho", "O cabecalho aceita no maximo uma variavel, {{1}}.");
                return null;
            }

            return ValidarExemplos(vars.Count, exemplos, "cabecalhoExemplos", erros);
        }

        private static IReadOnlyList<string>? ValidarCorpo(string corpo, IReadOnlyList<string>? exemplos, Dictionary<string, List<string>> erros)
        {
            if (corpo.Length == 0)
            {
                Add(erros, "corpo", "Escreva o corpo da mensagem.");
                return null;
            }

            if (corpo.Length > CorpoMax) Add(erros, "corpo", $"O corpo pode ter ate {CorpoMax} caracteres.");
            if (Malformado(corpo))
            {
                Add(erros, "corpo", "Variavel malformada. Use {{1}}, {{2}}, ... sem espacos dentro das chaves.");
                return null;
            }

            var vars = Variaveis(corpo);
            if (vars.Count == 0) return ValidarExemplos(0, exemplos, "corpoExemplos", erros);

            var distintas = vars.Distinct().OrderBy(v => v).ToList();
            if (distintas.Count > VariaveisCorpoMax) Add(erros, "corpo", $"Use no maximo {VariaveisCorpoMax} variaveis.");
            if (!distintas.SequenceEqual(Enumerable.Range(1, distintas.Count)))
                Add(erros, "corpo", "As variaveis devem ser numeradas em sequencia, comecando em {{1}}, sem pular numeros.");
            if (Variavel.Match(corpo) is { Success: true, Index: 0 }) Add(erros, "corpo", "O corpo nao pode comecar com uma variavel.");
            if (TerminaComVariavel.IsMatch(corpo)) Add(erros, "corpo", "O corpo nao pode terminar com uma variavel.");
            if (VariaveisColadas.IsMatch(corpo)) Add(erros, "corpo", "Coloque texto entre duas variaveis.");

            return ValidarExemplos(distintas.Count, exemplos, "corpoExemplos", erros);
        }

        private static IReadOnlyList<string>? ValidarExemplos(int quantidade, IReadOnlyList<string>? exemplos, string campo, Dictionary<string, List<string>> erros)
        {
            var lista = (exemplos ?? Array.Empty<string>()).Select(e => (e ?? string.Empty).Trim()).ToList();
            if (quantidade == 0)
            {
                if (lista.Any(e => e.Length > 0)) Add(erros, campo, "Ha exemplos sem variavel correspondente no texto.");
                return null;
            }

            if (lista.Count != quantidade)
            {
                Add(erros, campo, $"Informe um exemplo para cada variavel ({quantidade}).");
                return null;
            }

            for (var i = 0; i < lista.Count; i++)
            {
                var e = lista[i];
                if (e.Length == 0) Add(erros, $"{campo}[{i}]", $"Informe o exemplo da variavel {{{{{i + 1}}}}}.");
                else if (e.Length > ExemploMax) Add(erros, $"{campo}[{i}]", $"O exemplo pode ter ate {ExemploMax} caracteres.");
                else if (e.Contains("{{") || e.Contains("}}")) Add(erros, $"{campo}[{i}]", "O exemplo nao pode conter chaves de variavel.");
                else if (e.Contains('\n') || e.Contains('\t')) Add(erros, $"{campo}[{i}]", "O exemplo deve ter uma linha so.");
            }

            return lista;
        }

        private static IReadOnlyList<TemplateBotao>? ValidarBotoes(IReadOnlyList<TemplateBotao>? botoes, string categoria, Dictionary<string, List<string>> erros)
        {
            if (botoes == null || botoes.Count == 0) return null;
            if (botoes.Count > BotoesMax) Add(erros, "botoes", $"Use no maximo {BotoesMax} botoes.");

            var normalizados = new List<TemplateBotao>();
            for (var i = 0; i < botoes.Count; i++)
            {
                var b = botoes[i];
                var tipo = b.Tipo!.Trim().ToUpperInvariant();
                var campo = $"botoes[{i}]";
                var texto = Limpo(b.Texto);

                if (tipo != TipoBotao.CopiarCodigo)
                {
                    if (texto == null) Add(erros, $"{campo}.texto", "Informe o texto do botao.");
                    else if (texto.Length > TextoBotaoMax) Add(erros, $"{campo}.texto", $"O texto do botao pode ter ate {TextoBotaoMax} caracteres.");
                    else if (texto.Contains("{{") || texto.Contains('\n')) Add(erros, $"{campo}.texto", "O texto do botao nao aceita variaveis nem quebra de linha.");
                }

                switch (tipo)
                {
                    case TipoBotao.RespostaRapida:
                        normalizados.Add(new TemplateBotao(tipo, texto));
                        break;

                    case TipoBotao.Url:
                        normalizados.Add(ValidarUrl(b, texto, campo, erros));
                        break;

                    case TipoBotao.Telefone:
                        var telefone = new string((b.Telefone ?? string.Empty).Where(ch => ch == '+' || char.IsAsciiDigit(ch)).ToArray());
                        if (!telefone.StartsWith('+')) telefone = "+" + telefone;
                        if (!Telefone.IsMatch(telefone)) Add(erros, $"{campo}.telefone", "Informe o telefone com DDI, ex.: +5511999999999.");
                        normalizados.Add(new TemplateBotao(tipo, texto, Telefone: telefone));
                        break;

                    case TipoBotao.CopiarCodigo:
                        if (categoria != CategoriaTemplate.Marketing)
                            Add(erros, $"{campo}.tipo", "O botao de copiar codigo so existe em modelos de Marketing.");
                        var codigo = Limpo(b.Codigo);
                        if (codigo == null || codigo.Length > CodigoMax || !CodigoValido.IsMatch(codigo))
                            Add(erros, $"{campo}.codigo", $"Informe um codigo de exemplo com ate {CodigoMax} letras ou numeros.");
                        normalizados.Add(new TemplateBotao(tipo, Codigo: codigo));
                        break;
                }
            }

            int Quantos(string tipo) => normalizados.Count(b => b.Tipo == tipo);
            if (Quantos(TipoBotao.Url) > 2) Add(erros, "botoes", "Use no maximo 2 botoes de link.");
            if (Quantos(TipoBotao.Telefone) > 1) Add(erros, "botoes", "Use no maximo 1 botao de ligar.");
            if (Quantos(TipoBotao.CopiarCodigo) > 1) Add(erros, "botoes", "Use no maximo 1 botao de copiar codigo.");

            // A Meta exige respostas rapidas juntas (todas antes ou todas depois dos botoes de acao).
            var tipos = normalizados.Select(b => b.Tipo == TipoBotao.RespostaRapida).ToList();
            var trocas = tipos.Zip(tipos.Skip(1), (a, b) => a != b).Count(t => t);
            if (trocas > 1) Add(erros, "botoes", "Agrupe as respostas rapidas: todas antes ou todas depois dos outros botoes.");

            var repetidos = normalizados.Where(b => b.Texto != null).GroupBy(b => b.Texto!, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1);
            if (repetidos.Any()) Add(erros, "botoes", "Os botoes precisam ter textos diferentes.");

            return normalizados;
        }

        private static TemplateBotao ValidarUrl(TemplateBotao b, string? texto, string campo, Dictionary<string, List<string>> erros)
        {
            var url = (b.Url ?? string.Empty).Trim();
            var vars = Variaveis(url);
            var semVariavel = Variavel.Replace(url, string.Empty);
            if (url.Length == 0 || url.Length > UrlMax || !Uri.TryCreate(vars.Count > 0 ? semVariavel + "x" : url, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("https" or "http"))
            {
                Add(erros, $"{campo}.url", "Informe um link completo, comecando com https://.");
                return new TemplateBotao(TipoBotao.Url, texto, url);
            }

            if (Malformado(url) || vars.Count > 1 || (vars.Count == 1 && (vars[0] != 1 || !url.EndsWith("{{1}}", StringComparison.Ordinal))))
            {
                Add(erros, $"{campo}.url", "O link aceita uma variavel so, {{1}}, no final.");
                return new TemplateBotao(TipoBotao.Url, texto, url);
            }

            if (vars.Count == 0) return new TemplateBotao(TipoBotao.Url, texto, url);

            var exemplo = (b.UrlExemplo ?? string.Empty).Trim();
            var prefixo = url[..^"{{1}}".Length];
            if (exemplo.Length <= prefixo.Length || !exemplo.StartsWith(prefixo, StringComparison.Ordinal) || exemplo.Contains("{{") || exemplo.Length > UrlMax)
                Add(erros, $"{campo}.urlExemplo", "Informe o link completo de exemplo (o mesmo endereco com a parte variavel preenchida).");
            return new TemplateBotao(TipoBotao.Url, texto, url, exemplo);
        }

        /// <summary>Chaves que nao formam uma variavel {{n}}: {{ 1 }}, {{nome}}, {1}, {{1}.</summary>
        private static bool Malformado(string texto)
        {
            var resto = Variavel.Replace(texto, string.Empty);
            return resto.Contains("{{") || resto.Contains("}}") || Regex.IsMatch(resto, @"\{\s*\d+\s*\}");
        }

        private static string? Limpo(string? texto)
        {
            var t = texto?.Trim();
            return string.IsNullOrEmpty(t) ? null : t;
        }

        private static void Add(Dictionary<string, List<string>> erros, string campo, string mensagem)
        {
            if (!erros.TryGetValue(campo, out var lista)) erros[campo] = lista = new List<string>();
            if (!lista.Contains(mensagem)) lista.Add(mensagem);
        }

        // ---- Formato da Meta ----------------------------------------------------------------------------------

        /// <summary>Corpo do POST /{waba-id}/message_templates.</summary>
        public static Dictionary<string, object> PayloadMeta(TemplateNovo t) => new()
        {
            ["name"] = t.Nome,
            ["language"] = t.Idioma,
            ["category"] = t.Categoria,
            ["components"] = ComponentesMeta(t.Conteudo)
        };

        public static List<object> ComponentesMeta(TemplateConteudo c)
        {
            var componentes = new List<object>();
            if (c.Cabecalho != null)
            {
                var header = new Dictionary<string, object> { ["type"] = "HEADER", ["format"] = "TEXT", ["text"] = c.Cabecalho };
                if (c.CabecalhoExemplos is { Count: > 0 }) header["example"] = new Dictionary<string, object> { ["header_text"] = c.CabecalhoExemplos };
                componentes.Add(header);
            }

            var body = new Dictionary<string, object> { ["type"] = "BODY", ["text"] = c.Corpo ?? string.Empty };
            if (c.CorpoExemplos is { Count: > 0 }) body["example"] = new Dictionary<string, object> { ["body_text"] = new[] { c.CorpoExemplos } };
            componentes.Add(body);

            if (c.Rodape != null) componentes.Add(new Dictionary<string, object> { ["type"] = "FOOTER", ["text"] = c.Rodape });

            if (c.Botoes is { Count: > 0 })
            {
                componentes.Add(new Dictionary<string, object>
                {
                    ["type"] = "BUTTONS",
                    ["buttons"] = c.Botoes.Select(BotaoMeta).ToList()
                });
            }

            return componentes;
        }

        private static object BotaoMeta(TemplateBotao b) => b.Tipo switch
        {
            TipoBotao.Url when b.UrlExemplo != null => new Dictionary<string, object> { ["type"] = "URL", ["text"] = b.Texto!, ["url"] = b.Url!, ["example"] = new[] { b.UrlExemplo } },
            TipoBotao.Url => new Dictionary<string, object> { ["type"] = "URL", ["text"] = b.Texto!, ["url"] = b.Url! },
            TipoBotao.Telefone => new Dictionary<string, object> { ["type"] = "PHONE_NUMBER", ["text"] = b.Texto!, ["phone_number"] = b.Telefone! },
            TipoBotao.CopiarCodigo => new Dictionary<string, object> { ["type"] = "COPY_CODE", ["example"] = b.Codigo! },
            _ => new Dictionary<string, object> { ["type"] = "QUICK_REPLY", ["text"] = b.Texto! }
        };

        /// <summary>Le os componentes devolvidos pela Meta no formato do Zippy (para mostrar o que a Meta tem de fato).</summary>
        public static TemplateConteudo? DeComponentesMeta(JsonElement componentes)
        {
            if (componentes.ValueKind != JsonValueKind.Array) return null;

            string? cabecalho = null, corpo = null, rodape = null;
            List<string>? cabecalhoEx = null, corpoEx = null;
            List<TemplateBotao>? botoes = null;

            foreach (var comp in componentes.EnumerateArray())
            {
                var tipo = Texto(comp, "type")?.ToUpperInvariant();
                switch (tipo)
                {
                    case "HEADER" when (Texto(comp, "format") ?? "TEXT").Equals("TEXT", StringComparison.OrdinalIgnoreCase):
                        cabecalho = Texto(comp, "text");
                        cabecalhoEx = Exemplos(comp, "header_text", aninhado: false);
                        break;
                    case "BODY":
                        corpo = Texto(comp, "text");
                        corpoEx = Exemplos(comp, "body_text", aninhado: true);
                        break;
                    case "FOOTER":
                        rodape = Texto(comp, "text");
                        break;
                    case "BUTTONS" when comp.TryGetProperty("buttons", out var lista) && lista.ValueKind == JsonValueKind.Array:
                        botoes = lista.EnumerateArray().Select(b =>
                        {
                            var bt = Texto(b, "type")?.ToUpperInvariant();
                            string? ex = b.TryGetProperty("example", out var e)
                                ? e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().Select(x => x.GetString()).FirstOrDefault() : e.ValueKind == JsonValueKind.String ? e.GetString() : null
                                : null;
                            return bt == TipoBotao.CopiarCodigo
                                ? new TemplateBotao(bt, Texto(b, "text"), Codigo: ex)
                                : new TemplateBotao(bt, Texto(b, "text"), Texto(b, "url"), bt == TipoBotao.Url ? ex : null, Texto(b, "phone_number"));
                        }).ToList();
                        break;
                }
            }

            return new TemplateConteudo(cabecalho, cabecalhoEx, corpo, corpoEx, rodape, botoes);
        }

        private static string? Texto(JsonElement e, string nome) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static List<string>? Exemplos(JsonElement comp, string campo, bool aninhado)
        {
            if (!comp.TryGetProperty("example", out var ex) || !ex.TryGetProperty(campo, out var lista) || lista.ValueKind != JsonValueKind.Array) return null;
            var alvo = aninhado ? lista.EnumerateArray().FirstOrDefault() : lista;
            return alvo.ValueKind == JsonValueKind.Array ? alvo.EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToList() : null;
        }

        /// <summary>Orientacao para corrigir, pelo motivo de rejeicao da Meta (rejected_reason).</summary>
        public static string? OrientacaoRejeicao(string? motivo) => (motivo ?? string.Empty).ToUpperInvariant() switch
        {
            "" or "NONE" => null,
            "INVALID_FORMAT" => "O formato nao foi aceito: confira variaveis (em sequencia, com texto entre elas e sem iniciar ou terminar o corpo) e os exemplos. Crie uma nova versao com outro nome.",
            "TAG_CONTENT_MISMATCH" => "O conteudo nao combina com a categoria escolhida. Se a mensagem tem oferta ou promocao, use Marketing; se e sobre um pedido, deixe so a informacao do pedido.",
            "INCORRECT_CATEGORY" => "A categoria nao corresponde ao conteudo. Crie uma nova versao na categoria correta.",
            "PROMOTIONAL" => "A Meta considerou o conteudo promocional. Use a categoria Marketing ou retire o tom de oferta.",
            "ABUSIVE_CONTENT" => "A Meta considerou o conteudo inadequado. Revise o texto seguindo as politicas do WhatsApp Business.",
            "SCAM" => "A Meta considerou o conteudo enganoso. Revise links e promessas e siga as politicas do WhatsApp Business.",
            _ => "A Meta recusou o modelo. Revise o texto e os exemplos e crie uma nova versao com outro nome."
        };
    }
}
