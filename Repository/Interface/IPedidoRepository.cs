using APIBack.DTOs;
using APIBack.Model;

namespace APIBack.Repository.Interface
{
    public interface IPedidoRepository
    {

        IEnumerable<Pedido> GetPedidos();
        EnviarPedidosParaRotaDTO GetPedidosId(int id);
        IEnumerable<Pedido> CriarPedido();
        IEnumerable<PedidoDTOs> GetPedidosMaps();
        Task AtribuirMotoboy(EnviarPedidosParaRotaDTO dto);
        IEnumerable<Pedido> CancelarPedido();
        IEnumerable<Pedido> FinalizarPedido();
        IEnumerable<Pedido> AlteraPedido(int Id, Pedido pedido);
        IEnumerable<Pedido> GetPedidosPorMotoboy(int motoboyId);
        //void UpdateStatusLote(EnviarPedidosParaRotaDTO dto);
        void InserirPedidosIfood(PedidoCapturado pedidos);
    }
}
