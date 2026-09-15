using APIBack.DTOs;
using APIBack.Model;

namespace APIBack.Repository.Interface
{
    public interface IPedidoRepository
    {

        IEnumerable<Pedido> GetPedidos(Guid estabelecimentoId);
        EnviarPedidosParaRotaDTO? GetPedidosId(int id, Guid estabelecimentoId);
        IEnumerable<Pedido> CriarPedido();
        IEnumerable<PedidoDTOs> GetPedidosMaps(Guid estabelecimentoId);
        // Atribuir motoboy passou a ser responsabilidade de IPedidoQueueRepository.
        IEnumerable<Pedido> CancelarPedido();
        IEnumerable<Pedido> FinalizarPedido();
        IEnumerable<Pedido> AlteraPedido(int Id, Pedido pedido);
        IEnumerable<Pedido> GetPedidosPorMotoboy(int motoboyId, Guid estabelecimentoId);
        //void UpdateStatusLote(EnviarPedidosParaRotaDTO dto);
        /// <returns>true se inseriu; false se ja existia um pedido com o mesmo id_ifood/estabelecimento (repeticao idempotente).</returns>
        bool InserirPedidosIfood(PedidoCapturado pedidos, Guid estabelecimentoId);

        /// <summary>
        /// Obtém pedido completo com todos os detalhes
        /// </summary>
        /// <param name="id">ID do pedido</param>
        /// <returns>Dados completos do pedido ou null se não encontrado</returns>
        Task<PedidoCompletoResponse?> GetPedidoCompleto(int id, Guid estabelecimentoId);

        /// <summary>
        /// Obtém todos os pedidos completos do banco de dados
        /// </summary>
        /// <returns>Lista com todos os pedidos completos</returns>
        Task<List<PedidoCompletoResponse>> GetTodosPedidosCompletos(Guid estabelecimentoId);
    }
}
