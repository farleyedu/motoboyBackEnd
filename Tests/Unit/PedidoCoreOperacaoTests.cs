using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using APIBack.DTOs.Delivery;
using APIBack.Model.Cardapio;
using APIBack.Repository.Interface;
using APIBack.Service;
using APIBack.Service.Interface;
using Moq;
using Xunit;

namespace APIBack.Tests.Unit
{
    /// <summary>Regras de operacao do nucleo de pedidos: horario sempre vale para cliente/IA e o pedido manual de entrega exige raio e coordenada.</summary>
    public class PedidoCoreOperacaoTests
    {
        private static readonly Guid P1 = Guid.NewGuid();

        private static (PedidoCoreService Service, Mock<IPedidoQueueRepository> Queue) Create(
            RestaurantSettingsDto restaurant, AberturaLoja? abertura = null, bool bloquearForaHorarioNoDelivery = false)
        {
            var queue = new Mock<IPedidoQueueRepository>();
            queue.Setup(q => q.GetSettingsAsync(It.IsAny<Guid>())).ReturnsAsync(new DeliverySettingsDto
            {
                DefaultDeliveryMinutes = 45,
                // O interruptor antigo nao decide mais nada para cliente/IA.
                BloquearPedidosForaHorario = bloquearForaHorarioNoDelivery
            });
            queue.Setup(q => q.CreatePedidoAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<ManualOrder>()))
                .ReturnsAsync((Guid _, int _, ManualOrder o) => new CreatedPedidoDto { Id = 10, Total = o.Value });
            var restaurantRepo = new Mock<IRestaurantSettingsRepository>();
            restaurantRepo.Setup(r => r.GetAsync(It.IsAny<Guid>())).ReturnsAsync(restaurant);
            var menu = new Mock<ICardapioRepository>();
            menu.Setup(m => m.ListarProdutosPublicosPorIdsAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<bool>()))
                .ReturnsAsync(new[] { CoreFixtures.Product(P1, 30m) });
            var zonas = new Mock<IDeliveryZonaRepository>();
            zonas.Setup(z => z.ListAtivasOrdenadasAsync(It.IsAny<Guid>())).ReturnsAsync(Array.Empty<DeliveryZonaDto>());
            var horarios = new Mock<IHorarioOperacaoRepository>();
            horarios.Setup(h => h.AvaliarAberturaAsync(It.IsAny<Guid>(), It.IsAny<DateTimeOffset>(), It.IsAny<string?>()))
                .ReturnsAsync(abertura ?? new AberturaLoja(true, null, null, "America/Sao_Paulo"));
            var clientes = new Mock<IClienteCadastroRepository>();
            clientes.Setup(c => c.ResolverOuCriarAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string?>())).ReturnsAsync(Guid.NewGuid());
            return (new PedidoCoreService(queue.Object, restaurantRepo.Object, menu.Object, zonas.Object, horarios.Object,
                Mock.Of<IAtendenteConfirmacaoSender>(), clientes.Object, Mock.Of<IClienteEnderecoRepository>()), queue);
        }

        private static CreatePedidoRequest Estruturado(string? origem = null)
        {
            var request = CoreFixtures.Request();
            request.Origem = origem;
            request.Itens = new List<PedidoItemRequest> { new() { ProdutoId = P1, Quantidade = 1 } };
            return request;
        }

        private static ManualOrder Salvo(Mock<IPedidoQueueRepository> queue) =>
            (ManualOrder)queue.Invocations.Single(i => i.Method.Name == nameof(IPedidoQueueRepository.CreatePedidoAsync)).Arguments[2];

        // ---- horario para cliente e IA ----------------------------------------------------------------------------------

        [Theory]
        [InlineData("cardapio_web", HorarioRegras.SemHorario)]
        [InlineData("cardapio_web", HorarioRegras.ForaHorario)]
        [InlineData("ia_whatsapp", HorarioRegras.SemHorario)]
        [InlineData("ia_whatsapp", HorarioRegras.ForaHorario)]
        public async Task Cliente_e_ia_fora_do_horario_ou_sem_horario_sao_recusados_mesmo_com_o_interruptor_desligado(string origem, string motivo)
        {
            var (service, queue) = Create(CoreFixtures.Restaurant(), new AberturaLoja(false, motivo, "hoje às 18:00", "America/Sao_Paulo"));

            var ex = await Assert.ThrowsAsync<DeliveryDomainException>(() => service.CreateAsync(CoreFixtures.Est, 1, Estruturado(origem), null));

            Assert.Equal("STORE_CLOSED", ex.Code);
            Assert.Equal(409, ex.StatusCode);
            var detalhes = Assert.IsType<DetalhesErroOperacional>(ex.Details);
            Assert.False(string.IsNullOrWhiteSpace(detalhes.ComoResolver));
            Assert.Equal(motivo, detalhes.Dados!["motivo"]);
            queue.Verify(q => q.CreatePedidoAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<ManualOrder>()), Times.Never);
        }

        [Fact]
        public async Task Cliente_dentro_do_horario_e_aceito()
        {
            var (service, queue) = Create(CoreFixtures.Restaurant());

            await service.CreateAsync(CoreFixtures.Est, 1, Estruturado("cardapio_web"), null);

            Assert.Equal(30m + 0m, Salvo(queue).Subtotal);
        }

        // ---- pedido manual -----------------------------------------------------------------------------------------------

        [Fact]
        public async Task Pedido_manual_nao_e_barrado_pelo_horario_e_devolve_aviso()
        {
            var (service, queue) = Create(CoreFixtures.Restaurant(), new AberturaLoja(false, HorarioRegras.SemHorario, null, "America/Sao_Paulo"));

            await service.CreateAsync(CoreFixtures.Est, 1, Estruturado(), null, autoAtribuir: false);

            var aviso = Assert.Single(Salvo(queue).Avisos);
            Assert.Contains("horário", aviso);
        }

        [Fact]
        public async Task Pedido_manual_fora_do_horario_avisa_que_cliente_nao_consegue_pedir()
        {
            var (service, queue) = Create(CoreFixtures.Restaurant(), new AberturaLoja(false, HorarioRegras.ForaHorario, "hoje às 18:00", "America/Sao_Paulo"));

            await service.CreateAsync(CoreFixtures.Est, 1, Estruturado(), null, autoAtribuir: false);

            Assert.Contains("fora do horário", Assert.Single(Salvo(queue).Avisos));
        }

        [Theory]
        [InlineData(null)]
        [InlineData(0.0)]
        public async Task Pedido_manual_de_entrega_sem_raio_e_recusado_com_o_caminho(double? raio)
        {
            var (service, queue) = Create(CoreFixtures.Restaurant(radiusKm: raio.HasValue ? (decimal)raio.Value : null));

            var ex = await Assert.ThrowsAsync<DeliveryDomainException>(() => service.CreateAsync(CoreFixtures.Est, 1, Estruturado(), null));

            Assert.Equal("RAIO_ENTREGA_NAO_CONFIGURADO", ex.Code);
            Assert.Equal(422, ex.StatusCode);
            var detalhes = Assert.IsType<DetalhesErroOperacional>(ex.Details);
            Assert.Equal("delivery", detalhes.Correcao!.Area);
            Assert.False(string.IsNullOrWhiteSpace(detalhes.ComoResolver));
            queue.Verify(q => q.CreatePedidoAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<ManualOrder>()), Times.Never);
        }

        [Theory]
        [InlineData(null, null)]
        [InlineData(0.0, 0.0)]
        public async Task Pedido_manual_de_entrega_sem_coordenada_da_loja_e_recusado(double? lat, double? lon)
        {
            var (service, queue) = Create(CoreFixtures.Restaurant(radiusKm: 5m, lat: lat, lon: lon));

            var ex = await Assert.ThrowsAsync<DeliveryDomainException>(() => service.CreateAsync(CoreFixtures.Est, 1, Estruturado(), null));

            Assert.Equal("LOJA_SEM_COORDENADA", ex.Code);
            var correcao = Assert.IsType<DetalhesErroOperacional>(ex.Details).Correcao!;
            Assert.Equal(("negocio", "endereco"), (correcao.Area, correcao.Foco));
            queue.Verify(q => q.CreatePedidoAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<ManualOrder>()), Times.Never);
        }

        [Fact]
        public async Task Pedido_manual_com_raio_e_coordenada_e_criado()
        {
            var (service, queue) = Create(CoreFixtures.Restaurant(radiusKm: 10m));

            await service.CreateAsync(CoreFixtures.Est, 1, Estruturado(), null, autoAtribuir: false);

            Assert.Empty(Salvo(queue).Avisos);
        }

        [Fact]
        public async Task Pedido_do_ifood_nao_passa_pela_regra_do_raio_nem_do_horario()
        {
            var (service, queue) = Create(CoreFixtures.Restaurant(radiusKm: null, lat: null, lon: null),
                new AberturaLoja(false, HorarioRegras.SemHorario, null, "America/Sao_Paulo"));
            var request = CoreFixtures.Request();
            request.Origem = "ifood";
            request.OrigemRef = "ifood-123";
            request.Items = "1 combo";
            request.Value = 55m;

            await service.CreateAsync(CoreFixtures.Est, 1, request, null, autoAtribuir: false);

            Assert.Equal("ifood", Salvo(queue).Origem);
        }
    }
}
