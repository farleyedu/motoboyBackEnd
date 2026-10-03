using System.Collections.Generic;
using APIBack.Service;

namespace APIBack.Atendimento.Motor
{
    /// <summary>
    /// Cardapio web: o cliente recebe o link e faz o pedido la. O que acontece depois (codigo de confirmacao, aceite,
    /// recusa) nao e conversa: sao avisos que o delivery dispara, ja existentes.
    /// </summary>
    public sealed class FluxoCardapioWeb : IFluxoDeServico
    {
        public string Servico => "cardapio_web";
        public string TituloNoMenu => "Fazer pedido";
        public IReadOnlyList<string> Palavras { get; } = new[] { "cardapio", "pedir", "fazer pedido", "quero pedir", "fazer um pedido", "lanche", "comer" };

        public ResultadoFluxo Iniciar(EntradaMotor entrada)
        {
            if (string.IsNullOrWhiteSpace(entrada.CardapioUrl))
            {
                // Sem a URL publica configurada nao ha link para mandar: melhor passar para a equipe do que ficar mudo.
                return new ResultadoFluxo(
                    new AcaoMotor[]
                    {
                        new AcaoResponder("O nosso cardápio online ainda não está disponível por aqui. Vou chamar alguém da equipe para te ajudar."),
                        new AcaoChamarAtendente("cardapio sem link configurado")
                    },
                    ProximoPasso: null, Regra: "cardapio_sem_link");
            }

            if (entrada.Pedidos is { Aberto: false } fechada)
            {
                // Loja fechada ou com pedidos pausados: o cliente ainda pode ver o cardapio, mas o pedido nao entra.
                var padrao = fechada.Motivo == PedidosAbertosRules.Pausado
                    ? "No momento a loja pausou os pedidos. Você pode ver o cardápio aqui, mas só consegue pedir quando voltarmos:\n{link}"
                    : string.IsNullOrWhiteSpace(fechada.AbreEm)
                        ? "Estamos fechados agora e não estamos aceitando pedidos. Você pode ver o cardápio aqui:\n{link}"
                        : "Estamos fechados agora. Abrimos {abre}. Você pode ver o cardápio aqui:\n{link}";
                return new ResultadoFluxo(
                    new AcaoMotor[] { new AcaoResponder(entrada.Escolher(entrada.Textos?.CardapioFechado, padrao)) },
                    ProximoPasso: null, Regra: "cardapio_fechado_" + (fechada.Motivo ?? "fechado"));
            }

            return new ResultadoFluxo(
                new AcaoMotor[]
                {
                    new AcaoResponder(entrada.Escolher(
                        entrada.Textos?.Cardapio,
                        "Este é o nosso cardápio, é só escolher os itens e finalizar:\n{link}\n\nDepois de enviar o pedido eu confirmo por aqui mesmo."))
                },
                ProximoPasso: null, Regra: "link_do_cardapio");
        }

        public ResultadoFluxo Continuar(EntradaMotor entrada, EstadoFluxo estado) => Iniciar(entrada);
    }

    /// <summary>Delivery (acompanhar o pedido). Por ora so encaminha para a equipe; o status automatico vem depois.</summary>
    public sealed class FluxoDelivery : IFluxoDeServico
    {
        public string Servico => "delivery";
        public string TituloNoMenu => "Meu pedido";
        public IReadOnlyList<string> Palavras { get; } = new[] { "meu pedido", "onde esta", "status", "acompanhar", "demora", "atrasado" };

        public ResultadoFluxo Iniciar(EntradaMotor entrada) => new(
            new AcaoMotor[]
            {
                new AcaoResponder("Vou chamar alguém da equipe para falar sobre o seu pedido."),
                new AcaoChamarAtendente("duvida sobre pedido")
            },
            ProximoPasso: null, Regra: "encaminhar_pedido");

        public ResultadoFluxo Continuar(EntradaMotor entrada, EstadoFluxo estado) => Iniciar(entrada);
    }

    /// <summary>Agendamento: so o encaixe. Mostra que o servico existe e leva para a equipe; o fluxo real vem depois.</summary>
    public sealed class FluxoAgendamento : IFluxoDeServico
    {
        public string Servico => "agendamento";
        public string TituloNoMenu => "Agendar";
        public IReadOnlyList<string> Palavras { get; } = new[] { "agendar", "agendamento", "reservar", "reserva", "marcar horario" };

        public ResultadoFluxo Iniciar(EntradaMotor entrada) => new(
            new AcaoMotor[]
            {
                new AcaoResponder(entrada.Escolher(
                    entrada.Textos?.Agendamento,
                    "O agendamento por aqui estará disponível em breve. Vou chamar alguém da equipe para te ajudar agora.")),
                new AcaoChamarAtendente("agendamento (fluxo ainda nao implementado)")
            },
            ProximoPasso: null, Regra: "agendamento_em_breve");

        public ResultadoFluxo Continuar(EntradaMotor entrada, EstadoFluxo estado) => Iniciar(entrada);
    }
}
