using APIBack.DTOs;
using APIBack.Model;

namespace APIBack.Repository.Interface
{
    public interface IPedidoRepository
    {

        IEnumerable<Pedido> GetPedidos();
        EnviarPedidosParaRotaDTO? GetPedidosId(int id);
        IEnumerable<Pedido> CriarPedido();
        IEnumerable<PedidoDTOs> GetPedidosMaps();
        Task AtribuirMotoboy(EnviarPedidosParaRotaDTO dto);
        IEnumerable<Pedido> CancelarPedido();
        IEnumerable<Pedido> FinalizarPedido();
        IEnumerable<Pedido> AlteraPedido(int Id, Pedido pedido);
        IEnumerable<Pedido> GetPedidosPorMotoboy(int motoboyId);
        //void UpdateStatusLote(EnviarPedidosParaRotaDTO dto);
        void InserirPedidosIfood(PedidoCapturado pedidos);
        
        /// <summary>
        /// Obtém pedido completo com todos os detalhes
        /// </summary>
        /// <param name="id">ID do pedido</param>
        /// <returns>Dados completos do pedido ou null se não encontrado</returns>
        Task<PedidoCompletoResponse?> GetPedidoCompleto(int id);

        /// <summary>
        /// Obtém todos os pedidos completos do banco de dados
        /// </summary>
        /// <returns>Lista com todos os pedidos completos</returns>
        Task<List<PedidoCompletoResponse>> GetTodosPedidosCompletos();
    }
}
