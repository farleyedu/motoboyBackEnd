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

    }
}
