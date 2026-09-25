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

    public static class AtendimentoConfigRules
    {
        public const int MaxMensagem = 1000;
        private static readonly Regex Hora = new(@"^([01]\d|2[0-3]):[0-5]\d$", RegexOptions.Compiled);

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

            var horario = request.HorarioAtendimento;
            if (horario != null)
            {
                var dias = horario.Dias ?? new List<HorarioDiaDto>();
                if (dias.Any(d => d.Dia is < 0 or > 6) || dias.Select(d => d.Dia).Distinct().Count() != dias.Count)
                {
                    throw new DeliveryDomainException(422, "INVALID_SCHEDULE", "Horario invalido: cada dia (0 a 6) so pode aparecer uma vez.");
                }
                if (dias.Any(d => !Hora.IsMatch(d.Abre ?? string.Empty) || !Hora.IsMatch(d.Fecha ?? string.Empty) || d.Abre == d.Fecha))
                {
                    throw new DeliveryDomainException(422, "INVALID_SCHEDULE", "Horario invalido: use HH:mm e abertura diferente do fechamento.");
                }
                horario = new HorarioAtendimentoDto { Dias = dias.OrderBy(d => d.Dia).ToList() };
            }

            return new UpdateAtendimentoConfigRequest
            {
                Modo = modo,
                SaudacaoHumano = Clean(request.SaudacaoHumano, MaxMensagem, "saudacaoHumano"),
                MensagemForaHorario = Clean(request.MensagemForaHorario, MaxMensagem, "mensagemForaHorario"),
                HorarioAtendimento = horario is { Dias.Count: > 0 } ? horario : null
            };
        }

        /// <summary>O atendimento esta aberto agora? Sem horario configurado, sempre aberto. Aceita virada de meia-noite.</summary>
        public static bool IsOpen(HorarioAtendimentoDto? horario, DateTime localNow)
        {
            if (horario?.Dias == null || horario.Dias.Count == 0) return true;
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
                    if (dia.Dia == yesterday && time < fecha) return true;
                }
            }
            return false;
        }

        internal static string? Clean(string? value, int max, string field)
        {
            var trimmed = value?.Trim();
            if (string.IsNullOrEmpty(trimmed)) return null;
            if (trimmed.Length > max) throw new DeliveryDomainException(422, "INVALID_REQUEST", $"{field} aceita no maximo {max} caracteres.");
            return trimmed;
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
