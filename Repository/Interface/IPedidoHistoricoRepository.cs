using System;
using System.Threading.Tasks;
using APIBack.DTOs.Delivery;

namespace APIBack.Repository.Interface
{
    public interface IPedidoHistoricoRepository
    {
        /// <summary>Linha do tempo do pedido; null quando ele nao existe neste estabelecimento.</summary>
        Task<PedidoHistoricoDto?> GetAsync(Guid estabelecimentoId, int pedidoId);
    }
}
