using APIBack.DTOs;
using APIBack.Model;

namespace APIBack.Service.Interface
{
    public interface IPedidoService
    {
        IEnumerable<Pedido> GetPedidos();
        IEnumerable<Pedido> GetPedidosId(int id);
        IEnumerable<PedidoDTOs> GetPedidosMaps();
        IEnumerable<Pedido> CriarPedido();
        IEnumerable<Pedido> AtribuirMotoboy();
        IEnumerable<Pedido> CancelarPedido();
        IEnumerable<Pedido> FinalizarPedido();
        IEnumerable<Pedido> AlteraPedido(int id, Pedido pedido);


    }
}
