using System;
using System.Linq;
using System.Threading.Tasks;
using APIBack.DTOs.Delivery;
using APIBack.Repository.Interface;
using APIBack.Service.Interface;

namespace APIBack.Service
{
    /// <summary>Reutiliza a consulta de itens/fotos, restringindo a leitura à fila do próprio motoboy.</summary>
    public sealed class MotoboyPedidoService
    {
        private readonly IPedidoQueueService _queue;
        private readonly IPedidoConsultaRepository _consulta;

        public MotoboyPedidoService(IPedidoQueueService queue, IPedidoConsultaRepository consulta)
        {
            _queue = queue;
            _consulta = consulta;
        }

        public async Task<MotoboyPedidoDetalheDto> GetAsync(Guid estabelecimentoId, int motoboyId, int pedidoId)
        {
            if (estabelecimentoId == Guid.Empty || motoboyId <= 0)
                throw new DeliveryDomainException(401, "OPERATIONAL_TOKEN_REQUIRED", "Contexto operacional inválido.");
            var queue = await _queue.GetQueueAsync(estabelecimentoId, motoboyId);
            var stop = queue.Current?.PedidoId == pedidoId ? queue.Current
                : queue.Next.FirstOrDefault(item => item.PedidoId == pedidoId)
                  ?? queue.Offer?.Stops.FirstOrDefault(item => item.PedidoId == pedidoId);
            if (stop == null) throw NotInQueue();

            var pedido = await _consulta.GetAsync(estabelecimentoId, pedidoId);
            // Confere também o responsável no detalhe: a atribuição pode ter mudado durante as leituras.
            if (pedido == null || pedido.MotoboyId != motoboyId) throw NotInQueue();
            return new MotoboyPedidoDetalheDto
            {
                Checklist = DeliveryChecklistRules.Build(pedido.Itens),
                Id = pedido.Id, QueueVersion = queue.Version, Position = stop.Position,
                StopStatus = stop.Status, IsCurrent = queue.Current?.PedidoId == pedidoId,
                IsOffer = queue.Offer?.Stops.Any(item => item.PedidoId == pedidoId) == true,
                Locked = stop.Locked, AssignedAtUtc = stop.AssignedAtUtc,
                PickedUpAtUtc = stop.PickedUpAtUtc, ArrivedAtUtc = stop.ArrivedAtUtc,
                Origem = pedido.Origem, NomeCliente = pedido.NomeCliente, TelefoneCliente = pedido.TelefoneCliente,
                EnderecoEntrega = pedido.EnderecoEntrega, Rua = pedido.Rua, Numero = pedido.Numero,
                Bairro = pedido.Bairro, Cidade = pedido.Cidade, Estado = pedido.Estado, Cep = pedido.Cep,
                Latitude = pedido.Latitude, Longitude = pedido.Longitude, Total = pedido.Total,
                Subtotal = pedido.Subtotal, TaxaEntrega = pedido.TaxaEntrega, FormaPagamento = pedido.FormaPagamento,
                StatusPagamento = pedido.StatusPagamento, Troco = pedido.Troco, Observacoes = pedido.Observacoes,
                PrevisaoEntrega = pedido.PrevisaoEntrega, CapaImagemUrl = pedido.CapaImagemUrl, Itens = pedido.Itens,
                RequerCodigoEntrega = stop.Pedido?.RequerCodigoEntrega == true || !string.IsNullOrWhiteSpace(pedido.CodigoEntrega)
            };
        }

        private static DeliveryDomainException NotInQueue() =>
            new(404, "PEDIDO_NOT_IN_YOUR_QUEUE", "Este pedido não está na sua fila.");
    }
}
