using System;
using System.Threading.Tasks;
using APIBack.DTOs.Delivery;
using APIBack.Service;

namespace APIBack.Repository.Interface
{
    /// <summary>Leitura de pedidos para a tela de Pedidos (lista com filtros e detalhe com itens).</summary>
    public interface IPedidoConsultaRepository
    {
        Task<PedidoListaDto> ListAsync(Guid estabelecimentoId, PedidoFiltro filtro);

        /// <summary>Null quando o pedido nao existe neste estabelecimento.</summary>
        Task<PedidoDetalheDto?> GetAsync(Guid estabelecimentoId, int pedidoId);
    }
}
