using APIBack.DTOs;
using APIBack.Model;

namespace APIBack.Service.Interface
{
    public interface IPedidoService
    {
        IEnumerable<Pedido> GetPedidos(Guid estabelecimentoId);
        EnviarPedidosParaRotaDTO GetPedidosId(int id, Guid estabelecimentoId);
        IEnumerable<PedidoDTOs> GetPedidosMaps(Guid estabelecimentoId);
        // Atribuir motoboy passou a ser responsabilidade de IPedidoQueueService.AssignAsync
        // (comando transacional com validacao de tenant/vinculo/sessao). Ver Controllers/DeliveryOrdersV2Controller.cs.
        /// <returns>true se criou; false se ja existia (repeticao idempotente do webhook).</returns>
        Task<bool> CriarPedidosIfood(PedidoCapturado pedidos, Guid estabelecimentoId);

        /// <summary>
        /// Obtém pedido completo com todos os detalhes para o endpoint riderlink
        /// </summary>
        /// <param name="id">ID do pedido</param>
        /// <returns>Dados completos do pedido ou null se não encontrado</returns>
        Task<PedidoCompletoResponse?> GetPedidoCompleto(int id, Guid estabelecimentoId);

        /// <summary>
        /// Obtém todos os pedidos completos com todos os detalhes
        /// </summary>
        /// <returns>Lista com todos os pedidos completos</returns>
        Task<List<PedidoCompletoResponse>> GetTodosPedidosCompletos(Guid estabelecimentoId);

    }
}
