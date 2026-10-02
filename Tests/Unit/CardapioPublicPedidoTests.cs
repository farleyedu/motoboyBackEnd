using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using APIBack.Automation.Interfaces;
using APIBack.DTOs.Cardapio;
using APIBack.Model.Cardapio;
using APIBack.Repository.Interface;
using APIBack.Service;
using APIBack.Service.Interface;
using Moq;
using Xunit;

namespace APIBack.Tests.Unit
{
    public class CardapioPublicPedidoTests
    {
        private static readonly Guid Estabelecimento = Guid.Parse("11111111-1111-1111-1111-111111111111");
        private static readonly Guid ProdutoId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        private static readonly Guid GrupoBacon = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        private static readonly Guid ItemBacon = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

        private sealed class Fixture
        {
            public Mock<ICardapioRepository> Cardapio { get; } = new();
            public Mock<ILocalizacaoService> Localizacao { get; } = new();
            public Mock<IWabaPhoneRepository> Waba { get; } = new();
            public Mock<ICardapioPedidoWebService> Confirmacao { get; } = new();
            public CardapioPedidoPublico? Gravado { get; private set; }

            public Fixture()
            {
                Cardapio.Setup(c => c.ObterEstabelecimentoPublicoAsync(It.IsAny<Guid?>(), It.IsAny<string?>()))
                    .ReturnsAsync(new CardapioEstabelecimentoPublico
                    {
                        Id = Estabelecimento,
                        NomeFantasia = "Pizza Bom",
                        NomePublico = "Pizza Bom Centro",
                        Publicado = true,
                        AceitaPedidos = true,
                        TaxaEntregaFixa = 5m,
                        ModulosAtivosRaw = new[] { "cardapio", "cardapioweb" }
                    });
                Cardapio.Setup(c => c.ListarProdutosPublicosPorIdsAsync(Estabelecimento, It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<bool>()))
                    .ReturnsAsync(new[] { Produto() });
                Cardapio.Setup(c => c.CriarPedidoPublicoAsync(It.IsAny<CardapioPedidoPublico>()))
                    .Callback<CardapioPedidoPublico>(entity => Gravado = entity)
                    .ReturnsAsync(Guid.NewGuid());
                Localizacao.Setup(l => l.ObterCoordenadasAsync(It.IsAny<string>())).ReturnsAsync(("-18,9186", "-48.2772"));
                Waba.Setup(w => w.ObterDisplayPhonePorEstabelecimentoAsync(Estabelecimento)).ReturnsAsync("+55 34 3333-0000");
                Confirmacao.Setup(c => c.IniciarConfirmacaoAsync(It.IsAny<CardapioPedidoPublico>(), It.IsAny<string>()))
                    .ReturnsAsync(new CardapioConfirmacaoDto { Modo = "codigo", Codigo = "4821" });
            }

            public CardapioPublicService Build() => new(Cardapio.Object, Localizacao.Object, Waba.Object, Confirmacao.Object);
        }

        private static CardapioProduto Produto() => new()
        {
            Id = ProdutoId,
            Nome = "X-Bacon",
            PrecoBase = 30m,
            Grupos = new List<CardapioGrupoAdicional>
            {
                new()
                {
                    Id = GrupoBacon,
                    Nome = "Bacon extra",
                    Tipo = "adicional_global",
                    MinSelecionados = 0,
                    MaxSelecionados = 1,
                    Itens = new List<CardapioGrupoAdicionalItem>
                    {
                        new() { Id = ItemBacon, IdGrupo = GrupoBacon, Nome = "Bacon extra", Preco = 4.5m, Ativo = true }
                    }
                }
            }
        };

        private static CriarCardapioPedidoPublicoRequest Pedido(
            string tipoEntrega = "entrega",
            string telefone = "(34) 99123-0001",
            params Guid[] adicionais) => new()
        {
            EstabelecimentoId = Estabelecimento,
            TipoEntrega = tipoEntrega,
            Itens = new List<CardapioPedidoPublicoItemRequest>
            {
                new() { ProdutoId = ProdutoId, Quantidade = 2, Observacao = "sem cebola", AdicionalItemIds = adicionais.ToList() }
            },
            Cliente = new CriarCardapioPedidoPublicoClienteRequest { Nome = "Maria Souza", Telefone = telefone },
            FormaPagamento = "Pix",
            EnderecoEntrega = tipoEntrega == "entrega"
                ? new CriarCardapioPedidoPublicoEnderecoRequest
                {
                    Logradouro = "Rua das Flores", Numero = "120", Bairro = "Centro", Cidade = "Uberlandia", Uf = "mg", Cep = "38400-000"
                }
                : null
        };

        // =====================================================================
        // Adicionais: o cardapio web manda o id do adicional (o grupo)
        // =====================================================================

        [Fact]
        public async Task The_quote_accepts_the_extras_id_and_prices_it_with_its_item()
        {
            var cotacao = await new Fixture().Build().CalcularCotacaoAsync(Pedido(adicionais: GrupoBacon));

            var item = Assert.Single(cotacao.Itens);
            var adicional = Assert.Single(item.AdicionaisSelecionados);
            Assert.Equal(ItemBacon, adicional.Id);
            Assert.Equal(4.5m, adicional.Preco);
            Assert.Equal(60m, item.TotalProduto);
            Assert.Equal(9m, item.TotalAdicionais);
            Assert.Equal(69m + 5m, cotacao.Total);
        }

        [Fact]
        public async Task The_quote_still_accepts_the_item_id_and_never_counts_the_same_extra_twice()
        {
            var fixture = new Fixture();

            var porItem = await fixture.Build().CalcularCotacaoAsync(Pedido(adicionais: ItemBacon));
            var porAmbos = await fixture.Build().CalcularCotacaoAsync(Pedido(adicionais: new[] { GrupoBacon, ItemBacon }));

            Assert.Equal(9m, porItem.Itens.Single().TotalAdicionais);
            Assert.Equal(9m, porAmbos.Itens.Single().TotalAdicionais);
            Assert.Single(porAmbos.Itens.Single().AdicionaisSelecionados);
        }

        [Fact]
        public async Task An_id_that_is_neither_an_item_nor_an_extra_of_the_product_is_rejected()
        {
            var erro = await Assert.ThrowsAsync<RequestValidationException>(() =>
                new Fixture().Build().CalcularCotacaoAsync(Pedido(adicionais: Guid.NewGuid())));

            Assert.Contains(erro.Errors.Keys, chave => chave.EndsWith("adicionalItemIds", StringComparison.Ordinal));
        }

        // =====================================================================
        // Criar o pedido
        // =====================================================================

        [Fact]
        public async Task The_order_keeps_what_the_server_understood_the_phone_in_e164_and_the_found_coordinates()
        {
            var fixture = new Fixture();

            var criado = await fixture.Build().CriarPedidoAsync(Pedido(adicionais: GrupoBacon));

            var gravado = fixture.Gravado!;
            Assert.Equal("+5534991230001", gravado.TelefoneCliente);
            Assert.Equal("entrega", gravado.TipoEntrega);
            Assert.Equal(74m, gravado.Total);

            using var itens = JsonDocument.Parse(gravado.ItensJson);
            var solicitado = itens.RootElement.GetProperty("solicitado")[0];
            Assert.Equal(ProdutoId, solicitado.GetProperty("produtoId").GetGuid());
            Assert.Equal(2, solicitado.GetProperty("quantidade").GetInt32());
            // Id de ITEM: e o que o nucleo do delivery precifica no aceite.
            Assert.Equal(ItemBacon, solicitado.GetProperty("adicionalItemIds")[0].GetGuid());

            var endereco = JsonSerializer.Deserialize<CardapioEnderecoArmazenado>(gravado.EnderecoEntregaJson!, Web)!;
            Assert.Equal(-18.9186, endereco.Latitude);
            Assert.Equal(-48.2772, endereco.Longitude);
            Assert.Equal("MG", endereco.Uf);

            Assert.Equal("codigo", criado.Confirmacao.Modo);
            Assert.Equal(CardapioPedidoStatus.AguardandoCodigo, criado.Status);
            // O nome que o cliente le na mensagem e o publico da loja.
            fixture.Confirmacao.Verify(c => c.IniciarConfirmacaoAsync(gravado, "Pizza Bom Centro"), Times.Once);
        }

        [Fact]
        public async Task A_message_already_sent_means_the_order_is_already_waiting_for_the_restaurant()
        {
            var fixture = new Fixture();
            fixture.Confirmacao.Setup(c => c.IniciarConfirmacaoAsync(It.IsAny<CardapioPedidoPublico>(), It.IsAny<string>()))
                .ReturnsAsync(new CardapioConfirmacaoDto { Modo = "mensagem_enviada" });

            var criado = await fixture.Build().CriarPedidoAsync(Pedido());

            Assert.Equal(CardapioPedidoStatus.AguardandoAceite, criado.Status);
        }

        [Theory]
        [InlineData("")]
        [InlineData("12345")]
        [InlineData("abc")]
        public async Task A_phone_that_cannot_be_a_whatsapp_number_is_rejected_before_anything_is_saved(string telefone)
        {
            var fixture = new Fixture();

            var erro = await Assert.ThrowsAsync<RequestValidationException>(() => fixture.Build().CriarPedidoAsync(Pedido(telefone: telefone)));

            Assert.Contains("cliente.telefone", erro.Errors.Keys);
            Assert.Null(fixture.Gravado);
        }

        [Fact]
        public async Task A_delivery_address_not_found_on_the_map_is_a_validation_error_and_nothing_is_saved()
        {
            var fixture = new Fixture();
            fixture.Localizacao.Setup(l => l.ObterCoordenadasAsync(It.IsAny<string>())).ReturnsAsync(((string, string)?)null);

            var erro = await Assert.ThrowsAsync<RequestValidationException>(() => fixture.Build().CriarPedidoAsync(Pedido()));

            Assert.Contains("enderecoEntrega", erro.Errors.Keys);
            Assert.Null(fixture.Gravado);
            fixture.Confirmacao.Verify(c => c.IniciarConfirmacaoAsync(It.IsAny<CardapioPedidoPublico>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task A_map_service_failure_never_creates_an_order_without_a_position()
        {
            var fixture = new Fixture();
            fixture.Localizacao.Setup(l => l.ObterCoordenadasAsync(It.IsAny<string>())).ThrowsAsync(new InvalidOperationException("mapa fora"));

            await Assert.ThrowsAsync<RequestValidationException>(() => fixture.Build().CriarPedidoAsync(Pedido()));

            Assert.Null(fixture.Gravado);
        }

        [Fact]
        public async Task Unreadable_coordinates_are_treated_as_address_not_found()
        {
            var fixture = new Fixture();
            fixture.Localizacao.Setup(l => l.ObterCoordenadasAsync(It.IsAny<string>())).ReturnsAsync(("abc", "-48.2"));

            await Assert.ThrowsAsync<RequestValidationException>(() => fixture.Build().CriarPedidoAsync(Pedido()));

            Assert.Null(fixture.Gravado);
        }

        [Fact]
        public async Task A_pickup_order_needs_no_address_and_never_calls_the_map()
        {
            var fixture = new Fixture();

            await fixture.Build().CriarPedidoAsync(Pedido(tipoEntrega: "retirada"));

            Assert.Equal("retirada", fixture.Gravado!.TipoEntrega);
            Assert.Null(fixture.Gravado.EnderecoEntregaJson);
            fixture.Localizacao.Verify(l => l.ObterCoordenadasAsync(It.IsAny<string>()), Times.Never);
        }

        [Theory]
        [InlineData("MGX", "38400-000", "enderecoEntrega.uf")]
        [InlineData("M1", "38400-000", "enderecoEntrega.uf")]
        [InlineData("MG", "384", "enderecoEntrega.cep")]
        public async Task The_address_fields_the_core_would_refuse_are_refused_here_first(string uf, string cep, string campo)
        {
            var fixture = new Fixture();
            var pedido = Pedido();
            pedido.EnderecoEntrega!.Uf = uf;
            pedido.EnderecoEntrega.Cep = cep;

            var erro = await Assert.ThrowsAsync<RequestValidationException>(() => fixture.Build().CriarPedidoAsync(pedido));

            Assert.Contains(campo, erro.Errors.Keys);
            Assert.Null(fixture.Gravado);
        }

        [Fact]
        public async Task Notes_longer_than_the_core_accepts_are_refused_here_not_at_acceptance()
        {
            var pedido = Pedido();
            pedido.Observacoes = new string('x', 501);

            var erro = await Assert.ThrowsAsync<RequestValidationException>(() => new Fixture().Build().CriarPedidoAsync(pedido));

            Assert.Contains("observacoes", erro.Errors.Keys);
        }

        [Theory]
        [InlineData("nome", 151, "cliente.nome")]
        [InlineData("pagamento", 51, "formaPagamento")]
        [InlineData("rua", 201, "enderecoEntrega.logradouro")]
        [InlineData("numero", 21, "enderecoEntrega.numero")]
        [InlineData("complemento", 101, "enderecoEntrega.complemento")]
        [InlineData("bairro", 101, "enderecoEntrega.bairro")]
        [InlineData("cidade", 101, "enderecoEntrega.cidade")]
        public async Task Fields_longer_than_the_delivery_order_accepts_are_refused_here_not_at_acceptance(string campo, int tamanho, string chave)
        {
            var fixture = new Fixture();
            var pedido = Pedido();
            var longo = new string('x', tamanho);
            switch (campo)
            {
                case "nome": pedido.Cliente.Nome = longo; break;
                case "pagamento": pedido.FormaPagamento = longo; break;
                case "rua": pedido.EnderecoEntrega!.Logradouro = longo; break;
                case "numero": pedido.EnderecoEntrega!.Numero = longo; break;
                case "complemento": pedido.EnderecoEntrega!.Complemento = longo; break;
                case "bairro": pedido.EnderecoEntrega!.Bairro = longo; break;
                case "cidade": pedido.EnderecoEntrega!.Cidade = longo; break;
            }

            var erro = await Assert.ThrowsAsync<RequestValidationException>(() => fixture.Build().CriarPedidoAsync(pedido));

            Assert.Contains(chave, erro.Errors.Keys);
            Assert.Null(fixture.Gravado);
        }

        [Fact]
        public async Task A_store_without_a_whatsapp_number_cannot_take_orders()
        {
            var fixture = new Fixture();
            fixture.Waba.Setup(w => w.ObterDisplayPhonePorEstabelecimentoAsync(Estabelecimento)).ReturnsAsync((string?)null);

            var erro = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Build().CriarPedidoAsync(Pedido()));

            Assert.Contains("WhatsApp", erro.Message);
            Assert.Null(fixture.Gravado);
        }
    }
}
