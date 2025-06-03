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

        public IEnumerable<Pedido> GetPedidosId(int Id)
        {
            return _pedidoRepository.GetPedidosId();
        }
        public IEnumerable<PedidoDTOs> GetPedidosMaps()
        {
            return _pedidoRepository.GetPedidosMaps();
        }
        public IEnumerable<Pedido> CriarPedido()
        {
            return _pedidoRepository.CriarPedido();
        }
        public IEnumerable<Pedido> AtribuirMotoboy()
        {
            return _pedidoRepository.AtribuirMotoboy();
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
    }
}
