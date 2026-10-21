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

        /// <summary>
        /// Contador que sobe a cada mudança na lista de pedidos da loja (0 = nada ainda). Null quando o schema da versão
        /// ainda não foi migrado: quem consulta deve tratar como "sem suporte" e buscar a lista sempre.
        /// </summary>
        Task<long?> GetListVersionAsync(Guid estabelecimentoId);
    }
}
