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
        public IEnumerable<Pedido> GetPedidos()
        {
            return _pedidoRepository.GetPedidos();
        }
        public async Task CriarPedidosIfood(PedidoCapturado pedidos)
        {
            // Pode virar async de verdade futuramente
            _pedidoRepository.InserirPedidosIfood(pedidos);
            await Task.CompletedTask;
        }

        public EnviarPedidosParaRotaDTO GetPedidosId(int Id)
        {
            return _pedidoRepository.GetPedidosId(Id);
        }
        public IEnumerable<PedidoDTOs> GetPedidosMaps()
        {
            return _pedidoRepository.GetPedidosMaps();
        }
        public IEnumerable<Pedido> CriarPedido()
        {
            return _pedidoRepository.CriarPedido();
        }
        public async Task AtribuirMotoboy(EnviarPedidosParaRotaDTO dto)
        {
            foreach (var id in dto.PedidosIds)
            {
                var pedido = _pedidoRepository.GetPedidosId(id);
                if (pedido == null) continue;

                pedido.StatusPedido = dto.StatusPedido;
                pedido.MotoboyResponsavel = dto.MotoboyResponsavel;
                pedido.HorarioSaida = DateTime.UtcNow.ToString("HH:mm:ss");

                await _pedidoRepository.AtribuirMotoboy(pedido);
            }
            await Task.CompletedTask;
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
        public async Task<PedidoCompletoResponse?> GetPedidoCompleto(int id)
        {
            return await _pedidoRepository.GetPedidoCompleto(id);
        }

        /// <summary>
        /// Obtém todos os pedidos completos com todos os detalhes
        /// </summary>
        /// <returns>Lista com todos os pedidos completos</returns>
        public async Task<List<PedidoCompletoResponse>> GetTodosPedidosCompletos()
        {
            return await _pedidoRepository.GetTodosPedidosCompletos();
        }

    }
}
