using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using APIBack.DTOs.Atendimento;

namespace APIBack.Service
{
    /// <summary>Regras puras do atendimento (Fase 5): configuracao, respostas rapidas, telefone e mensagens ao motoboy.</summary>
    public static class AtendimentoModos
    {
        public const string Humano = "humano";
        public const string Ia = "ia";
    }

    /// <summary>Textos do bot que o dono pode trocar. A saudacao e a mensagem de fora do horario tem coluna propria.</summary>
    public static class MensagensDoAtendimento
    {
        public const string Menu = "menu";
        public const string Cardapio = "cardapio";
        public const string Atendente = "atendente";
        public const string Agendamento = "agendamento";
        public const string SemServico = "semServico";
        public const string CardapioFechado = "cardapioFechado";

        public static readonly IReadOnlyList<string> Chaves = new[] { Menu, Cardapio, Atendente, Agendamento, SemServico, CardapioFechado };
        /// <summary>{loja} vira o nome da loja e {link} o endereco do cardapio.</summary>
        public static readonly IReadOnlyList<string> Variaveis = new[] { "loja", "link", "abre" };

        private static readonly Regex Variavel = new(@"\{([^{}]*)\}", RegexOptions.Compiled);

        /// <summary>Variaveis usadas no texto que o sistema nao conhece (ex.: {nome}).</summary>
        public static IReadOnlyList<string> VariaveisDesconhecidas(string texto) =>
            Variavel.Matches(texto).Select(m => m.Groups[1].Value).Where(v => !Variaveis.Contains(v)).Distinct().ToList();

        /// <summary>Limpa e valida os textos. Vazio some (volta ao padrao). O texto do cardapio sem {link} deixaria o cliente sem o endereco.</summary>
        public static Dictionary<string, string> Normalizar(Dictionary<string, string>? mensagens, int max)
        {
            var resultado = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (chave, valor) in mensagens ?? new Dictionary<string, string>())
            {
                if (!Chaves.Contains(chave))
                {
                    throw new DeliveryDomainException(422, "INVALID_MESSAGE_KEY", $"Texto desconhecido: '{chave}'.");
                }

                var texto = valor?.Trim();
                if (string.IsNullOrEmpty(texto)) continue;
                if (texto.Length > max)
                {
                    throw new DeliveryDomainException(422, "INVALID_MESSAGE", $"O texto '{chave}' aceita no maximo {max} caracteres.");
                }

                var desconhecidas = VariaveisDesconhecidas(texto);
                if (desconhecidas.Count > 0)
                {
                    throw new DeliveryDomainException(422, "INVALID_MESSAGE",
                        $"O texto '{chave}' usa variaveis que nao existem: {string.Join(", ", desconhecidas.Select(v => "{" + v + "}"))}. Use {{loja}}, {{link}} e {{abre}}.");
                }

                if (chave == Cardapio && !texto.Contains("{link}", StringComparison.Ordinal))
                {
                    throw new DeliveryDomainException(422, "INVALID_MESSAGE", "O texto do cardapio precisa conter {link}: e por ele que o cliente recebe o endereco.");
                }

                resultado[chave] = texto;
            }

            return resultado;
        }
    }

    public static class AtendimentoConfigRules
    {
        public const int MaxMensagem = 1000;

        /// <summary>Normaliza e valida o corpo do PUT. O modo "ia" so existe na etapa 2 (modulo de IA).</summary>
        public static UpdateAtendimentoConfigRequest Validate(UpdateAtendimentoConfigRequest? request, bool iaAvailable = false)
        {
            if (request == null) throw new DeliveryDomainException(400, "INVALID_REQUEST", "Corpo da requisicao obrigatorio.");

            var modo = (request.Modo ?? AtendimentoModos.Humano).Trim().ToLowerInvariant();
            if (modo != AtendimentoModos.Humano && modo != AtendimentoModos.Ia)
            {
                throw new DeliveryDomainException(422, "INVALID_MODE", "O modo de atendimento deve ser 'humano' ou 'ia'.");
            }
            if (modo == AtendimentoModos.Ia && !iaAvailable)
            {
                throw new DeliveryDomainException(422, "IA_NOT_AVAILABLE",
                    "O atendimento com IA ainda nao esta disponivel. Use o modo 'humano'.");
            }

            return new UpdateAtendimentoConfigRequest
            {
                Modo = modo,
                SaudacaoHumano = Clean(request.SaudacaoHumano, MaxMensagem, "saudacaoHumano"),
                MensagemForaHorario = Clean(request.MensagemForaHorario, MaxMensagem, "mensagemForaHorario"),
                Mensagens = MensagensDoAtendimento.Normalizar(request.Mensagens, MaxMensagem)
            };
        }

        /// <summary>A loja esta aberta agora? Sem horario cadastrado (nulo), sempre aberta; horario sem nenhum dia aberto, fechada. Aceita virada de meia-noite.</summary>
        public static bool IsOpen(HorarioAtendimentoDto? horario, DateTime localNow)
        {
            if (horario == null) return true;
            var especial = horario.Especiais?.FirstOrDefault(e => e.Data == DateOnly.FromDateTime(localNow));
            if (especial != null)
                return !especial.Fechado && TimeSpan.TryParse(especial.Abre, CultureInfo.InvariantCulture, out var inicio)
                    && TimeSpan.TryParse(especial.Fecha, CultureInfo.InvariantCulture, out var fim)
                    && localNow.TimeOfDay >= inicio && localNow.TimeOfDay < fim;
            if (horario.SemHorarioSemanal) return true;
            if (horario.Dias == null || horario.Dias.Count == 0) return false;
            var today = (int)localNow.DayOfWeek;
            var yesterday = (today + 6) % 7;
            var time = localNow.TimeOfDay;

            foreach (var dia in horario.Dias)
            {
                var abre = TimeSpan.Parse(dia.Abre, CultureInfo.InvariantCulture);
                var fecha = TimeSpan.Parse(dia.Fecha, CultureInfo.InvariantCulture);
                if (abre < fecha)
                {
                    if (dia.Dia == today && time >= abre && time < fecha) return true;
                }
                else
                {
                    // Vira a noite: abre hoje ate a meia-noite; a madrugada e do dia anterior.
                    if (dia.Dia == today && time >= abre) return true;
                    if (dia.Dia == yesterday && time < fecha && horario.Especiais?.Any(e => e.Data == DateOnly.FromDateTime(localNow.AddDays(-1))) != true) return true;
                }
            }
            return false;
        }

        /// <summary>Hora local (America/Sao_Paulo) de um instante UTC: o horario de atendimento e sempre o da loja no Brasil.</summary>
        public static DateTime ParaHorarioLocal(DateTime utc)
        {
            var instante = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
            try
            {
                return TimeZoneInfo.ConvertTimeFromUtc(instante, TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo"));
            }
            catch (TimeZoneNotFoundException)
            {
                return instante.AddHours(-3);
            }
        }

        private static readonly string[] NomesDosDias = { "domingo", "segunda", "terça", "quarta", "quinta", "sexta", "sábado" };

        /// <summary>"hoje às 18:00", "amanhã às 11:00" ou "quarta às 11:00": a proxima abertura. Nulo sem horario ou sem dia marcado.</summary>
        public static string? ProximaAbertura(HorarioAtendimentoDto? horario, DateTime localNow)
        {
            if (horario == null) return null;

            DateTime? melhor = null;
            var melhorOffset = 0;
            for (var offset = 0; offset <= 7; offset++)
            {
                var data = localNow.Date.AddDays(offset);
                var especial = horario.Especiais?.FirstOrDefault(e => e.Data == DateOnly.FromDateTime(data));
                var aberturas = especial != null
                    ? (!especial.Fechado && TimeSpan.TryParse(especial.Abre, CultureInfo.InvariantCulture, out var abreEspecial) ? new[] { abreEspecial } : Array.Empty<TimeSpan>())
                    : horario.SemHorarioSemanal ? new[] { TimeSpan.Zero }
                    : horario.Dias.Where(d => d.Dia == (int)data.DayOfWeek).Select(d => TimeSpan.Parse(d.Abre, CultureInfo.InvariantCulture));
                foreach (var abre in aberturas)
                {
                    var candidato = data + abre;
                    if (candidato > localNow && (melhor == null || candidato < melhor))
                    {
                        melhor = candidato;
                        melhorOffset = offset;
                    }
                }
            }

            if (melhor == null) return null;
            var hora = melhor.Value.ToString("HH:mm", CultureInfo.InvariantCulture);
            return melhorOffset switch
            {
                0 => $"hoje às {hora}",
                1 => $"amanhã às {hora}",
                _ => $"{NomesDosDias[(int)melhor.Value.DayOfWeek]} às {hora}"
            };
        }

        internal static string? Clean(string? value, int max, string field)
        {
            var trimmed = value?.Trim();
            if (string.IsNullOrEmpty(trimmed)) return null;
            if (trimmed.Length > max) throw new DeliveryDomainException(422, "INVALID_REQUEST", $"{field} aceita no maximo {max} caracteres.");
            return trimmed;
        }
    }

    /// <summary>Se a loja aceita pedido agora, e se nao, por que (para a tela e o bot explicarem).</summary>
    public sealed record SituacaoPedidos(bool Aberto, string? Motivo, string? AbreEm)
    {
        public static readonly SituacaoPedidos Aberta = new(true, null, null);
    }

    /// <summary>
    /// Pedido so entra com a loja aberta: o interruptor "pausar pedidos" (aceita_pedidos) e o horario de atendimento valem
    /// juntos. Uma unica regra, usada pelo cardapio web (que recusa) e pelo bot (que explica).
    /// </summary>
    public static class PedidosAbertosRules
    {
        public const string Pausado = "pausado";
        public const string ForaDoHorario = "fora_horario";

        public static SituacaoPedidos Avaliar(bool aceitaPedidos, HorarioAtendimentoDto? horario, DateTime localNow)
        {
            if (!aceitaPedidos) return new SituacaoPedidos(false, Pausado, null);
            if (AtendimentoConfigRules.IsOpen(horario, localNow)) return SituacaoPedidos.Aberta;
            return new SituacaoPedidos(false, ForaDoHorario, AtendimentoConfigRules.ProximaAbertura(horario, localNow));
        }

        public static string Mensagem(SituacaoPedidos situacao)
        {
            if (situacao.Aberto) return string.Empty;
            if (situacao.Motivo == Pausado) return "A loja pausou os pedidos por enquanto. Tente novamente em instantes.";
            return string.IsNullOrWhiteSpace(situacao.AbreEm)
                ? "A loja está fechada agora e não está aceitando pedidos."
                : $"A loja está fechada agora. Abrimos {situacao.AbreEm}.";
        }
    }

    public static class QuickReplyRules
    {
        public const int MaxTitulo = 60;
        public const int MaxTexto = 1000;
        public const int MaxAtalho = 20;
        private static readonly Regex Atalho = new(@"^[a-z0-9_-]+$", RegexOptions.Compiled);

        public static SalvarRespostaRapidaRequest Normalize(SalvarRespostaRapidaRequest? request)
        {
            if (request == null) throw new DeliveryDomainException(400, "INVALID_REQUEST", "Corpo da requisicao obrigatorio.");
            var titulo = request.Titulo?.Trim() ?? string.Empty;
            var texto = request.Texto?.Trim() ?? string.Empty;
            if (titulo.Length == 0 || titulo.Length > MaxTitulo)
            {
                throw new DeliveryDomainException(422, "INVALID_REQUEST", $"O titulo e obrigatorio e aceita no maximo {MaxTitulo} caracteres.");
            }
            if (texto.Length == 0 || texto.Length > MaxTexto)
            {
                throw new DeliveryDomainException(422, "INVALID_REQUEST", $"O texto e obrigatorio e aceita no maximo {MaxTexto} caracteres.");
            }

            var atalho = request.Atalho?.Trim().TrimStart('/').ToLowerInvariant();
            if (string.IsNullOrEmpty(atalho)) atalho = null;
            else if (atalho.Length > MaxAtalho || !Atalho.IsMatch(atalho))
            {
                throw new DeliveryDomainException(422, "INVALID_REQUEST",
                    $"O atalho aceita letras minusculas, numeros, - e _ (ate {MaxAtalho} caracteres).");
            }

            var unknown = QuickReplyRenderer.FindUnknownVariables(texto);
            if (unknown.Count > 0)
            {
                throw new DeliveryDomainException(422, "UNKNOWN_VARIABLE",
                    $"Variavel desconhecida: {string.Join(", ", unknown.Select(v => "{" + v + "}"))}. Use: {string.Join(", ", QuickReplyRenderer.Variables.Select(v => "{" + v + "}"))}.");
            }

            return new SalvarRespostaRapidaRequest { Titulo = titulo, Atalho = atalho, Texto = texto, Ordem = Math.Clamp(request.Ordem, 0, 1000), Ativo = request.Ativo };
        }
    }

    /// <summary>Preenche as variaveis de uma resposta rapida com os dados do pedido.</summary>
    public static class QuickReplyRenderer
    {
        public static readonly string[] Variables = { "numero", "cliente", "motoboy", "previsao", "total", "loja" };
        private static readonly Regex Token = new(@"\{([a-zA-Z_]+)\}", RegexOptions.Compiled);

        public static IReadOnlyList<string> FindUnknownVariables(string text) =>
            Token.Matches(text).Select(m => m.Groups[1].Value.ToLowerInvariant())
                .Where(v => !Variables.Contains(v)).Distinct().ToList();

        /// <summary>
        /// Variavel sem valor (ex.: pedido ainda sem motoboy) NAO e trocada por vazio: fica como esta e
        /// volta em <c>Pendentes</c>, para o atendente decidir antes de enviar.
        /// </summary>
        public static RenderRespostaResult Render(string text, IReadOnlyDictionary<string, string?> values)
        {
            var pendentes = new List<string>();
            var rendered = Token.Replace(text, match =>
            {
                var key = match.Groups[1].Value.ToLowerInvariant();
                if (values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)) return value!;
                if (!pendentes.Contains(key)) pendentes.Add(key);
                return match.Value;
            });
            return new RenderRespostaResult { Texto = rendered, Pendentes = pendentes };
        }

        public static string FormatMoney(decimal? value) =>
            value.HasValue ? "R$ " + value.Value.ToString("N2", new CultureInfo("pt-BR")) : string.Empty;

        public static string FormatTime(DateTime? value) => value.HasValue ? value.Value.ToString("HH:mm", CultureInfo.InvariantCulture) : string.Empty;
    }

    /// <summary>
    /// A loja consegue mandar mensagem para o cliente deste pedido? O unico canal e o WhatsApp da loja, e fora da
    /// janela de 24h so vale template aprovado (que ainda nao existe). Entao ficam de fora: pedido do iFood (la o
    /// cliente fala pelo app do iFood, mesmo que o telefone bata com um contato), pedido de balcao/telefone de quem
    /// nunca escreveu no WhatsApp e cliente cuja janela ja fechou.
    /// </summary>
    public static class ClienteCanalRules
    {
        public const string Ifood = "ifood";
        public const string SemConversa = "sem_conversa";
        public const string NuncaEscreveu = "nunca_escreveu";
        public const string JanelaFechada = "janela_fechada";

        public static (bool PodeReceber, string? Motivo) Evaluate(string? origem, Guid? conversaId, DateTimeOffset? janelaFimUtc, DateTimeOffset now)
        {
            if (string.Equals(origem?.Trim(), APIBack.Model.Delivery.PedidoOrigem.Ifood, StringComparison.OrdinalIgnoreCase)) return (false, Ifood);
            if (!conversaId.HasValue) return (false, SemConversa);
            if (!janelaFimUtc.HasValue) return (false, NuncaEscreveu);
            return janelaFimUtc.Value > now ? (true, null) : (false, JanelaFechada);
        }

        public static string Explain(string? motivo) => motivo switch
        {
            Ifood => "Pedido do iFood: a loja nao tem canal para falar com este cliente.",
            SemConversa => "Este cliente nao tem conversa no WhatsApp da loja.",
            NuncaEscreveu => "Este cliente nunca escreveu no WhatsApp da loja.",
            JanelaFechada => "A janela de 24h do WhatsApp esta fechada para este cliente.",
            _ => "Nao e possivel enviar mensagem para este cliente agora."
        };
    }

    /// <summary>Telefone em varias formas ("(34) 99123-0001", "+5534991230001", "34991230001") comparado pela chave dos 11 ultimos digitos.</summary>
    public static class PhoneKey
    {
        public static string? From(string? phone)
        {
            if (string.IsNullOrWhiteSpace(phone)) return null;
            var digits = new string(phone.Where(char.IsDigit).ToArray());
            if (digits.Length < 8) return null;
            return digits.Length > 11 ? digits[^11..] : digits;
        }

        /// <summary>E.164 brasileiro a partir do telefone digitado; null quando nao ha digitos suficientes.</summary>
        public static string? ToE164(string? phone)
        {
            if (string.IsNullOrWhiteSpace(phone)) return null;
            var digits = new string(phone.Where(char.IsDigit).ToArray());
            if (digits.StartsWith("55") && digits.Length is 12 or 13) return "+" + digits;
            if (digits.Length is 10 or 11) return "+55" + digits;
            return null;
        }
    }

    public static class MotoboyMessageRules
    {
        public const int MaxBody = 500;

        /// <summary>Atalhos de mensagem do atendente para o motoboy (e do motoboy para o atendente).</summary>
        public static readonly IReadOnlyList<(string Key, string Text)> OperatorShortcuts = new List<(string, string)>
        {
            ("cliente_nao_atende", "O cliente nao esta atendendo. Tente ligar ou aguarde alguns minutos."),
            ("confira_endereco", "Confira o endereco de entrega com o cliente antes de sair."),
            ("volte_loja", "Volte para a loja, por favor."),
            ("pedido_prioridade", "Este pedido e prioridade: entregue primeiro se puder."),
        };

        public static readonly IReadOnlyList<(string Key, string Text)> MotoboyShortcuts = new List<(string, string)>
        {
            ("cliente_nao_atende", "O cliente nao atende."),
            ("portao_trancado", "Portao trancado, aguardando o cliente."),
            ("endereco_nao_encontrado", "Nao encontrei o endereco."),
            ("atrasado", "Vou me atrasar um pouco."),
        };

        public static (string Body, string? QuickKey) Normalize(string? body, string? quickKey, bool operatorSide)
        {
            var key = quickKey?.Trim().ToLowerInvariant();
            var catalog = operatorSide ? OperatorShortcuts : MotoboyShortcuts;
            if (!string.IsNullOrEmpty(key))
            {
                var shortcut = catalog.FirstOrDefault(s => s.Key == key);
                if (shortcut.Key == null) throw new DeliveryDomainException(422, "INVALID_SHORTCUT", "Atalho de mensagem desconhecido.");
                var text = string.IsNullOrWhiteSpace(body) ? shortcut.Text : body!.Trim();
                return (Validate(text), key);
            }
            return (Validate(body), null);
        }

        private static string Validate(string? body)
        {
            var text = body?.Trim() ?? string.Empty;
            if (text.Length == 0 || text.Length > MaxBody)
            {
                throw new DeliveryDomainException(422, "INVALID_MESSAGE", $"A mensagem e obrigatoria e aceita no maximo {MaxBody} caracteres.");
            }
            return text;
        }
    }
}
