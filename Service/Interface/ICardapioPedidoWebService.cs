using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.DTOs.Cardapio;
using APIBack.Model.Cardapio;

namespace APIBack.Service.Interface
{
    /// <summary>
    /// Confirmacao do pedido do cardapio web pelo WhatsApp e decisao do restaurante.
    /// Janela de 24h aberta: manda a mensagem e o pedido ja vai ao restaurante. Janela fechada: o cliente manda
    /// o codigo de 4 digitos pelo WhatsApp da loja. So depois disso o atendente ve o pedido e pode aceitar ou recusar.
    /// </summary>
    public interface ICardapioPedidoWebService
    {
        /// <summary>Escolhe o caminho (mensagem ou codigo) para um pedido recem-criado.</summary>
        Task<CardapioConfirmacaoDto> IniciarConfirmacaoAsync(CardapioPedidoPublico pedido, string nomeLoja);

        /// <summary>Acompanhamento publico; nulo quando o id nao existe.</summary>
        Task<CardapioPedidoPublicoStatusDto?> ObterStatusAsync(Guid id);

        /// <summary>Codigo novo para quem deixou o anterior vencer.</summary>
        Task<CardapioConfirmacaoDto> GerarNovoCodigoAsync(Guid id);

        /// <summary>
        /// Chamado pelo webhook com cada mensagem recebida. True quando a mensagem era uma confirmacao (o fluxo
        /// normal de atendimento/IA nao deve tratar essa mensagem).
        /// </summary>
        Task<bool> TentarConfirmarPorMensagemAsync(Guid conversaId, string? texto);

        Task<IReadOnlyList<CardapioPedidoAguardandoDto>> ListarAguardandoAsync(Guid estabelecimentoId);
        Task<AceitarCardapioPedidoResultDto> AceitarAsync(Guid estabelecimentoId, int actorUserId, Guid id);
        Task RecusarAsync(Guid estabelecimentoId, Guid id, string? motivo);

        /// <summary>Disparo manual: avisa que o pedido de retirada ja esta pronto (so depois de aceito, so retirada).</summary>
        Task ProntoParaRetiradaAsync(Guid estabelecimentoId, Guid id);
    }
}
