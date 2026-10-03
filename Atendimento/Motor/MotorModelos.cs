using System;
using System.Collections.Generic;
using APIBack.Service;

namespace APIBack.Atendimento.Motor
{
    public sealed record Botao(string Id, string Titulo);

    /// <summary>O que o atendimento automatico decide fazer. O motor so decide; quem envia e quem grava e o executor.</summary>
    public abstract record AcaoMotor;

    public sealed record AcaoResponder(string Mensagem) : AcaoMotor;

    /// <summary>Mensagem com botoes de resposta (ate 3, limite do WhatsApp).</summary>
    public sealed record AcaoBotoes(string Mensagem, IReadOnlyList<Botao> Opcoes) : AcaoMotor;

    /// <summary>Passa a conversa para a equipe (fila de atendimento humano).</summary>
    public sealed record AcaoChamarAtendente(string Motivo) : AcaoMotor;

    /// <summary>
    /// Textos que o dono da loja personalizou. Qualquer um nulo = texto padrao do sistema. Variaveis: {loja} e {link}.
    /// </summary>
    public sealed record TextosMotor(string? Menu, string? Cardapio, string? Atendente, string? Agendamento, string? SemServico, string? CardapioFechado = null)
    {
        public static string Renderizar(string modelo, string loja, string? link, string? abre = null) =>
            modelo.Replace("{loja}", loja, StringComparison.Ordinal)
                .Replace("{link}", link ?? string.Empty, StringComparison.Ordinal)
                .Replace("{abre}", abre ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>Tudo que o motor precisa saber de uma mensagem recebida. Montado pelo executor; o motor nao toca em banco.</summary>
    public sealed record EntradaMotor(
        Guid ConversaId,
        Guid LojaId,
        Guid CanalId,
        string NomeLoja,
        /// <summary>bot, humano ou hibrido (modo do numero). So "hibrido" oferece falar com atendente.</summary>
        string ModoCanal,
        /// <summary>Servicos que ESTE numero atende (ja filtrados pelos ativos da loja).</summary>
        IReadOnlyList<string> Servicos,
        string Texto,
        /// <summary>Id do botao ou item de lista que o cliente tocou, quando foi o caso.</summary>
        string? Interpretado,
        bool PrimeiraMensagem,
        bool ForaDoHorario,
        string? MensagemForaDoHorario,
        string? Saudacao,
        /// <summary>Link publico do cardapio da loja; nulo quando a URL base nao esta configurada.</summary>
        string? CardapioUrl,
        /// <summary>Textos personalizados da loja; nulo = todos os padroes.</summary>
        TextosMotor? Textos = null,
        /// <summary>A loja aceita pedido agora? Nulo = sim (nada configurado).</summary>
        SituacaoPedidos? Pedidos = null)
    {
        /// <summary>O texto escolhido pelo dono (com {loja} e {link} trocados) ou o padrao.</summary>
        public string Escolher(string? personalizado, string padrao) =>
            TextosMotor.Renderizar(string.IsNullOrWhiteSpace(personalizado) ? padrao : personalizado!, NomeLoja, CardapioUrl, Pedidos?.AbreEm);
    }

    /// <summary>Estado do atendimento automatico de uma conversa (persistido em conversas.fluxo_estado).</summary>
    public sealed class EstadoFluxo
    {
        public const int VersaoAtual = 1;

        public int Versao { get; set; } = VersaoAtual;
        /// <summary>Servico cujo fluxo esta em andamento; nulo = o cliente esta no menu / sem fluxo.</summary>
        public string? Servico { get; set; }
        public string? Passo { get; set; }
        public Dictionary<string, string> Dados { get; set; } = new();
        public DateTime? ForaDoHorarioAvisadoEm { get; set; }
    }

    public sealed record ResultadoFluxo(
        IReadOnlyList<AcaoMotor> Acoes,
        /// <summary>Proximo passo do fluxo; nulo = o fluxo terminou.</summary>
        string? ProximoPasso,
        string Regra,
        IReadOnlyDictionary<string, string>? Dados = null);

    public sealed record ResultadoMotor(IReadOnlyList<AcaoMotor> Acoes, EstadoFluxo Estado, string Fluxo, string Passo, string Regra);

    /// <summary>Fluxo de um servico (delivery, cardapio_web, agendamento...). Puro: sem banco e sem rede.</summary>
    public interface IFluxoDeServico
    {
        string Servico { get; }
        /// <summary>Texto do botao no menu.</summary>
        string TituloNoMenu { get; }
        /// <summary>Palavras que, ditas pelo cliente, levam direto a este fluxo (ja sem acento e em minusculas).</summary>
        IReadOnlyList<string> Palavras { get; }
        ResultadoFluxo Iniciar(EntradaMotor entrada);
        ResultadoFluxo Continuar(EntradaMotor entrada, EstadoFluxo estado);
    }
}
