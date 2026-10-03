using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Models;
using APIBack.DTOs.Cardapio;
using APIBack.DTOs.Delivery;
using APIBack.Model.Cardapio;
using APIBack.Repository.Interface;
using APIBack.Service;
using APIBack.Service.Interface;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace APIBack.Tests.Unit
{
    public class CardapioPedidoWebServiceTests
    {
        private static readonly Guid Estabelecimento = Guid.Parse("11111111-1111-1111-1111-111111111111");
        private static readonly Guid Conversa = Guid.Parse("22222222-2222-2222-2222-222222222222");
        private static readonly Guid Cliente = Guid.Parse("33333333-3333-3333-3333-333333333333");
        private static readonly Guid ProdutoId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        private static readonly Guid AdicionalId = Guid.Parse("55555555-5555-5555-5555-555555555555");
        private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

        private sealed class Fixture
        {
            public Mock<ICardapioPedidoWebRepository> Repo { get; } = new();
            public Mock<ICardapioRepository> Cardapio { get; } = new();
            public Mock<IConversationRepository> Conversas { get; } = new();
            public Mock<IClienteRepository> Clientes { get; } = new();
            public Mock<IWabaPhoneRepository> Waba { get; } = new();
            public Mock<ITrackingNoticeSender> Sender { get; } = new();
            public Mock<IPedidoCoreService> Core { get; } = new();
            public List<string> Enviadas { get; } = new();

            public Fixture()
            {
                Cardapio.Setup(c => c.ObterEstabelecimentoPublicoAsync(It.IsAny<Guid?>(), It.IsAny<string?>()))
                    .ReturnsAsync(new CardapioEstabelecimentoPublico { Id = Estabelecimento, NomeFantasia = "Pizza Bom" });
                Waba.Setup(w => w.ObterDisplayPhoneParaServicoAsync(Estabelecimento, "cardapio_web")).ReturnsAsync("+55 34 3333-0000");
                Sender.Setup(s => s.SendAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>()))
                    .Callback<Guid, Guid, string>((_, _, texto) => Enviadas.Add(texto))
                    .ReturnsAsync(Guid.NewGuid());
                Conversas.Setup(c => c.ObterPorIdAsync(Conversa, It.IsAny<Guid?>()))
                    .ReturnsAsync(new Conversation { IdConversa = Conversa, IdEstabelecimento = Estabelecimento, IdCliente = Cliente });
                Clientes.Setup(c => c.ObterTelefoneClienteAsync(Cliente, Estabelecimento)).ReturnsAsync("+5534991230001");
                Repo.Setup(r => r.ObterConversaPorTelefoneAsync(Estabelecimento, It.IsAny<IReadOnlyList<string>>()))
                    .ReturnsAsync((ConversaPorTelefone?)null);
            }

            public CardapioPedidoWebService Build() => new(
                Repo.Object, Cardapio.Object, Conversas.Object, Clientes.Object, Waba.Object,
                Sender.Object, Core.Object, new MemoryCache(new MemoryCacheOptions()),
                NullLogger<CardapioPedidoWebService>.Instance);
        }

        private static CardapioPedidoPublico Pedido(
            string status = CardapioPedidoStatus.Pendente,
            string tipoEntrega = "entrega",
            Guid? conversa = null)
        {
            var itens = JsonSerializer.Serialize(new
            {
                solicitado = new[]
                {
                    new CardapioPedidoPublicoItemRequest
                    {
                        ProdutoId = ProdutoId, Quantidade = 2, Observacao = "sem cebola",
                        AdicionalItemIds = new List<Guid> { AdicionalId }
                    }
                },
                cotacao = new[]
                {
                    new CardapioCotacaoItemDto
                    {
                        ProdutoId = ProdutoId, ProdutoNome = "X-Bacon", Quantidade = 2, PrecoUnitario = 30m,
                        TotalProduto = 60m, TotalAdicionais = 4.9m, TotalItem = 64.9m, Observacao = "sem cebola",
                        AdicionaisSelecionados = new List<CardapioCotacaoAdicionalDto>
                        {
                            new() { Id = AdicionalId, Nome = "Bacon extra", Preco = 2.45m }
                        }
                    }
                }
            }, Web);

            var endereco = JsonSerializer.Serialize(new CardapioEnderecoArmazenado
            {
                Logradouro = "Rua das Flores", Numero = "120", Complemento = "ap 4", Bairro = "Centro",
                Cidade = "Uberlandia", Uf = "MG", Cep = "38400-000", Referencia = "portao azul",
                Latitude = -18.9186, Longitude = -48.2772
            }, Web);

            return new CardapioPedidoPublico
            {
                Id = Guid.Parse("66666666-6666-6666-6666-666666666666"),
                IdEstabelecimento = Estabelecimento,
                Codigo = "CDP-ABC12345",
                Status = status,
                TipoEntrega = tipoEntrega,
                NomeCliente = "Maria Souza",
                TelefoneCliente = "+5534991230001",
                FormaPagamento = "Pix",
                Observacoes = "tocar a campainha",
                Total = 64.9m,
                ItensJson = itens,
                EnderecoEntregaJson = tipoEntrega == "entrega" ? endereco : null,
                IdConversa = conversa,
                CreatedAt = DateTime.UtcNow
            };
        }

        private static CardapioPedidoPublico ComCodigo(CardapioPedidoPublico pedido, string codigo)
        {
            pedido.Status = CardapioPedidoStatus.AguardandoCodigo;
            pedido.CodigoConfirmacao = codigo;
            pedido.CodigoExpiraEm = DateTimeOffset.UtcNow.AddMinutes(5);
            return pedido;
        }

        // =====================================================================
        // Iniciar a confirmacao
        // =====================================================================

        [Fact]
        public async Task Open_window_sends_the_message_and_goes_straight_to_the_restaurant()
        {
            var f = new Fixture();
            var pedido = Pedido();
            f.Repo.Setup(r => r.ObterConversaPorTelefoneAsync(Estabelecimento, It.IsAny<IReadOnlyList<string>>()))
                .ReturnsAsync(new ConversaPorTelefone(Conversa, DateTimeOffset.UtcNow.AddHours(5)));
            f.Repo.Setup(r => r.MarcarAguardandoAceiteAsync(pedido.Id, "+5534991230001", Conversa)).ReturnsAsync(true);

            var confirmacao = await f.Build().IniciarConfirmacaoAsync(pedido, "Pizza Bom");

            Assert.Equal("mensagem_enviada", confirmacao.Modo);
            Assert.Null(confirmacao.Codigo);
            f.Repo.Verify(r => r.MarcarAguardandoAceiteAsync(pedido.Id, "+5534991230001", Conversa), Times.Once);
            f.Repo.Verify(r => r.DefinirCodigoAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<TimeSpan>()), Times.Never);
            var texto = Assert.Single(f.Enviadas);
            Assert.Contains("Total: R$ 64,90", texto);
            Assert.Contains("2x X-Bacon (+ Bacon extra)", texto);
        }

        [Fact]
        public async Task The_restaurant_is_only_notified_after_the_whatsapp_accepted_the_message()
        {
            var f = new Fixture();
            var pedido = Pedido();
            f.Repo.Setup(r => r.ObterConversaPorTelefoneAsync(Estabelecimento, It.IsAny<IReadOnlyList<string>>()))
                .ReturnsAsync(new ConversaPorTelefone(Conversa, DateTimeOffset.UtcNow.AddHours(1)));
            f.Sender.Setup(s => s.SendAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>()))
                .ThrowsAsync(new InvalidOperationException("janela fechada"));
            f.Repo.Setup(r => r.DefinirCodigoAsync(pedido.Id, It.IsAny<string>(), CardapioConfirmacaoRules.CodigoValidade))
                .ReturnsAsync((ComCodigo(Pedido(), "4821"), false));

            var confirmacao = await f.Build().IniciarConfirmacaoAsync(pedido, "Pizza Bom");

            // Envio recusado (a janela fechou no meio): cai no codigo, sem notificar o restaurante.
            Assert.Equal("codigo", confirmacao.Modo);
            Assert.Equal("4821", confirmacao.Codigo);
            f.Repo.Verify(r => r.MarcarAguardandoAceiteAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Guid>()), Times.Never);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Without_an_open_window_the_client_gets_a_code_and_a_prefilled_whatsapp_link(bool conversaComJanelaVencida)
        {
            var f = new Fixture();
            var pedido = Pedido();
            if (conversaComJanelaVencida)
            {
                f.Repo.Setup(r => r.ObterConversaPorTelefoneAsync(Estabelecimento, It.IsAny<IReadOnlyList<string>>()))
                    .ReturnsAsync(new ConversaPorTelefone(Conversa, DateTimeOffset.UtcNow.AddHours(-2)));
            }
            f.Repo.Setup(r => r.DefinirCodigoAsync(pedido.Id, It.IsAny<string>(), CardapioConfirmacaoRules.CodigoValidade))
                .ReturnsAsync((ComCodigo(Pedido(), "4821"), false));

            var confirmacao = await f.Build().IniciarConfirmacaoAsync(pedido, "Pizza Bom");

            Assert.Equal("codigo", confirmacao.Modo);
            Assert.Equal("4821", confirmacao.Codigo);
            Assert.NotNull(confirmacao.ExpiraEm);
            Assert.StartsWith("https://wa.me/553433330000?text=", confirmacao.WhatsappUrl);
            f.Sender.Verify(s => s.SendAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
            f.Repo.Verify(r => r.ExpirarCodigosVencidosAsync(Estabelecimento), Times.Once);
        }

        [Fact]
        public async Task The_phone_lookup_tries_with_and_without_the_ninth_digit()
        {
            var f = new Fixture();
            var pedido = Pedido();
            IReadOnlyList<string>? variantes = null;
            f.Repo.Setup(r => r.ObterConversaPorTelefoneAsync(Estabelecimento, It.IsAny<IReadOnlyList<string>>()))
                .Callback<Guid, IReadOnlyList<string>>((_, v) => variantes = v)
                .ReturnsAsync((ConversaPorTelefone?)null);
            f.Repo.Setup(r => r.DefinirCodigoAsync(pedido.Id, It.IsAny<string>(), It.IsAny<TimeSpan>()))
                .ReturnsAsync((ComCodigo(Pedido(), "4821"), false));

            await f.Build().IniciarConfirmacaoAsync(pedido, "Pizza Bom");

            Assert.Equal(new[] { "5534991230001", "553491230001" }, variantes);
        }

        [Fact]
        public async Task A_code_that_collides_with_another_active_one_is_redrawn()
        {
            var f = new Fixture();
            var pedido = Pedido();
            f.Repo.SetupSequence(r => r.DefinirCodigoAsync(pedido.Id, It.IsAny<string>(), It.IsAny<TimeSpan>()))
                .ReturnsAsync(((CardapioPedidoPublico?)null, true))
                .ReturnsAsync(((CardapioPedidoPublico?)null, true))
                .ReturnsAsync((ComCodigo(Pedido(), "7305"), false));

            var confirmacao = await f.Build().IniciarConfirmacaoAsync(pedido, "Pizza Bom");

            Assert.Equal("7305", confirmacao.Codigo);
            f.Repo.Verify(r => r.DefinirCodigoAsync(pedido.Id, It.IsAny<string>(), It.IsAny<TimeSpan>()), Times.Exactly(3));
        }

        [Fact]
        public async Task A_status_that_does_not_accept_a_code_is_a_conflict_not_an_endless_retry()
        {
            var f = new Fixture();
            f.Repo.Setup(r => r.ObterAsync(It.IsAny<Guid>())).ReturnsAsync(Pedido(CardapioPedidoStatus.Aceito));
            f.Repo.Setup(r => r.DefinirCodigoAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<TimeSpan>()))
                .ReturnsAsync(((CardapioPedidoPublico?)null, false));

            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Build().GerarNovoCodigoAsync(Guid.NewGuid()));

            f.Repo.Verify(r => r.DefinirCodigoAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<TimeSpan>()), Times.Once);
        }

        // =====================================================================
        // Status publico
        // =====================================================================

        [Fact]
        public async Task Status_hides_the_code_once_it_expired_even_if_the_row_was_not_swept_yet()
        {
            var f = new Fixture();
            var pedido = ComCodigo(Pedido(), "4821");
            pedido.CodigoExpiraEm = DateTimeOffset.UtcNow.AddSeconds(-1);
            f.Repo.Setup(r => r.ObterAsync(pedido.Id)).ReturnsAsync(pedido);

            var status = await f.Build().ObterStatusAsync(pedido.Id);

            Assert.Equal(CardapioPedidoStatus.Expirado, status!.Status);
            Assert.Null(status.Confirmacao);
        }

        [Fact]
        public async Task Status_while_waiting_for_the_code_returns_the_code_and_the_link()
        {
            var f = new Fixture();
            var pedido = ComCodigo(Pedido(), "4821");
            f.Repo.Setup(r => r.ObterAsync(pedido.Id)).ReturnsAsync(pedido);

            var status = await f.Build().ObterStatusAsync(pedido.Id);

            Assert.Equal(CardapioPedidoStatus.AguardandoCodigo, status!.Status);
            Assert.Equal("4821", status.Confirmacao!.Codigo);
            Assert.NotNull(status.Confirmacao.WhatsappUrl);
        }

        [Fact]
        public async Task Status_shows_the_reason_only_for_declined_orders_and_the_number_once_accepted()
        {
            var f = new Fixture();
            var recusado = Pedido(CardapioPedidoStatus.Recusado);
            recusado.MotivoRecusa = "item em falta";
            var aceito = Pedido(CardapioPedidoStatus.Aceito);
            aceito.MotivoRecusa = "nao deve aparecer";
            aceito.IdPedido = 77;
            f.Repo.Setup(r => r.ObterAsync(recusado.Id)).ReturnsAsync(recusado);
            var service = f.Build();

            Assert.Equal("item em falta", (await service.ObterStatusAsync(recusado.Id))!.MotivoRecusa);

            f.Repo.Setup(r => r.ObterAsync(aceito.Id)).ReturnsAsync(aceito);
            var statusAceito = await service.ObterStatusAsync(aceito.Id);
            Assert.Null(statusAceito!.MotivoRecusa);
            Assert.Equal(77, statusAceito.NumeroPedido);
        }

        [Fact]
        public async Task Unknown_order_has_no_status()
        {
            var f = new Fixture();
            f.Repo.Setup(r => r.ObterAsync(It.IsAny<Guid>())).ReturnsAsync((CardapioPedidoPublico?)null);

            Assert.Null(await f.Build().ObterStatusAsync(Guid.NewGuid()));
        }

        // =====================================================================
        // Mensagem recebida pelo webhook
        // =====================================================================

        [Fact]
        public async Task An_active_code_sent_by_the_client_confirms_with_the_senders_number()
        {
            var f = new Fixture();
            var pedido = ComCodigo(Pedido(conversa: Conversa), "4821");
            f.Repo.Setup(r => r.ConfirmarPorCodigoAsync(Estabelecimento, "4821", "+5534991230001", Conversa)).ReturnsAsync(pedido);

            var tratada = await f.Build().TentarConfirmarPorMensagemAsync(Conversa, "Olá! Quero confirmar meu pedido. Código: 4821");

            Assert.True(tratada);
            var texto = Assert.Single(f.Enviadas);
            Assert.Contains("Código confirmado", texto);
            Assert.Contains("Total: R$ 64,90", texto);
        }

        [Fact]
        public async Task A_plain_message_never_touches_the_database()
        {
            var f = new Fixture();

            var tratada = await f.Build().TentarConfirmarPorMensagemAsync(Conversa, "Boa noite, vocês abrem hoje?");

            Assert.False(tratada);
            f.Conversas.Verify(c => c.ObterPorIdAsync(It.IsAny<Guid>(), It.IsAny<Guid?>()), Times.Never);
            f.Repo.Verify(r => r.ConfirmarPorCodigoAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>()), Times.Never);
        }

        [Fact]
        public async Task A_bare_number_that_matches_no_order_stays_a_normal_message()
        {
            var f = new Fixture();
            f.Repo.Setup(r => r.ConfirmarPorCodigoAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>()))
                .ReturnsAsync((CardapioPedidoPublico?)null);

            var tratada = await f.Build().TentarConfirmarPorMensagemAsync(Conversa, "1234");

            Assert.False(tratada);
            Assert.Empty(f.Enviadas);
        }

        [Fact]
        public async Task A_wrong_code_written_as_code_gets_an_explanation()
        {
            var f = new Fixture();
            f.Repo.Setup(r => r.ConfirmarPorCodigoAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>()))
                .ReturnsAsync((CardapioPedidoPublico?)null);

            var tratada = await f.Build().TentarConfirmarPorMensagemAsync(Conversa, "Código: 1234");

            Assert.True(tratada);
            Assert.Contains("expirou", Assert.Single(f.Enviadas));
        }

        [Fact]
        public async Task After_five_wrong_codes_the_sender_is_blocked_and_the_message_follows_the_normal_flow()
        {
            var f = new Fixture();
            f.Repo.Setup(r => r.ConfirmarPorCodigoAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>()))
                .ReturnsAsync((CardapioPedidoPublico?)null);
            var service = f.Build();

            for (var i = 0; i < CardapioConfirmacaoRules.MaxTentativasErradas; i++)
            {
                await service.TentarConfirmarPorMensagemAsync(Conversa, "1234");
            }
            var sexta = await service.TentarConfirmarPorMensagemAsync(Conversa, "Código: 4821");

            Assert.False(sexta);
            f.Repo.Verify(
                r => r.ConfirmarPorCodigoAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>()),
                Times.Exactly(CardapioConfirmacaoRules.MaxTentativasErradas));
        }

        [Fact]
        public async Task A_conversation_without_a_known_phone_is_left_alone()
        {
            var f = new Fixture();
            f.Clientes.Setup(c => c.ObterTelefoneClienteAsync(Cliente, Estabelecimento)).ReturnsAsync((string?)null);

            Assert.False(await f.Build().TentarConfirmarPorMensagemAsync(Conversa, "4821"));
            f.Repo.Verify(r => r.ConfirmarPorCodigoAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>()), Times.Never);
        }

        // =====================================================================
        // Fila, aceite e recusa
        // =====================================================================

        [Fact]
        public async Task The_queue_shows_items_address_total_and_waiting_time_base()
        {
            var f = new Fixture();
            var pedido = Pedido(CardapioPedidoStatus.AguardandoAceite, conversa: Conversa);
            pedido.ConfirmadoEm = DateTimeOffset.UtcNow.AddMinutes(-3);
            pedido.TelefoneContato = "+5534991230001";
            f.Repo.Setup(r => r.ListarAguardandoAceiteAsync(Estabelecimento, It.IsAny<int>())).ReturnsAsync(new[] { pedido });

            var fila = await f.Build().ListarAguardandoAsync(Estabelecimento);

            var item = Assert.Single(fila);
            Assert.Equal("Maria Souza", item.NomeCliente);
            Assert.Equal("Rua das Flores, 120 (ap 4) – Centro", item.Endereco);
            Assert.Equal(64.9m, item.Total);
            Assert.Equal(pedido.ConfirmadoEm, item.ConfirmadoEm);
            var linha = Assert.Single(item.Itens);
            Assert.Equal(2, linha.Quantidade);
            Assert.Equal("X-Bacon", linha.Nome);
            Assert.Equal(new[] { "Bacon extra" }, linha.Adicionais);
            Assert.Equal("sem cebola", linha.Observacao);
        }

        [Fact]
        public async Task The_queue_hides_the_address_of_a_pickup_order()
        {
            var f = new Fixture();
            var pedido = Pedido(CardapioPedidoStatus.AguardandoAceite, tipoEntrega: "retirada");
            f.Repo.Setup(r => r.ListarAguardandoAceiteAsync(Estabelecimento, It.IsAny<int>())).ReturnsAsync(new[] { pedido });

            var item = Assert.Single(await f.Build().ListarAguardandoAsync(Estabelecimento));

            Assert.Equal("retirada", item.TipoEntrega);
            Assert.Null(item.Endereco);
        }

        [Fact]
        public async Task Accepting_a_delivery_creates_the_real_order_through_the_core_then_tells_the_client()
        {
            var f = new Fixture();
            var pedido = Pedido(CardapioPedidoStatus.AguardandoAceite, conversa: Conversa);
            f.Repo.Setup(r => r.ObterAsync(Estabelecimento, pedido.Id)).ReturnsAsync(pedido);
            CreatePedidoRequest? enviado = null;
            f.Core.Setup(c => c.CreateAsync(Estabelecimento, 9, It.IsAny<CreatePedidoRequest>(), null, true))
                .Callback<Guid, int, CreatePedidoRequest, string?, bool>((_, _, request, _, _) => enviado = request)
                .ReturnsAsync(new CreatedPedidoDto { Id = 77, Status = "rascunho", Total = 70m });
            f.Core.Setup(c => c.ConfirmAsync(Estabelecimento, 9, 77))
                .ReturnsAsync(new CreatedPedidoDto { Id = 77, Status = "pendente", Total = 70m });
            f.Repo.Setup(r => r.MarcarAceitoAsync(Estabelecimento, pedido.Id, 77)).ReturnsAsync(true);

            var resultado = await f.Build().AceitarAsync(Estabelecimento, 9, pedido.Id);

            Assert.Equal(77, resultado.PedidoId);
            Assert.Equal(64.9m, resultado.TotalCliente);
            Assert.Equal(70m, resultado.TotalPedido);
            Assert.True(resultado.ClienteAvisado);
            Assert.Contains("#77", Assert.Single(f.Enviadas));

            Assert.NotNull(enviado);
            Assert.Equal("cardapio_web", enviado!.Origem);
            Assert.Equal("CDP-ABC12345", enviado.OrigemRef);
            Assert.Equal(Conversa, enviado.ConversaId);
            Assert.Equal("Maria Souza", enviado.NomeCliente);
            Assert.Equal("Rua das Flores", enviado.Rua);
            Assert.Equal("120", enviado.Numero);
            Assert.Equal("MG", enviado.Estado);
            Assert.Equal(-18.9186, enviado.Latitude);
            Assert.Equal(-48.2772, enviado.Longitude);
            Assert.Equal("Pix", enviado.TipoPagamento);
            Assert.Contains("tocar a campainha", enviado.Observacoes);
            Assert.Contains("portao azul", enviado.Observacoes);
            var linha = Assert.Single(enviado.Itens!);
            Assert.Equal(ProdutoId, linha.ProdutoId);
            Assert.Equal(2, linha.Quantidade);
            Assert.Equal(new List<Guid> { AdicionalId }, linha.AdicionalItemIds);
        }

        [Fact]
        public async Task An_order_the_core_already_left_pending_is_not_confirmed_again()
        {
            var f = new Fixture();
            var pedido = Pedido(CardapioPedidoStatus.AguardandoAceite, conversa: Conversa);
            f.Repo.Setup(r => r.ObterAsync(Estabelecimento, pedido.Id)).ReturnsAsync(pedido);
            f.Core.Setup(c => c.CreateAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<CreatePedidoRequest>(), null, true))
                .ReturnsAsync(new CreatedPedidoDto { Id = 78, Status = "pendente", Total = 64.9m });
            f.Repo.Setup(r => r.MarcarAceitoAsync(Estabelecimento, pedido.Id, 78)).ReturnsAsync(true);

            await f.Build().AceitarAsync(Estabelecimento, 9, pedido.Id);

            f.Core.Verify(c => c.ConfirmAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never);
        }

        [Fact]
        public async Task Accepting_a_pickup_does_not_create_a_delivery_order()
        {
            var f = new Fixture();
            var pedido = Pedido(CardapioPedidoStatus.AguardandoAceite, tipoEntrega: "retirada", conversa: Conversa);
            f.Repo.Setup(r => r.ObterAsync(Estabelecimento, pedido.Id)).ReturnsAsync(pedido);
            f.Repo.Setup(r => r.MarcarAceitoAsync(Estabelecimento, pedido.Id, null)).ReturnsAsync(true);

            var resultado = await f.Build().AceitarAsync(Estabelecimento, 9, pedido.Id);

            Assert.Null(resultado.PedidoId);
            f.Core.Verify(c => c.CreateAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<CreatePedidoRequest>(), It.IsAny<string?>(), It.IsAny<bool>()), Times.Never);
            Assert.Contains("retirar", Assert.Single(f.Enviadas));
        }

        [Fact]
        public async Task Only_the_attendant_who_changed_the_status_messages_the_client()
        {
            var f = new Fixture();
            var pedido = Pedido(CardapioPedidoStatus.AguardandoAceite, conversa: Conversa);
            f.Repo.Setup(r => r.ObterAsync(Estabelecimento, pedido.Id)).ReturnsAsync(pedido);
            f.Core.Setup(c => c.CreateAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<CreatePedidoRequest>(), null, true))
                .ReturnsAsync(new CreatedPedidoDto { Id = 77, Status = "pendente", JaExistia = true, Total = 64.9m });
            // Outro atendente aceitou no mesmo instante: o UPDATE condicional nao altera nada.
            f.Repo.Setup(r => r.MarcarAceitoAsync(Estabelecimento, pedido.Id, 77)).ReturnsAsync(false);

            var resultado = await f.Build().AceitarAsync(Estabelecimento, 9, pedido.Id);

            Assert.Equal(77, resultado.PedidoId);
            Assert.False(resultado.ClienteAvisado);
            Assert.Empty(f.Enviadas);
        }

        [Fact]
        public async Task A_failed_message_is_reported_but_the_acceptance_stands()
        {
            var f = new Fixture();
            var pedido = Pedido(CardapioPedidoStatus.AguardandoAceite, conversa: Conversa);
            f.Repo.Setup(r => r.ObterAsync(Estabelecimento, pedido.Id)).ReturnsAsync(pedido);
            f.Core.Setup(c => c.CreateAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<CreatePedidoRequest>(), null, true))
                .ReturnsAsync(new CreatedPedidoDto { Id = 77, Status = "pendente", Total = 64.9m });
            f.Repo.Setup(r => r.MarcarAceitoAsync(Estabelecimento, pedido.Id, 77)).ReturnsAsync(true);
            f.Sender.Setup(s => s.SendAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>()))
                .ThrowsAsync(new InvalidOperationException("whatsapp fora"));

            var resultado = await f.Build().AceitarAsync(Estabelecimento, 9, pedido.Id);

            Assert.Equal(77, resultado.PedidoId);
            Assert.False(resultado.ClienteAvisado);
        }

        [Theory]
        [InlineData(CardapioPedidoStatus.AguardandoCodigo)]
        [InlineData(CardapioPedidoStatus.Aceito)]
        [InlineData(CardapioPedidoStatus.Recusado)]
        [InlineData(CardapioPedidoStatus.Expirado)]
        public async Task Only_a_confirmed_order_can_be_accepted_or_declined(string status)
        {
            var f = new Fixture();
            var pedido = Pedido(status, conversa: Conversa);
            f.Repo.Setup(r => r.ObterAsync(Estabelecimento, pedido.Id)).ReturnsAsync(pedido);
            var service = f.Build();

            var aceitar = await Assert.ThrowsAsync<DeliveryDomainException>(() => service.AceitarAsync(Estabelecimento, 9, pedido.Id));
            var recusar = await Assert.ThrowsAsync<DeliveryDomainException>(() => service.RecusarAsync(Estabelecimento, pedido.Id, null));

            Assert.Equal(409, aceitar.StatusCode);
            Assert.Equal(409, recusar.StatusCode);
            f.Core.Verify(c => c.CreateAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<CreatePedidoRequest>(), It.IsAny<string?>(), It.IsAny<bool>()), Times.Never);
            Assert.Empty(f.Enviadas);
        }

        [Fact]
        public async Task Accepting_an_unknown_order_is_not_found()
        {
            var f = new Fixture();
            f.Repo.Setup(r => r.ObterAsync(Estabelecimento, It.IsAny<Guid>())).ReturnsAsync((CardapioPedidoPublico?)null);

            var erro = await Assert.ThrowsAsync<DeliveryDomainException>(() => f.Build().AceitarAsync(Estabelecimento, 9, Guid.NewGuid()));

            Assert.Equal(404, erro.StatusCode);
        }

        [Fact]
        public async Task A_core_rejection_keeps_the_order_waiting_so_the_attendant_can_decline_it()
        {
            var f = new Fixture();
            var pedido = Pedido(CardapioPedidoStatus.AguardandoAceite, conversa: Conversa);
            f.Repo.Setup(r => r.ObterAsync(Estabelecimento, pedido.Id)).ReturnsAsync(pedido);
            f.Core.Setup(c => c.CreateAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<CreatePedidoRequest>(), null, true))
                .ThrowsAsync(new DeliveryDomainException(409, "STORE_CLOSED", "O estabelecimento esta fechado no momento."));

            var erro = await Assert.ThrowsAsync<DeliveryDomainException>(() => f.Build().AceitarAsync(Estabelecimento, 9, pedido.Id));

            Assert.Equal("STORE_CLOSED", erro.Code);
            f.Repo.Verify(r => r.MarcarAceitoAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<int?>()), Times.Never);
            Assert.Empty(f.Enviadas);
        }

        [Fact]
        public async Task Declining_tells_the_client_the_reason()
        {
            var f = new Fixture();
            var pedido = Pedido(CardapioPedidoStatus.AguardandoAceite, conversa: Conversa);
            f.Repo.Setup(r => r.ObterAsync(Estabelecimento, pedido.Id)).ReturnsAsync(pedido);
            f.Repo.Setup(r => r.MarcarRecusadoAsync(Estabelecimento, pedido.Id, "item em falta")).ReturnsAsync(true);

            await f.Build().RecusarAsync(Estabelecimento, pedido.Id, "  item em falta ");

            Assert.Contains("Motivo: item em falta.", Assert.Single(f.Enviadas));
            f.Core.Verify(c => c.CreateAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<CreatePedidoRequest>(), It.IsAny<string?>(), It.IsAny<bool>()), Times.Never);
        }

        [Fact]
        public async Task Declining_twice_messages_the_client_once()
        {
            var f = new Fixture();
            var pedido = Pedido(CardapioPedidoStatus.AguardandoAceite, conversa: Conversa);
            f.Repo.Setup(r => r.ObterAsync(Estabelecimento, pedido.Id)).ReturnsAsync(pedido);
            f.Repo.Setup(r => r.MarcarRecusadoAsync(Estabelecimento, pedido.Id, null)).ReturnsAsync(false);

            await f.Build().RecusarAsync(Estabelecimento, pedido.Id, null);

            Assert.Empty(f.Enviadas);
        }
    }
}
