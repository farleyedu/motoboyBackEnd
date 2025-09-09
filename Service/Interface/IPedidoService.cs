using APIBack.DTOs;
using APIBack.Model;

namespace APIBack.Service.Interface
{
    public interface IPedidoService
    {
        IEnumerable<Pedido> GetPedidos();
        EnviarPedidosParaRotaDTO GetPedidosId(int id);
        IEnumerable<PedidoDTOs> GetPedidosMaps();
        IEnumerable<Pedido> CriarPedido();
        Task AtribuirMotoboy(EnviarPedidosParaRotaDTO dto);
        IEnumerable<Pedido> CancelarPedido();
        IEnumerable<Pedido> FinalizarPedido();
        IEnumerable<Pedido> AlteraPedido(int id, Pedido pedido);
        Task CriarPedidosIfood(PedidoCapturado pedidos);
        
        /// <summary>
        /// Obtém pedido completo com todos os detalhes para o endpoint riderlink
        /// </summary>
        /// <param name="id">ID do pedido</param>
        /// <returns>Dados completos do pedido ou null se não encontrado</returns>
        Task<PedidoCompletoResponse?> GetPedidoCompleto(int id);

        /// <summary>
        /// Obtém todos os pedidos completos com todos os detalhes
        /// </summary>
        /// <returns>Lista com todos os pedidos completos</returns>
        Task<List<PedidoCompletoResponse>> GetTodosPedidosCompletos();

    }
}
