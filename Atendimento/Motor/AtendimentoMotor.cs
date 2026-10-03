using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace APIBack.Atendimento.Motor
{
    /// <summary>
    /// Decide como o atendimento automatico responde a uma mensagem. Generico para qualquer loja: o que muda de uma loja
    /// para outra sao os servicos do numero que recebeu a mensagem, e cada servico traz o proprio fluxo (IFluxoDeServico).
    /// Tipo novo de estabelecimento = fluxo novo registrado; o motor nao muda. Sem IA: botoes, numeros e palavras-chave,
    /// com resultado previsivel. A ordem das regras e a documentacao do comportamento (cada uma vira "regra=" no log).
    /// </summary>
    public sealed class AtendimentoMotor
    {
        public const string BotaoMenuAtendente = "menu_atendente";
        public const string PrefixoBotaoServico = "svc_";
        public const string TextoPadraoAtendente = "Certo! Já avisei a nossa equipe e em instantes alguém continua por aqui.";
        private const string PassoMenu = "menu";
        private static readonly TimeSpan IntervaloAvisoForaDoHorario = TimeSpan.FromHours(6);

        private readonly IReadOnlyDictionary<string, IFluxoDeServico> _fluxos;

        public AtendimentoMotor(IEnumerable<IFluxoDeServico> fluxos)
        {
            _fluxos = fluxos.ToDictionary(f => f.Servico, StringComparer.OrdinalIgnoreCase);
        }

        public ResultadoMotor Decidir(EntradaMotor entrada, EstadoFluxo? atual, DateTime agoraUtc)
        {
            var estado = atual ?? new EstadoFluxo();
            var texto = Normalizar(entrada.Texto);
            var acoes = new List<AcaoMotor>();
            var hibrido = entrada.ModoCanal == ModoAtendimento.Hibrido;

            // Servicos que este numero atende E que o sistema sabe tratar (ordem estavel pelo nome do servico).
            var servicos = entrada.Servicos
                .Where(s => _fluxos.ContainsKey(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToList();

            AvisarForaDoHorario(entrada, estado, agoraUtc, acoes);

            // 1) Pedir atendente (so quando o numero oferece atendimento humano).
            if (entrada.Interpretado == BotaoMenuAtendente || PedeAtendente(texto))
            {
                if (hibrido)
                {
                    acoes.Add(new AcaoResponder(entrada.Escolher(entrada.Textos?.Atendente, TextoPadraoAtendente)));
                    acoes.Add(new AcaoChamarAtendente("pedido do cliente"));
                    return Finalizar(acoes, Limpo(estado), "base", "atendente", "comando:atendente");
                }

                acoes.Add(new AcaoResponder("Por aqui eu consigo te ajudar com as opções abaixo."));
                return Finalizar(MaisMenu(acoes, entrada, servicos, estado, saudar: false), estado, "base", PassoMenu, "comando:atendente_indisponivel");
            }

            // 2) Voltar ao menu.
            if (texto is "menu" or "inicio" or "voltar" or "0")
            {
                var limpo = Limpo(estado);
                return Finalizar(MaisMenu(acoes, entrada, servicos, limpo, saudar: false), limpo, "base", PassoMenu, "comando:menu");
            }

            // 3) Cliente escolheu um servico: botao tocado ou numero do menu.
            var escolhido = ServicoEscolhido(entrada.Interpretado, texto, estado, servicos, hibrido, out var escolheuAtendente);
            if (escolheuAtendente)
            {
                acoes.Add(new AcaoResponder(entrada.Escolher(entrada.Textos?.Atendente, TextoPadraoAtendente)));
                acoes.Add(new AcaoChamarAtendente("pedido do cliente (menu)"));
                return Finalizar(acoes, Limpo(estado), "base", "atendente", "menu:atendente");
            }

            if (escolhido != null)
            {
                return Executar(_fluxos[escolhido].Iniciar(entrada), escolhido, acoes, estado, entrada, "menu:servico");
            }

            // 4) Conversa no meio de um fluxo: o fluxo decide.
            if (estado.Servico != null && estado.Passo != null && estado.Passo != PassoMenu && _fluxos.TryGetValue(estado.Servico, out var emAndamento))
            {
                return Executar(emAndamento.Continuar(entrada, estado), estado.Servico, acoes, estado, entrada, "fluxo:continuar");
            }

            // 5) Palavra-chave de um servico do numero ("cardapio", "agendar"...).
            var porPalavra = servicos.FirstOrDefault(s => _fluxos[s].Palavras.Any(p => texto.Contains(p, StringComparison.Ordinal)));
            if (porPalavra != null)
            {
                return Executar(_fluxos[porPalavra].Iniciar(entrada), porPalavra, acoes, estado, entrada, $"gatilho:{porPalavra}");
            }

            // 6) Primeira conversa ou texto solto: depende de quantos servicos o numero atende.
            switch (servicos.Count)
            {
                case 0:
                    acoes.Add(new AcaoResponder(Saudar(entrada, entrada.Escolher(entrada.Textos?.SemServico, "No momento não tenho um atendimento automático por aqui. Vou chamar alguém da equipe."))));
                    acoes.Add(new AcaoChamarAtendente("numero sem servico ligado"));
                    return Finalizar(acoes, Limpo(estado), "base", "sem_servico", "numero_sem_servico");
                case 1:
                    return Executar(_fluxos[servicos[0]].Iniciar(entrada), servicos[0], acoes, estado, entrada, "servico_unico");
                default:
                    var semFluxo = Limpo(estado);
                    return Finalizar(MaisMenu(acoes, entrada, servicos, semFluxo, saudar: entrada.PrimeiraMensagem), semFluxo, "base", PassoMenu, $"menu:{servicos.Count}_servicos");
            }
        }

        // ---- fluxo de um servico -----------------------------------------------------------------------------------

        private ResultadoMotor Executar(
            ResultadoFluxo resultado, string servico, List<AcaoMotor> acoes, EstadoFluxo estado, EntradaMotor entrada, string regraDeEntrada)
        {
            var respostas = resultado.Acoes.ToList();
            if (entrada.PrimeiraMensagem && respostas.Count > 0 && respostas[0] is AcaoResponder primeira)
            {
                respostas[0] = new AcaoResponder(Saudar(entrada, primeira.Mensagem));
            }

            acoes.AddRange(respostas);

            var novo = new EstadoFluxo
            {
                ForaDoHorarioAvisadoEm = estado.ForaDoHorarioAvisadoEm,
                Servico = resultado.ProximoPasso == null ? null : servico,
                Passo = resultado.ProximoPasso,
                Dados = resultado.Dados == null ? new Dictionary<string, string>() : new Dictionary<string, string>(resultado.Dados)
            };

            var temAtendente = acoes.Any(a => a is AcaoChamarAtendente);
            if (temAtendente) novo = Limpo(novo);

            return new ResultadoMotor(acoes, novo, servico, resultado.ProximoPasso ?? "fim", $"{regraDeEntrada}>{resultado.Regra}");
        }

        // ---- menu ---------------------------------------------------------------------------------------------------

        private List<AcaoMotor> MaisMenu(List<AcaoMotor> acoes, EntradaMotor entrada, IReadOnlyList<string> servicos, EstadoFluxo estado, bool saudar)
        {
            var hibrido = entrada.ModoCanal == ModoAtendimento.Hibrido;
            var opcoes = servicos.Select(s => new Botao(PrefixoBotaoServico + s, _fluxos[s].TituloNoMenu)).ToList();
            if (hibrido) opcoes.Add(new Botao(BotaoMenuAtendente, "Falar com atendente"));

            var cabecalho = entrada.Escolher(entrada.Textos?.Menu, "Como posso te ajudar?");
            if (saudar) cabecalho = Saudar(entrada, cabecalho);

            // Ate 3 opcoes cabem em botoes do WhatsApp; com mais, vira lista numerada ("responda 1, 2...").
            if (opcoes.Count <= 3)
            {
                acoes.Add(new AcaoBotoes(cabecalho, opcoes));
            }
            else
            {
                var lista = string.Join("\n", opcoes.Select((o, i) => $"{i + 1} - {o.Titulo}"));
                acoes.Add(new AcaoResponder($"{cabecalho}\n{lista}\nResponda com o número da opção."));
            }

            estado.Servico = null;
            estado.Passo = PassoMenu;
            estado.Dados["menu"] = string.Join(",", opcoes.Select(o => o.Id));
            return acoes;
        }

        private static string? ServicoEscolhido(
            string? interpretado, string texto, EstadoFluxo estado, IReadOnlyList<string> servicos, bool hibrido, out bool atendente)
        {
            atendente = false;

            if (!string.IsNullOrWhiteSpace(interpretado) && interpretado.StartsWith(PrefixoBotaoServico, StringComparison.Ordinal))
            {
                var codigo = interpretado[PrefixoBotaoServico.Length..];
                return servicos.FirstOrDefault(s => string.Equals(s, codigo, StringComparison.OrdinalIgnoreCase));
            }

            // Resposta numerica ao menu em lista ("2").
            if (estado.Passo == PassoMenu && estado.Dados.TryGetValue("menu", out var menu) && int.TryParse(texto, out var numero))
            {
                var ids = menu.Split(',', StringSplitOptions.RemoveEmptyEntries);
                if (numero >= 1 && numero <= ids.Length)
                {
                    var id = ids[numero - 1];
                    if (id == BotaoMenuAtendente)
                    {
                        atendente = hibrido;
                        return null;
                    }

                    return servicos.FirstOrDefault(s => PrefixoBotaoServico + s == id);
                }
            }

            return null;
        }

        // ---- fora do horario ------------------------------------------------------------------------------------------

        private static void AvisarForaDoHorario(EntradaMotor entrada, EstadoFluxo estado, DateTime agoraUtc, List<AcaoMotor> acoes)
        {
            if (!entrada.ForaDoHorario) return;
            if (estado.ForaDoHorarioAvisadoEm.HasValue && agoraUtc - estado.ForaDoHorarioAvisadoEm.Value < IntervaloAvisoForaDoHorario) return;

            acoes.Add(new AcaoResponder(string.IsNullOrWhiteSpace(entrada.MensagemForaDoHorario)
                ? "No momento nosso atendimento está fechado, mas você pode fazer o seu pedido pelo cardápio a qualquer hora."
                : entrada.MensagemForaDoHorario!));
            estado.ForaDoHorarioAvisadoEm = agoraUtc;
        }

        // ---- utilidades ---------------------------------------------------------------------------------------------

        private static ResultadoMotor Finalizar(List<AcaoMotor> acoes, EstadoFluxo estado, string fluxo, string passo, string regra) =>
            new(acoes, estado, fluxo, passo, regra);

        private static EstadoFluxo Limpo(EstadoFluxo origem) => new() { ForaDoHorarioAvisadoEm = origem.ForaDoHorarioAvisadoEm };

        private static string Saudar(EntradaMotor entrada, string corpo)
        {
            var saudacao = string.IsNullOrWhiteSpace(entrada.Saudacao) ? $"Olá! Aqui é a {entrada.NomeLoja}." : entrada.Saudacao!.Trim();
            return $"{saudacao}\n{corpo}";
        }

        private static bool PedeAtendente(string texto) =>
            texto.Contains("atendente", StringComparison.Ordinal)
            || texto is "humano" or "pessoa" or "atendimento"
            || texto.Contains("falar com alguem", StringComparison.Ordinal)
            || texto.Contains("falar com uma pessoa", StringComparison.Ordinal)
            || texto.Contains("falar com humano", StringComparison.Ordinal);

        /// <summary>Minusculas, sem acento, sem pontuacao nas pontas e com espacos simples: base de toda comparacao de texto.</summary>
        public static string Normalizar(string? texto)
        {
            if (string.IsNullOrWhiteSpace(texto)) return string.Empty;

            var decomposto = texto.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
            var limpo = new StringBuilder(decomposto.Length);
            foreach (var c in decomposto)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) limpo.Append(c);
            }

            var partes = limpo.ToString().Trim(' ', '.', ',', '!', '?', ';', ':').Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return string.Join(' ', partes);
        }
    }
}
