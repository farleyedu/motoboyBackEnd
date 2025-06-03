using APIBack.DTOs;
using APIBack.Model;

namespace APIBack.Repository.Interface
{
    public interface IPedidoRepository
    {

        IEnumerable<Pedido> GetPedidos();
        IEnumerable<Pedido> GetPedidosId();
        IEnumerable<Pedido> CriarPedido();
        IEnumerable<PedidoDTOs> GetPedidosMaps();
        IEnumerable<Pedido> AtribuirMotoboy();
        IEnumerable<Pedido> CancelarPedido();
        IEnumerable<Pedido> FinalizarPedido();
        IEnumerable<Pedido> AlteraPedido(int Id, Pedido pedido);
        IEnumerable<Pedido> GetPedidosPorMotoboy(int motoboyId);


    }
}
