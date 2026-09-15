using APIBack.DTOs;
using APIBack.Model;
using APIBack.Repository.Interface;
using APIBack.Service.Interface;

namespace APIBack.Service
{
    public class PedidoService : IPedidoService
    {
        private readonly IPedidoRepository _pedidoRepository;
        public PedidoService(IPedidoRepository pedidoRepository)
        {
            _pedidoRepository = pedidoRepository;
        }
        public IEnumerable<Pedido> GetPedidos(Guid estabelecimentoId)
        {
            return _pedidoRepository.GetPedidos(estabelecimentoId);
        }
        public async Task<bool> CriarPedidosIfood(PedidoCapturado pedidos, Guid estabelecimentoId)
        {
            var inserted = _pedidoRepository.InserirPedidosIfood(pedidos, estabelecimentoId);
            await Task.CompletedTask;
            return inserted;
        }

        public EnviarPedidosParaRotaDTO GetPedidosId(int Id, Guid estabelecimentoId)
        {
            return _pedidoRepository.GetPedidosId(Id, estabelecimentoId);
        }
        public IEnumerable<PedidoDTOs> GetPedidosMaps(Guid estabelecimentoId)
        {
            return _pedidoRepository.GetPedidosMaps(estabelecimentoId);
        }
        public IEnumerable<Pedido> CriarPedido()
        {
            return _pedidoRepository.CriarPedido();
        }
        public IEnumerable<Pedido> CancelarPedido()
        {
            return _pedidoRepository.CancelarPedido();
        }
        public IEnumerable<Pedido> FinalizarPedido()
        {
            return _pedidoRepository.FinalizarPedido();
        }
        public IEnumerable<Pedido> AlteraPedido(int Id, Pedido pedido)
        {
            return _pedidoRepository.AlteraPedido(Id,pedido);
        }

        /// <summary>
        /// Obtém pedido completo com todos os detalhes para o endpoint riderlink
        /// </summary>
        /// <param name="id">ID do pedido</param>
        /// <returns>Dados completos do pedido ou null se não encontrado</returns>
        public async Task<PedidoCompletoResponse?> GetPedidoCompleto(int id, Guid estabelecimentoId)
        {
            return await _pedidoRepository.GetPedidoCompleto(id, estabelecimentoId);
        }

        /// <summary>
        /// Obtém todos os pedidos completos com todos os detalhes
        /// </summary>
        /// <returns>Lista com todos os pedidos completos</returns>
        public async Task<List<PedidoCompletoResponse>> GetTodosPedidosCompletos(Guid estabelecimentoId)
        {
            return await _pedidoRepository.GetTodosPedidosCompletos(estabelecimentoId);
        }

    }
}
