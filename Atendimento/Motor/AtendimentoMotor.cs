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

            if (entrada.ModoCanal == ModoAtendimento.Humano || estado.AguardandoEquipe)
                return Finalizar(acoes, estado, "base", "humano", "conversa:humano");

            if (texto is "obrigado" or "obrigada" or "valeu" or "muito obrigado" or "muito obrigada" or "ok obrigado" or "tchau" or "boa noite obrigado")
            {
                estado.NaoEntendidas = 0;
                return Finalizar(new() { new AcaoResponder("Por nada! Se precisar de algo, é só escrever por aqui.") }, estado, "base", "fim", "comando:agradecimento");
            }

            // O cardapio fechado ja explica a situacao na propria resposta; evita dois avisos iguais.
            if (entrada.Pedidos is not { Aberto: false }) AvisarForaDoHorario(entrada, estado, agoraUtc, acoes);

            var pedidos = entrada.PedidosCliente ?? Array.Empty<PedidoDoAtendimento>();
            var permitePedido = servicos.Contains("delivery") || servicos.Contains("cardapio_web");
            if (entrada.TipoMensagem is not (null or "text" or "interactive" or "button"))
                return EncaminharOuOrientar(entrada, estado, acoes,
                    "Recebi sua mensagem, mas o atendimento automático ainda não interpreta áudio, fotos, documentos ou localização. Se puder, descreva em texto o que precisa.", "midia:" + entrada.TipoMensagem);

            if (estado.Passo == "motivo_suporte" && texto is not ("menu" or "inicio" or "voltar" or "0"))
                return EncaminharOuOrientar(entrada, estado, acoes, "Sua solicitação foi registrada para a equipe.", "suporte:" + ContextoSuporte(pedidos, entrada.Texto));

            if (EhSuporte(texto) || entrada.Interpretado == "ajuda_pedido")
            {
                if (entrada.Interpretado == "ajuda_pedido" || texto is "cancelar" or "reclamacao" or "problema")
                {
                    estado.Servico = null; estado.Passo = "motivo_suporte"; estado.NaoEntendidas = 0;
                    acoes.Add(new AcaoResponder("Conte o que aconteceu ou o que você deseja alterar no pedido. Cancelamentos e alterações precisam da confirmação da equipe."));
                    return Finalizar(acoes, estado, "base", "motivo_suporte", "suporte:coletar_motivo");
                }
                return EncaminharOuOrientar(entrada, estado, acoes, "Sua solicitação foi registrada para a equipe.", "suporte:" + ContextoSuporte(pedidos, entrada.Texto));
            }

            if (entrada.Interpretado == "info_loja" || ContemTermo(texto, "horario") || ContemTermo(texto, "aberto") || ContemTermo(texto, "fechado"))
            {
                var info = entrada.Pedidos is { Aberto: false } situacao
                    ? situacao.Motivo == APIBack.Service.PedidosAbertosRules.Pausado ? "A loja pausou os pedidos no momento."
                        : string.IsNullOrWhiteSpace(situacao.AbreEm) ? "A loja está fechada no momento." : $"A loja está fechada. Abrimos {situacao.AbreEm}."
                    : "A loja está recebendo pedidos agora.";
                acoes.Add(new AcaoResponder(info + (entrada.CardapioUrl == null ? "" : $"\nCardápio: {entrada.CardapioUrl}")));
                return Finalizar(acoes, Limpo(estado), "base", "informacoes", "comando:informacoes");
            }

            var selecionado = entrada.Interpretado?.StartsWith("pedido:", StringComparison.Ordinal) == true
                ? pedidos.FirstOrDefault(p => "pedido:" + p.Chave == entrada.Interpretado) : null;
            if (estado.Passo == "escolher_pedido" && int.TryParse(texto, out var indice)
                && estado.Dados.TryGetValue("pedidos", out var ids))
            {
                var chaves = ids.Split('|');
                if (indice >= 1 && indice <= chaves.Length) selecionado = pedidos.FirstOrDefault(p => p.Chave == chaves[indice - 1]);
            }
            if (permitePedido && (selecionado != null || EhConsultaPedido(texto) || entrada.Interpretado is "meus_pedidos" or "svc_delivery"))
                return ResponderPedido(entrada, estado, acoes, pedidos, selecionado);

            if (entrada.Interpretado == "novo_pedido" && servicos.Contains("cardapio_web"))
                return Executar(_fluxos["cardapio_web"].Iniciar(entrada), "cardapio_web", acoes, estado, entrada, "menu:novo_pedido");

            if (entrada.PrimeiraMensagem && permitePedido && pedidos.Count > 0 && !EhCompra(texto) && !PedeAtendente(texto))
            {
                var ativo = pedidos.Any(p => p.Ativo);
                var opcoes = new List<Botao> { new("meus_pedidos", ativo ? "Acompanhar pedido" : "Pedido anterior") };
                if (servicos.Contains("cardapio_web")) opcoes.Add(new("novo_pedido", "Fazer novo pedido"));
                if (hibrido) opcoes.Add(new(BotaoMenuAtendente, "Falar com a equipe"));
                acoes.Add(new AcaoBotoes(Saudar(entrada, ativo ? "Você tem pedido em andamento. Como posso ajudar?" : "Encontrei seu pedido recente. Como posso ajudar?"), opcoes));
                return Finalizar(acoes, Limpo(estado), "base", "menu_contexto", "menu:pedido_recente");
            }

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
            if (estado.Servico != null && estado.Passo != null && estado.Passo != PassoMenu && servicos.Contains(estado.Servico) && _fluxos.TryGetValue(estado.Servico, out var emAndamento))
            {
                return Executar(emAndamento.Continuar(entrada, estado), estado.Servico, acoes, estado, entrada, "fluxo:continuar");
            }

            // 5) Palavra-chave de um servico do numero ("cardapio", "agendar"...).
            var porPalavra = servicos.FirstOrDefault(s => _fluxos[s].Palavras.Any(p => ContemTermo(texto, p)));
            if (porPalavra != null)
            {
                return Executar(_fluxos[porPalavra].Iniciar(entrada), porPalavra, acoes, estado, entrada, $"gatilho:{porPalavra}");
            }

            // 6) Primeira conversa ou texto solto: depende de quantos servicos o numero atende.
            if (!entrada.PrimeiraMensagem && estado.Passo != null && texto is not ("oi" or "ola" or "bom dia" or "boa tarde" or "boa noite"))
            {
                estado.NaoEntendidas++;
                if (estado.NaoEntendidas >= 2 && hibrido)
                    return EncaminharOuOrientar(entrada, estado, acoes, "Não consegui entender sua solicitação. A equipe vai continuar o atendimento.", "nao_entendido:duas_tentativas");
                acoes.Add(new AcaoResponder("Não entendi essa mensagem. Escolha uma opção abaixo ou escreva o que precisa, como cardápio ou meu pedido."));
                return Finalizar(MaisMenu(acoes, entrada, servicos, estado, false), estado, "base", PassoMenu, "entrada:nao_entendida");
            }
            switch (servicos.Count)
            {
                case 0:
                    acoes.Add(new AcaoResponder(Saudar(entrada, hibrido
                        ? entrada.Escolher(entrada.Textos?.SemServico, "No momento não tenho um atendimento automático por aqui. A equipe continuará por aqui quando disponível.")
                        : "Este número está sem opções automáticas disponíveis no momento. Tente novamente mais tarde.")));
                    if (hibrido) acoes.Add(new AcaoChamarAtendente("numero sem servico ligado"));
                    return Finalizar(acoes, Limpo(estado), "base", "sem_servico", "numero_sem_servico");
                case 1:
                    return Executar(_fluxos[servicos[0]].Iniciar(entrada), servicos[0], acoes, estado, entrada, "servico_unico");
                default:
                    var semFluxo = Limpo(estado); semFluxo.NaoEntendidas = estado.NaoEntendidas;
                    return Finalizar(MaisMenu(acoes, entrada, servicos, semFluxo, saudar: entrada.PrimeiraMensagem), semFluxo, "base", PassoMenu, $"menu:{servicos.Count}_servicos");
            }
        }

        // ---- fluxo de um servico -----------------------------------------------------------------------------------

        private ResultadoMotor Executar(
            ResultadoFluxo resultado, string servico, List<AcaoMotor> acoes, EstadoFluxo estado, EntradaMotor entrada, string regraDeEntrada)
        {
            var respostas = resultado.Acoes.ToList();
            if (entrada.ModoCanal != ModoAtendimento.Hibrido && respostas.Any(a => a is AcaoChamarAtendente))
                respostas = new() { new AcaoResponder(servico == "agendamento"
                    ? "O agendamento automático ainda não está disponível neste número."
                    : "Não consegui concluir esta solicitação automaticamente. Este número está configurado apenas para o bot; tente novamente mais tarde.") };
            if (entrada.ForaDoHorario && entrada.Pedidos is { Aberto: false } && servico != "cardapio_web")
                AvisarForaDoHorario(entrada, estado, DateTime.UtcNow, acoes);
            if (entrada.PrimeiraMensagem && respostas.Count > 0 && respostas[0] is AcaoResponder primeira)
            {
                respostas[0] = new AcaoResponder(Saudar(entrada, primeira.Mensagem));
            }

            acoes.AddRange(respostas);

            var novo = new EstadoFluxo
            {
                ForaDoHorarioAvisadoEm = estado.ForaDoHorarioAvisadoEm,
                Servico = resultado.ProximoPasso == null ? null : servico,
                Dados = resultado.Dados == null ? new Dictionary<string, string>() : new Dictionary<string, string>(resultado.Dados),
                Passo = resultado.ProximoPasso ?? "apos_resposta"
            };

            var temAtendente = acoes.Any(a => a is AcaoChamarAtendente);
            if (temAtendente) { novo = Limpo(novo); novo.AguardandoEquipe = true; }

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
                ? (string.IsNullOrWhiteSpace(entrada.Pedidos?.AbreEm)
                    ? "No momento estamos fechados."
                    : $"No momento estamos fechados. Abrimos {entrada.Pedidos!.AbreEm}.")
                : entrada.MensagemForaDoHorario!));
            estado.ForaDoHorarioAvisadoEm = agoraUtc;
        }

        // ---- utilidades ---------------------------------------------------------------------------------------------

        private static ResultadoMotor Finalizar(List<AcaoMotor> acoes, EstadoFluxo estado, string fluxo, string passo, string regra)
        {
            if (acoes.Any(a => a is AcaoChamarAtendente)) estado.AguardandoEquipe = true;
            return new(acoes, estado, fluxo, passo, regra);
        }

        private static EstadoFluxo Limpo(EstadoFluxo origem) => new() { ForaDoHorarioAvisadoEm = origem.ForaDoHorarioAvisadoEm };

        private static string Saudar(EntradaMotor entrada, string corpo)
        {
            var saudacao = string.IsNullOrWhiteSpace(entrada.Saudacao) ? $"Olá! Aqui é a {entrada.NomeLoja}." : entrada.Saudacao!.Trim();
            return $"{saudacao}\n{corpo}";
        }

        internal static bool ContemTermo(string texto, string termo) =>
            System.Text.RegularExpressions.Regex.IsMatch(texto, @"(?<![\p{L}\p{N}])" + System.Text.RegularExpressions.Regex.Escape(termo) + @"(?![\p{L}\p{N}])");

        private static bool EhCompra(string texto) => new[] { "cardapio", "novo pedido", "fazer pedido", "quero pedir", "fazer um pedido" }.Any(p => ContemTermo(texto, p));
        private static bool EhConsultaPedido(string texto) => new[] { "meu pedido", "onde esta", "status", "acompanhar", "demora", "atrasado", "atrasou", "nao chegou" }.Any(p => ContemTermo(texto, p));
        private static bool EhSuporte(string texto) => new[] { "cancelar", "cancelamento", "reclamacao", "reclamar", "pedido errado", "item errado", "veio errado", "faltou", "alterar", "trocar endereco", "problema" }.Any(p => ContemTermo(texto, p));
        private static string ContextoSuporte(IReadOnlyList<PedidoDoAtendimento> pedidos, string texto) =>
            "pedidos=" + string.Join(",", pedidos.Select(p => p.Numero)) + "; motivo=" + texto[..Math.Min(texto.Length, 400)];

        private static ResultadoMotor EncaminharOuOrientar(EntradaMotor entrada, EstadoFluxo estado, List<AcaoMotor> acoes, string mensagem, string motivo)
        {
            var novo = Limpo(estado);
            if (entrada.ModoCanal == ModoAtendimento.Hibrido)
            {
                novo.AguardandoEquipe = true;
                acoes.Add(new AcaoChamarAtendente(motivo));
                acoes.Add(new AcaoResponder(mensagem + "\n" + TextoPadraoAtendente));
            }
            else acoes.Add(new AcaoResponder(mensagem.Replace("Sua solicitação foi registrada para a equipe.", "Alterações e cancelamentos precisam ser confirmados pela loja.")
                + "\nEste número atende apenas automaticamente. Escreva menu para ver as opções disponíveis."));
            return Finalizar(acoes, novo, "base", "suporte", motivo);
        }

        private static ResultadoMotor ResponderPedido(EntradaMotor entrada, EstadoFluxo estado, List<AcaoMotor> acoes,
            IReadOnlyList<PedidoDoAtendimento> pedidos, PedidoDoAtendimento? escolhido)
        {
            var candidatos = pedidos.Where(p => p.Ativo).ToList();
            if (candidatos.Count == 0) candidatos = pedidos.ToList();
            if (escolhido == null && candidatos.Count > 1)
            {
                var novo = Limpo(estado); novo.Passo = "escolher_pedido";
                novo.Dados["pedidos"] = string.Join("|", candidatos.Select(p => p.Chave));
                var opcoes = candidatos.Select(p => new Botao("pedido:" + p.Chave, "Pedido " + p.Numero)).ToList();
                if (opcoes.Count <= 3) acoes.Add(new AcaoBotoes("Qual pedido você quer acompanhar?", opcoes));
                else acoes.Add(new AcaoResponder("Qual pedido você quer acompanhar? Responda com o número da opção:\n" + string.Join("\n", candidatos.Select((p, i) => $"{i + 1} - Pedido {p.Numero}"))));
                return Finalizar(acoes, novo, "delivery", "escolher_pedido", "pedido:selecionar");
            }
            escolhido ??= candidatos.FirstOrDefault();
            if (escolhido == null)
                return EncaminharOuOrientar(entrada, estado, acoes, "Não encontrei pedido ativo ou recente vinculado a este número nesta loja.", "pedido:nao_encontrado");
            var texto = $"Pedido #{escolhido.Numero}: {escolhido.Status}.";
            if (!string.IsNullOrWhiteSpace(escolhido.Previsao)) texto += $"\nPrevisão informada pela loja: {escolhido.Previsao}.";
            if (!string.IsNullOrWhiteSpace(escolhido.Link)) texto += $"\nAcompanhe: {escolhido.Link}";
            acoes.Add(new AcaoResponder(texto));
            if (entrada.ModoCanal == ModoAtendimento.Hibrido)
                acoes.Add(new AcaoBotoes("Precisa de ajuda com esse pedido?", new[] { new Botao("ajuda_pedido", "Preciso de ajuda") }));
            var fim = Limpo(estado); fim.Passo = "apos_resposta";
            return Finalizar(acoes, fim, "delivery", "status", "pedido:status");
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
