using System;
using System.Threading.Tasks;
using APIBack.DTOs.Delivery;

namespace APIBack.Service.Interface
{
    /// <summary>
    /// Nucleo de pedido: unico caminho para criar, editar e confirmar pedidos, com as regras do
    /// estabelecimento aplicadas no servidor. Usado por todas as origens (atendente, cardapio web,
    /// iFood assistido, simulador e, depois, a IA).
    /// </summary>
    public interface IPedidoCoreService
    {
        /// <summary>Cria (ou devolve, se a origem/origemRef ja existe) um pedido.</summary>
        Task<CreatedPedidoDto> CreateAsync(Guid estabelecimentoId, int actorUserId, CreatePedidoRequest request, string? idempotencyKey);

        /// <summary>Substitui os dados de um pedido em Rascunho ou Pendente (sem motoboy).</summary>
        Task<CreatedPedidoDto> UpdateAsync(Guid estabelecimentoId, int actorUserId, int pedidoId, CreatePedidoRequest request);

        /// <summary>Rascunho -> Pendente. Idempotente.</summary>
        Task<CreatedPedidoDto> ConfirmAsync(Guid estabelecimentoId, int actorUserId, int pedidoId);
    }
}
