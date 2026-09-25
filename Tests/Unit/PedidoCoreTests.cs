using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using APIBack.DTOs.Delivery;
using APIBack.Model.Cardapio;
using APIBack.Model.Delivery;
using APIBack.Model.Enum;
using APIBack.Model.Gestao;
using APIBack.Repository.Interface;
using APIBack.Service;
using Moq;
using Xunit;

namespace APIBack.Tests.Unit
{
    internal static class CoreFixtures
    {
        public static readonly Guid Est = Guid.NewGuid();

        public static CardapioProduto Product(Guid id, decimal price, params CardapioGrupoAdicional[] groups) => new()
        {
            Id = id,
            Nome = "X-Bacon",
            PrecoBase = price,
            Grupos = groups.ToList()
        };

        public static CardapioGrupoAdicional Group(int min, int max, params (Guid Id, string Nome, decimal Preco)[] addons) => new()
        {
            Id = Guid.NewGuid(),
            Nome = "Extras",
            MinSelecionados = min,
            MaxSelecionados = max,
            Itens = addons.Select(a => new CardapioGrupoAdicionalItem { Id = a.Id, Nome = a.Nome, Preco = a.Preco }).ToList()
        };

        public static RestaurantSettingsDto Restaurant(
            bool accepts = true, decimal? minimum = null, decimal? radiusKm = null,
            decimal? fixedFee = null, decimal? perKm = null, double? lat = -18.9, double? lon = -48.27) => new()
        {
            EstabelecimentoId = Est,
            AceitaPedidos = accepts,
            PedidoMinimo = minimum,
            RaioEntregaKm = radiusKm,
            TaxaEntregaFixa = fixedFee,
            TaxaEntregaPorKm = perKm,
            Latitude = lat,
            Longitude = lon
        };

        public static CreatePedidoRequest Request() => new()
        {
            NomeCliente = "Maria",
            TelefoneCliente = "34999990000",
            Rua = "Rua A",
            Numero = "10",
            Bairro = "Centro",
            Cidade = "Uberlandia",
            Estado = "MG",
            Latitude = -18.91,
            Longitude = -48.27
        };
    }

    public sealed class PedidoPricingTests
    {
        private static readonly Guid P1 = Guid.NewGuid();

        private static Dictionary<Guid, CardapioProduto> Menu(params CardapioProduto[] products) =>
            products.ToDictionary(p => p.Id);

        [Fact]
        public void Price_UsesMenuPrice_NotClientPrice()
        {
            var menu = Menu(CoreFixtures.Product(P1, 25.50m));
            var priced = PedidoPricing.Price(
                new[] { new PedidoItemRequest { ProdutoId = P1, Quantidade = 2, PrecoUnitario = 1m, Nome = "ignorado" } },
                menu, allowFreeLines: false);

            var line = Assert.Single(priced.Lines);
            Assert.Equal("X-Bacon", line.Nome);
            Assert.Equal(25.50m, line.PrecoUnitario);
            Assert.Equal(51.00m, priced.Subtotal);
        }

        [Fact]
        public void Price_AddsAddonsPerUnit()
        {
            var addon = Guid.NewGuid();
            var menu = Menu(CoreFixtures.Product(P1, 20m, CoreFixtures.Group(0, 2, (addon, "Queijo", 3m))));
            var priced = PedidoPricing.Price(
                new[] { new PedidoItemRequest { ProdutoId = P1, Quantidade = 3, AdicionalItemIds = new List<Guid> { addon, addon } } },
                menu, allowFreeLines: false);

            // (20 + 3) x 3; o adicional repetido na lista conta uma vez
            Assert.Equal(69m, priced.Subtotal);
            Assert.Equal("Queijo", priced.Lines[0].Adicionais[0].Nome);
        }

        [Fact]
        public void Price_EnforcesGroupMinAndMax()
        {
            var a = Guid.NewGuid();
            var b = Guid.NewGuid();
            var menu = Menu(CoreFixtures.Product(P1, 10m, CoreFixtures.Group(1, 1, (a, "A", 1m), (b, "B", 1m))));

            var missing = Assert.Throws<DeliveryDomainException>(() => PedidoPricing.Price(
                new[] { new PedidoItemRequest { ProdutoId = P1, Quantidade = 1 } }, menu, false));
            Assert.Equal("INVALID_ORDER_ITEMS", missing.Code);

            Assert.Throws<DeliveryDomainException>(() => PedidoPricing.Price(
                new[] { new PedidoItemRequest { ProdutoId = P1, Quantidade = 1, AdicionalItemIds = new List<Guid> { a, b } } }, menu, false));
        }

        [Fact]
        public void Price_RejectsAddonOfAnotherProduct()
        {
            var menu = Menu(CoreFixtures.Product(P1, 10m));
            Assert.Throws<DeliveryDomainException>(() => PedidoPricing.Price(
                new[] { new PedidoItemRequest { ProdutoId = P1, Quantidade = 1, AdicionalItemIds = new List<Guid> { Guid.NewGuid() } } },
                menu, false));
        }

        [Fact]
        public void Price_RejectsUnknownOrUnavailableProduct()
        {
            var ex = Assert.Throws<DeliveryDomainException>(() => PedidoPricing.Price(
                new[] { new PedidoItemRequest { ProdutoId = Guid.NewGuid(), Quantidade = 1 } }, Menu(), true));
            Assert.Contains("produto", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(101)]
        [InlineData(-1)]
        public void Price_RejectsInvalidQuantity(int quantity)
        {
            var menu = Menu(CoreFixtures.Product(P1, 10m));
            Assert.Throws<DeliveryDomainException>(() => PedidoPricing.Price(
                new[] { new PedidoItemRequest { ProdutoId = P1, Quantidade = quantity } }, menu, false));
        }

        [Fact]
        public void Price_RequiresAtLeastOneItem()
        {
            Assert.Throws<DeliveryDomainException>(() => PedidoPricing.Price(null, Menu(), true));
            Assert.Throws<DeliveryDomainException>(() => PedidoPricing.Price(Array.Empty<PedidoItemRequest>(), Menu(), true));
        }

        [Fact]
        public void Price_FreeLine_OnlyWhenAllowed_WithNameAndPrice()
        {
            var free = new PedidoItemRequest { Nome = "Marmita", Quantidade = 2, PrecoUnitario = 18.90m };

            var priced = PedidoPricing.Price(new[] { free }, Menu(), allowFreeLines: true);
            Assert.Null(priced.Lines[0].ProdutoId);
            Assert.Equal(37.80m, priced.Subtotal);

            Assert.Throws<DeliveryDomainException>(() => PedidoPricing.Price(new[] { free }, Menu(), allowFreeLines: false));
            Assert.Throws<DeliveryDomainException>(() => PedidoPricing.Price(
                new[] { new PedidoItemRequest { Nome = "  ", Quantidade = 1, PrecoUnitario = 5m } }, Menu(), true));
            Assert.Throws<DeliveryDomainException>(() => PedidoPricing.Price(
                new[] { new PedidoItemRequest { Nome = "X", Quantidade = 1 } }, Menu(), true));
        }
    }

    public sealed class OrderCoreRulesTests
    {
        private static readonly PedidoOrigem.OriginRules Strict = PedidoOrigem.Rules(PedidoOrigem.CardapioWeb);
        private static readonly PedidoOrigem.OriginRules Lenient = PedidoOrigem.Rules(PedidoOrigem.Atendente);

        [Fact]
        public void Strict_BlocksWhenStoreNotAccepting()
        {
            var ex = Assert.Throws<DeliveryDomainException>(() =>
                OrderCoreRules.Evaluate(CoreFixtures.Restaurant(accepts: false), 50m, -18.91, -48.27, Strict, null));
            Assert.Equal("STORE_NOT_ACCEPTING", ex.Code);
            Assert.Equal(409, ex.StatusCode);
        }

        [Fact]
        public void Lenient_TurnsViolationsIntoWarnings()
        {
            var result = OrderCoreRules.Evaluate(
                CoreFixtures.Restaurant(accepts: false, minimum: 30m), 10m, -18.91, -48.27, Lenient, null);

            Assert.Equal(2, result.Warnings.Count);
        }

        [Fact]
        public void Strict_BlocksBelowMinimum()
        {
            var ex = Assert.Throws<DeliveryDomainException>(() =>
                OrderCoreRules.Evaluate(CoreFixtures.Restaurant(minimum: 30m), 29.99m, -18.91, -48.27, Strict, null));
            Assert.Equal("MINIMUM_NOT_REACHED", ex.Code);

            // no minimo exato passa
            Assert.Empty(OrderCoreRules.Evaluate(CoreFixtures.Restaurant(minimum: 30m), 30m, -18.91, -48.27, Strict, null).Warnings);
        }

        [Fact]
        public void Strict_BlocksOutsideRadius_AndAllowsInside()
        {
            var store = CoreFixtures.Restaurant(radiusKm: 5m, lat: -18.90, lon: -48.27);

            // ~11 km ao sul
            var far = Assert.Throws<DeliveryDomainException>(() =>
                OrderCoreRules.Evaluate(store, 50m, -19.00, -48.27, Strict, null));
            Assert.Equal("OUT_OF_DELIVERY_RANGE", far.Code);

            var near = OrderCoreRules.Evaluate(store, 50m, -18.91, -48.27, Strict, null);
            Assert.InRange(near.DistanceKm!.Value, 1.0, 1.3);
        }

        [Fact]
        public void NoStorePosition_SkipsRadius_AndFeeUsesFixedOnly()
        {
            var store = CoreFixtures.Restaurant(radiusKm: 5m, fixedFee: 6m, perKm: 2m, lat: null, lon: null);
            var result = OrderCoreRules.Evaluate(store, 50m, -19.5, -47.0, Strict, null);

            Assert.Null(result.DistanceKm);
            Assert.Equal(6m, result.DeliveryFee);
        }

        [Fact]
        public void ZeroZeroStorePosition_IsTreatedAsMissing()
        {
            var store = CoreFixtures.Restaurant(radiusKm: 1m, lat: 0, lon: 0);
            Assert.Null(OrderCoreRules.Evaluate(store, 50m, -18.9, -48.2, Strict, null).DistanceKm);
        }

        [Fact]
        public void Fee_IsFixedPlusPerKmTimesDistance_Rounded()
        {
            var store = CoreFixtures.Restaurant(fixedFee: 5m, perKm: 1.5m, lat: -18.90, lon: -48.27);
            var result = OrderCoreRules.Evaluate(store, 50m, -18.91, -48.27, Strict, null);

            var expected = decimal.Round(5m + 1.5m * (decimal)result.DistanceKm!.Value, 2);
            Assert.Equal(expected, result.DeliveryFee);
            Assert.InRange(result.DeliveryFee, 6.5m, 7.1m);
        }

        [Fact]
        public void FeeOverride_OnlyWhereAllowed_AndMustBeValid()
        {
            var store = CoreFixtures.Restaurant(fixedFee: 5m);
            Assert.Equal(0m, OrderCoreRules.Evaluate(store, 50m, -18.91, -48.27, Lenient, 0m).DeliveryFee);
            // origem estrita ignora o override (o servico ja recusa antes)
            Assert.Equal(5m, OrderCoreRules.Evaluate(store, 50m, -18.91, -48.27, Strict, 0m).DeliveryFee);
            Assert.Throws<DeliveryDomainException>(() => OrderCoreRules.Evaluate(store, 50m, -18.91, -48.27, Lenient, -1m));
        }

        [Fact]
        public void DistanceKm_KnownValue()
        {
            // 1 grau de latitude ~ 111,19 km
            Assert.Equal(111.19, OrderCoreRules.DistanceKm(0, 0, 1, 0), 1);
            Assert.Equal(0, OrderCoreRules.DistanceKm(-18.9, -48.2, -18.9, -48.2), 6);
        }

        [Theory]
        [InlineData("Dinheiro", "dinheiro")]
        [InlineData("PIX", "pix")]
        [InlineData("Cartão na entrega", "cartao_entrega")]
        [InlineData("link de pagamento", "link")]
        [InlineData("pagoApp", "pagoApp")]
        [InlineData("  ", null)]
        [InlineData(null, null)]
        public void NormalizePayment_KnownValuesNormalized_UnknownPreserved(string? input, string? expected)
        {
            Assert.Equal(expected, OrderCoreRules.NormalizePayment(input));
        }
    }

    public sealed class PedidoOrigemTests
    {
        [Theory]
        [InlineData(null, "atendente")]
        [InlineData("", "atendente")]
        [InlineData("  IFOOD ", "ifood")]
        [InlineData("cardapio_web", "cardapio_web")]
        public void Normalize_DefaultsToAtendente_AndLowercases(string? input, string expected) =>
            Assert.Equal(expected, PedidoOrigem.Normalize(input));

        [Fact]
        public void Normalize_RejectsUnknown() => Assert.Null(PedidoOrigem.Normalize("telepatia"));

        [Fact]
        public void Rules_StrictOnlyForCustomerFacingOrigins()
        {
            Assert.True(PedidoOrigem.Rules(PedidoOrigem.CardapioWeb).EnforceStoreRules);
            Assert.True(PedidoOrigem.Rules(PedidoOrigem.IaWhatsapp).EnforceStoreRules);
            Assert.False(PedidoOrigem.Rules(PedidoOrigem.CardapioWeb).AllowFreeLines);
            Assert.False(PedidoOrigem.Rules(PedidoOrigem.IaWhatsapp).AllowFeeOverride);
            Assert.False(PedidoOrigem.Rules(PedidoOrigem.Atendente).EnforceStoreRules);
            Assert.True(PedidoOrigem.Rules(PedidoOrigem.Ifood).AllowFreeLines);
            Assert.True(PedidoOrigem.Rules(PedidoOrigem.Simulador).AllowFeeOverride);
        }

        [Fact]
        public void EveryOriginIsInTheDatabaseCheckConstraint()
        {
            var sql = System.IO.File.ReadAllText(System.IO.Path.Combine(
                AppContext.BaseDirectory, "Migrations", "Delivery", "20260925_05_constraints.sql"));
            foreach (var origin in PedidoOrigem.All)
            {
                Assert.Contains($"'{origin}'", sql, StringComparison.Ordinal);
            }
        }
    }

    public sealed class StatusPedidoRascunhoTests
    {
        [Fact]
        public void Rascunho_IsValueSix_AndHasApiKey()
        {
            Assert.Equal(6, (int)StatusPedido.Rascunho);
            Assert.Equal(StatusPedido.Rascunho, StatusPedidoExtensions.FromDbValue(6));
            Assert.Equal("rascunho", StatusPedido.Rascunho.ToApiKey());
            Assert.Equal("rascunho", StatusPedidoExtensions.ToApiKey(6));
        }

        [Fact]
        public void ExistingValuesAreUnchanged()
        {
            Assert.Equal(1, (int)StatusPedido.Pendente);
            Assert.Equal(2, (int)StatusPedido.EmRota);
            Assert.Equal(3, (int)StatusPedido.Concluido);
            Assert.Equal(4, (int)StatusPedido.Cancelado);
            Assert.Equal(5, (int)StatusPedido.Atribuido);
            Assert.Null(StatusPedidoExtensions.FromDbValue(7));
        }
    }

    public sealed class PedidoCoreServiceTests
    {
        private static readonly Guid P1 = Guid.NewGuid();

        private static (PedidoCoreService Service, Mock<IPedidoQueueRepository> Queue, Mock<ICardapioRepository> Menu) Create(
            RestaurantSettingsDto? restaurant = null, CardapioProduto? product = null)
        {
            var queue = new Mock<IPedidoQueueRepository>();
            queue.Setup(q => q.GetSettingsAsync(It.IsAny<Guid>())).ReturnsAsync(new DeliverySettingsDto { DefaultDeliveryMinutes = 45 });
            queue.Setup(q => q.CreatePedidoAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<ManualOrder>()))
                .ReturnsAsync((Guid _, int _, ManualOrder o) => new CreatedPedidoDto { Id = 10, Total = o.Value });

            var restaurantRepo = new Mock<IRestaurantSettingsRepository>();
            restaurantRepo.Setup(r => r.GetAsync(It.IsAny<Guid>())).ReturnsAsync(restaurant ?? CoreFixtures.Restaurant());

            var menu = new Mock<ICardapioRepository>();
            menu.Setup(m => m.ListarProdutosPublicosPorIdsAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<bool>()))
                .ReturnsAsync(product == null ? Array.Empty<CardapioProduto>() : new[] { product });

            return (new PedidoCoreService(queue.Object, restaurantRepo.Object, menu.Object), queue, menu);
        }

        private static ManualOrder Captured(Mock<IPedidoQueueRepository> queue) =>
            (ManualOrder)queue.Invocations.Single(i => i.Method.Name == nameof(IPedidoQueueRepository.CreatePedidoAsync)).Arguments[2];

        [Fact]
        public async Task LegacyRequest_KeepsTypedValueAndText_NoCoreFields()
        {
            var (service, queue, menu) = Create();
            var request = CoreFixtures.Request();
            request.Items = "2 pizzas";
            request.Value = 80m;
            request.TipoPagamento = "Dinheiro";

            await service.CreateAsync(CoreFixtures.Est, 1, request, null);

            var order = Captured(queue);
            Assert.Equal(80m, order.Value);
            Assert.Equal("2 pizzas", order.Items);
            Assert.Equal("Dinheiro", order.TipoPagamento); // formato antigo nao e normalizado
            Assert.Empty(order.Lines);
            Assert.Null(order.Subtotal);
            Assert.False(order.NeedsCoreSchema);
            menu.Verify(m => m.ListarProdutosPublicosPorIdsAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<bool>()), Times.Never);
        }

        [Fact]
        public async Task StructuredItems_ArePricedByTheServer_WithFeeAndTotal()
        {
            var product = CoreFixtures.Product(P1, 30m);
            var (service, queue, menu) = Create(CoreFixtures.Restaurant(fixedFee: 6m), product);
            var request = CoreFixtures.Request();
            request.Value = 1m; // o cliente tenta impor um total: e ignorado
            request.TipoPagamento = "PIX";
            request.Itens = new List<PedidoItemRequest> { new() { ProdutoId = P1, Quantidade = 2 } };

            await service.CreateAsync(CoreFixtures.Est, 1, request, null);

            var order = Captured(queue);
            Assert.Equal(60m, order.Subtotal);
            Assert.Equal(6m, order.TaxaEntrega);
            Assert.Equal(66m, order.Value);
            Assert.Equal("pix", order.TipoPagamento);
            Assert.Contains("X-Bacon", order.Items, StringComparison.Ordinal);
            Assert.True(order.NeedsCoreSchema);
            // busca produtos vendaveis, NAO so os publicados no cardapio web
            menu.Verify(m => m.ListarProdutosPublicosPorIdsAsync(CoreFixtures.Est, It.IsAny<IReadOnlyCollection<Guid>>(), false), Times.Once);
        }

        [Fact]
        public async Task IdempotencyKey_BecomesOrigemRef_UnlessOrigemRefIsGiven()
        {
            var (service, queue, _) = Create();
            await service.CreateAsync(CoreFixtures.Est, 1, CoreFixtures.Request(), "  chave-1 ");
            Assert.Equal("chave-1", Captured(queue).OrigemRef);

            var (service2, queue2, _) = Create();
            var request = CoreFixtures.Request();
            request.OrigemRef = "ifood-99";
            await service2.CreateAsync(CoreFixtures.Est, 1, request, "chave-1");
            Assert.Equal("ifood-99", Captured(queue2).OrigemRef);
        }

        [Fact]
        public async Task StrictOrigin_RequiresItems_AndRefusesFreeLinesAndFeeOverride()
        {
            var product = CoreFixtures.Product(P1, 30m);
            var (service, _, _) = Create(product: product);

            var noItems = CoreFixtures.Request();
            noItems.Origem = "ia_whatsapp";
            noItems.Value = 50m;
            await Assert.ThrowsAsync<DeliveryDomainException>(() => service.CreateAsync(CoreFixtures.Est, 1, noItems, null));

            var freeLine = CoreFixtures.Request();
            freeLine.Origem = "cardapio_web";
            freeLine.Itens = new List<PedidoItemRequest> { new() { Nome = "Avulso", Quantidade = 1, PrecoUnitario = 1m } };
            await Assert.ThrowsAsync<DeliveryDomainException>(() => service.CreateAsync(CoreFixtures.Est, 1, freeLine, null));

            var fee = CoreFixtures.Request();
            fee.Origem = "ia_whatsapp";
            fee.TaxaEntrega = 0m;
            fee.Itens = new List<PedidoItemRequest> { new() { ProdutoId = P1, Quantidade = 1 } };
            var ex = await Assert.ThrowsAsync<DeliveryDomainException>(() => service.CreateAsync(CoreFixtures.Est, 1, fee, null));
            Assert.Equal("INVALID_ORDER", ex.Code);
        }

        [Fact]
        public async Task StrictOrigin_ClosedStoreOrBelowMinimum_IsBlocked_NothingIsSaved()
        {
            var product = CoreFixtures.Product(P1, 10m);
            var (service, queue, _) = Create(CoreFixtures.Restaurant(accepts: false), product);
            var request = CoreFixtures.Request();
            request.Origem = "cardapio_web";
            request.Itens = new List<PedidoItemRequest> { new() { ProdutoId = P1, Quantidade = 1 } };

            var ex = await Assert.ThrowsAsync<DeliveryDomainException>(() => service.CreateAsync(CoreFixtures.Est, 1, request, null));
            Assert.Equal("STORE_NOT_ACCEPTING", ex.Code);
            queue.Verify(q => q.CreatePedidoAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<ManualOrder>()), Times.Never);
        }

        [Fact]
        public async Task Attendant_ViolationsBecomeWarnings_AndOrderIsStillCreated()
        {
            var product = CoreFixtures.Product(P1, 10m);
            var (service, queue, _) = Create(CoreFixtures.Restaurant(accepts: false, minimum: 50m), product);
            var request = CoreFixtures.Request();
            request.Itens = new List<PedidoItemRequest> { new() { ProdutoId = P1, Quantidade = 1 } };
            request.TaxaEntrega = 3m;

            await service.CreateAsync(CoreFixtures.Est, 1, request, null);

            var order = Captured(queue);
            Assert.Equal(2, order.Avisos.Count);
            Assert.Equal(13m, order.Value);
        }

        [Fact]
        public async Task UnknownOrigin_IsRejected()
        {
            var (service, _, _) = Create();
            var request = CoreFixtures.Request();
            request.Origem = "telepatia";
            var ex = await Assert.ThrowsAsync<DeliveryDomainException>(() => service.CreateAsync(CoreFixtures.Est, 1, request, null));
            Assert.Equal("INVALID_ORIGIN", ex.Code);
        }

        [Fact]
        public async Task DraftAndConversation_AreCarriedToTheRepository()
        {
            var (service, queue, _) = Create();
            var conversa = Guid.NewGuid();
            var request = CoreFixtures.Request();
            request.Rascunho = true;
            request.ConversaId = conversa;

            await service.CreateAsync(CoreFixtures.Est, 1, request, null);

            var order = Captured(queue);
            Assert.True(order.Rascunho);
            Assert.Equal(conversa, order.ConversaId);
            Assert.True(order.NeedsCoreSchema);
        }

        [Fact]
        public async Task DefaultForecast_ComesFromDeliverySettings()
        {
            var (service, queue, _) = Create();
            await service.CreateAsync(CoreFixtures.Est, 1, CoreFixtures.Request(), null);
            Assert.Equal(45, Captured(queue).PrevisaoMinutos);
        }

        [Fact]
        public async Task Update_DropsOrigemRef_AndConfirmUsesDefaultMinutes()
        {
            var (service, queue, _) = Create();
            queue.Setup(q => q.UpdatePedidoAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<ManualOrder>()))
                .ReturnsAsync(new CreatedPedidoDto { Id = 7 });
            queue.Setup(q => q.ConfirmPedidoAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()))
                .ReturnsAsync(new CreatedPedidoDto { Id = 7 });
            var request = CoreFixtures.Request();
            request.OrigemRef = "abc";

            await service.UpdateAsync(CoreFixtures.Est, 1, 7, request);
            await service.ConfirmAsync(CoreFixtures.Est, 1, 7);

            queue.Verify(q => q.UpdatePedidoAsync(CoreFixtures.Est, 1, 7, It.Is<ManualOrder>(o => o.OrigemRef == null)), Times.Once);
            queue.Verify(q => q.ConfirmPedidoAsync(CoreFixtures.Est, 1, 7, 45), Times.Once);
            await Assert.ThrowsAsync<DeliveryDomainException>(() => service.ConfirmAsync(CoreFixtures.Est, 1, 0));
        }

        [Fact]
        public void LegacyItemsJson_HasNameQuantityPrice_WithAddonsIncluded()
        {
            var lines = new[]
            {
                new PricedLine
                {
                    Nome = "X-Bacon", Quantidade = 2, PrecoUnitario = 20m,
                    Adicionais = new[] { new PricedAddon { Id = Guid.NewGuid(), Nome = "Queijo", Preco = 3m } }
                }
            };

            var json = PedidoCoreService.LegacyItemsJson(lines);

            Assert.Equal("[{\"nome\":\"X-Bacon + Queijo\",\"quantidade\":2,\"preco\":23}]", json);
        }
    }

    public sealed class ModuleDependenciesTests
    {
        [Fact]
        public void Close_AddsRequiredModules_Transitively()
        {
            var closed = ModuleDependencies.Close(new[] { "CARDAPIOWEB" });
            Assert.Contains("CARDAPIO", closed);
            Assert.Contains("PEDIDOS", closed);

            Assert.Contains("PEDIDOS", ModuleDependencies.Close(new[] { "DELIVERY" }));
        }

        [Fact]
        public void Close_DoesNotAddWhatIsNotRequired_AndIsCaseInsensitive()
        {
            var closed = ModuleDependencies.Close(new[] { "whatsapp", "delivery" });
            Assert.DoesNotContain("CARDAPIO", closed);
            Assert.Contains("PEDIDOS", closed);
            Assert.Contains("whatsapp", closed);
        }

        [Fact]
        public void Mapper_ToDatabaseModules_AppliesDependencies()
        {
            var modules = EstabelecimentoModuleMapper.ToDatabaseModules(new[] { "Delivery" }, "restaurante");
            Assert.Contains("DELIVERY", modules);
            Assert.Contains("PEDIDOS", modules);
            Assert.Contains("GERAL", modules);

            var web = EstabelecimentoModuleMapper.ToDatabaseModules(new[] { "CardapioWeb" }, "restaurante");
            Assert.Contains("CARDAPIO", web);
            Assert.Contains("PEDIDOS", web);
        }

        [Fact]
        public void Mapper_PedidosRoundTrips_BetweenUiAndDatabase()
        {
            Assert.Contains("PEDIDOS", EstabelecimentoModuleMapper.ToDatabaseModules(new[] { "Pedidos" }, "restaurante"));
            Assert.Contains("Pedidos", EstabelecimentoModuleMapper.ToUiModules("Loja", new[] { "PEDIDOS" }));
        }

        [Fact]
        public void RequiredBy_ExplainsWhyAModuleCannotBeRemoved()
        {
            var by = ModuleDependencies.RequiredBy("PEDIDOS", new[] { "DELIVERY", "WHATSAPP" });
            Assert.Equal(new[] { "DELIVERY" }, by);
        }
    }

    public sealed class CoreMigrationContractTests
    {
        private static string Read(string file) => System.IO.File.ReadAllText(
            System.IO.Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", file));

        [Fact]
        public void ModuleEnum_IsAloneInItsFile_AndBackfillIsSeparate()
        {
            var enumSql = Read("20260925_01_modulo_pedidos.sql");
            Assert.Contains("ALTER TYPE modulo_enum ADD VALUE IF NOT EXISTS 'PEDIDOS'", enumSql, StringComparison.Ordinal);
            // o valor novo nao pode ser usado na mesma transacao que o cria
            Assert.DoesNotContain("UPDATE ", enumSql, StringComparison.OrdinalIgnoreCase);

            var backfill = Read("20260925_04_modulo_backfill.sql");
            Assert.Contains("'PEDIDOS'::modulo_enum", backfill, StringComparison.Ordinal);
            Assert.DoesNotContain("DELETE ", backfill, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Schema_IsAdditiveAndIdempotent()
        {
            var sql = Read("20260925_02_schema.sql");
            foreach (var column in new[] { "origem", "origem_ref", "conversa_id", "cliente_id", "subtotal", "taxa_entrega", "desconto" })
            {
                Assert.Contains($"ADD COLUMN IF NOT EXISTS {column}", sql, StringComparison.OrdinalIgnoreCase);
            }
            Assert.Contains("CREATE TABLE IF NOT EXISTS pedido_item", sql, StringComparison.Ordinal);
            Assert.Contains("ON DELETE CASCADE", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("DROP ", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ALTER COLUMN", sql, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Constraints_EnforceOriginAndIdempotencyPerStore()
        {
            var sql = Read("20260925_05_constraints.sql");
            Assert.Contains("ck_pedido_origem", sql, StringComparison.Ordinal);
            Assert.Contains("ux_pedido_origem_ref", sql, StringComparison.Ordinal);
            Assert.Contains("(id_estabelecimento, origem, origem_ref)", sql, StringComparison.Ordinal);
            Assert.Contains("WHERE origem_ref IS NOT NULL", sql, StringComparison.Ordinal);
        }

        [Fact]
        public void Backfill_OnlyTouchesIfoodOrders_AndNeverDeletes()
        {
            var sql = Read("20260925_03_backfill.sql");
            Assert.Contains("id_ifood IS NOT NULL", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("DELETE ", sql, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void EveryApplicableMigration_RegistersItselfInTheLedger()
        {
            foreach (var file in new[] { "20260925_01_modulo_pedidos.sql", "20260925_02_schema.sql", "20260925_03_backfill.sql",
                         "20260925_04_modulo_backfill.sql", "20260925_05_constraints.sql" })
            {
                var name = System.IO.Path.GetFileNameWithoutExtension(file);
                Assert.Contains($"VALUES ('{name}')", Read(file), StringComparison.Ordinal);
            }
        }

        [Fact]
        public void VerifyFile_IsSkippedByTheMigrationRunner()
        {
            // O runner ignora arquivos terminados em _verify.sql / _preflight.sql.
            Assert.EndsWith("_verify.sql", "20260925_06_verify.sql", StringComparison.Ordinal);
            Assert.Contains("SELECT", Read("20260925_06_verify.sql"), StringComparison.Ordinal);
        }
    }
}
