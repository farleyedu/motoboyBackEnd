using System;
using System.Threading;
using System.Threading.Tasks;
using APIBack.DTOs.Cardapio;
using APIBack.Model.Cardapio;
using APIBack.Payments.Controllers;
using APIBack.Payments.MercadoPago;
using APIBack.Repository.Interface;

namespace APIBack.Service.Interface
{
    /// <summary>
    /// Pagamento online (Pix e cartao pelo Mercado Pago, na conta da loja) do pedido do cardapio web. O valor sai sempre do pre-pedido
    /// (recalculado no servidor); o navegador so escolhe o meio. Pagamento confirmado so pelo webhook/conciliador, nunca pelo retorno
    /// do navegador. Cancelamento e reembolso conforme a fase: antes de pagar cancela a intencao; pago e nao aceito volta integral
    /// automaticamente; aceito antes do preparo volta integral com aprovacao do gerente; em preparo/entrega vira solicitacao para analise.
    /// </summary>
    public interface ICardapioPagamentoService
    {
        /// <summary>Pix do pedido (o mesmo enquanto valer; um novo depois de vencido).</summary>
        Task<PublicPixDto> CriarPixAsync(Guid prePedidoId, string? codigo, CancellationToken ct = default);

        /// <summary>Cartao pelo Payment Brick. Pix em aberto e cancelado antes; recusado = 422 PAG_CARTAO_RECUSADO.</summary>
        Task<CardPaymentResult> PagarComCartaoAsync(Guid prePedidoId, PublicCartaoPedidoRequest request, CancellationToken ct = default);

        /// <summary>Pix atual (QR enquanto pendente) ou { status: sem_pagamento }.</summary>
        Task<object> ObterPagamentoPublicoAsync(Guid prePedidoId, string? codigo, CancellationToken ct = default);

        /// <summary>Resumo do pagamento para a tela de acompanhamento (null no presencial).</summary>
        Task<CardapioPagamentoPublicoDto?> ResumoAsync(CardapioPedidoPublico pedido);

        /// <summary>O cliente desiste antes do aceite: cancela a intencao aberta e, se pagou, devolve tudo.</summary>
        Task CancelarPeloClienteAsync(Guid prePedidoId, string? codigo, string? motivo, CancellationToken ct = default);

        /// <summary>Depois de uma recusa/cancelamento: cancela Pix aberto e dispara o reembolso gravado na decisao.</summary>
        Task AposEncerrarAsync(CardapioDecisao decisao, CancellationToken ct = default);

        /// <summary>Chama o Mercado Pago para os reembolsos pendentes (ou so o informado). Devolve quantos foram tentados.</summary>
        Task<int> ProcessarReembolsosAsync(Guid? somente = null, CancellationToken ct = default);

        /// <summary>Pagou e nao confirmou o numero no prazo: cancela e devolve o dinheiro. Devolve quantos pedidos.</summary>
        Task<int> DevolverAbandonadosAsync(CancellationToken ct = default);

        Task<CardapioPagamentoLojaDto> ObterParaLojaAsync(Guid estabelecimentoId, Guid prePedidoId);
        Task<CardapioReembolsoDto> SolicitarReembolsoAsync(Guid estabelecimentoId, int usuarioId, Guid prePedidoId, SolicitarReembolsoCardapioRequest request);
        Task<CardapioReembolsoDto> DecidirReembolsoAsync(Guid estabelecimentoId, int usuarioId, Guid reembolsoId, bool aprovar, string? justificativa);
    }
}
