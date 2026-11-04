using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using APIBack.Acontecimentos;
using APIBack.DTOs.Clientes;
using APIBack.DTOs.Delivery;
using APIBack.GestaoLogica.Caixa;
using APIBack.Model.Delivery;
using APIBack.Repository.Interface;
using APIBack.Service;
using APIBack.Service.Interface;
using Moq;
using Xunit;

namespace APIBack.Tests.Unit;

/// <summary>
/// 6C5, venda de balcão pelo Caixa (decisão do dono: pedido sem endereço): itens só do cardápio com preço do servidor, posição da loja,
/// conclusão do balcão na hora (que publica a venda), sem motoboy e idempotente pela chave.
/// </summary>
public class GestaoCaixaBalcaoTests
{
    private static readonly Guid Loja = Guid.NewGuid();
    private static readonly Guid P1 = Guid.NewGuid();

    private static (PedidoCoreService Service, Mock<IPedidoQueueRepository> Queue) Criar(double? latitude = -19.9, double? longitude = -43.9)
    {
        var queue = new Mock<IPedidoQueueRepository>();
        queue.Setup(q => q.CreatePedidoAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<ManualOrder>()))
            .ReturnsAsync((Guid _, int _, ManualOrder o) => new CreatedPedidoDto { Id = 77, Status = "pendente", Total = o.Value });
        queue.Setup(q => q.MarcarEntregueManualAsync(It.IsAny<Guid>(), It.IsAny<int>(), 77, It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync("concluido");
        var restaurante = new Mock<IRestaurantSettingsRepository>();
        restaurante.Setup(r => r.GetAsync(It.IsAny<Guid>())).ReturnsAsync(new RestaurantSettingsDto { Latitude = latitude, Longitude = longitude, Bairro = "Centro", Cidade = "BH" });
        var cardapio = new Mock<ICardapioRepository>();
        cardapio.Setup(m => m.ListarProdutosPublicosPorIdsAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<bool>()))
            .ReturnsAsync(new[] { CoreFixtures.Product(P1, 28.90m) });
        var clientes = new Mock<IClienteCadastroRepository>();
        clientes.Setup(c => c.ResolverOuCriarAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string?>())).ReturnsAsync((Guid?)null);
        return (new PedidoCoreService(queue.Object, restaurante.Object, cardapio.Object, Mock.Of<IDeliveryZonaRepository>(),
            Mock.Of<IHorarioOperacaoRepository>(), Mock.Of<IAtendenteConfirmacaoSender>(), clientes.Object, Mock.Of<IClienteEnderecoRepository>()), queue);
    }

    private static PedidoBalcao Venda(decimal? recebido = 100m, params PedidoItemRequest[] itens) =>
        new("v1", itens.Length > 0 ? itens : new[] { new PedidoItemRequest { ProdutoId = P1, Quantidade = 2 } }, "Dinheiro", recebido);

    [Fact]
    public async Task Gestao6C5_balcao_vira_pedido_sem_endereco_concluido_no_balcao_sem_motoboy()
    {
        var (service, queue) = Criar();
        var criado = await service.CreateBalcaoAsync(Loja, 7, Venda());

        Assert.Equal(("concluido", 57.80m), (criado.Status, criado.Total));
        var pedido = (ManualOrder)queue.Invocations.Single(i => i.Method.Name == nameof(IPedidoQueueRepository.CreatePedidoAsync)).Arguments[2];
        Assert.Equal(("Balcão", "Balcão", -19.9, -43.9), (pedido.EnderecoEntrega, pedido.NomeCliente, pedido.Latitude, pedido.Longitude));
        Assert.Equal((57.80m, 0m, "Dinheiro", 100m), (pedido.Value, pedido.TaxaEntrega!.Value, pedido.TipoPagamento, pedido.Troco!.Value));
        Assert.Equal((PedidoOrigem.Atendente, "caixa:v1"), (pedido.Origem, pedido.OrigemRef));
        Assert.Single(pedido.Lines);
        queue.Verify(q => q.MarcarEntregueManualAsync(Loja, 7, 77, EncerramentoRules.OrigemBalcao, null), Times.Once);
        // Nada de autoatribuição de motoboy no balcão.
        queue.Verify(q => q.AssignAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never);
        queue.Verify(q => q.GetSettingsAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task Gestao6C5_balcao_que_segue_o_fluxo_fica_pendente_e_pode_pagar_na_retirada()
    {
        var (service, queue) = Criar();
        var pago = await service.CreateBalcaoAsync(Loja, 7, Venda() with { Concluir = false });
        Assert.Equal("pendente", pago.Status);
        queue.Verify(q => q.MarcarEntregueManualAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);

        // Paga na retirada: sem forma nem troco no pedido; a venda fica a receber quando o fluxo concluir.
        await service.CreateBalcaoAsync(Loja, 7, Venda(null) with { TipoPagamento = null, Concluir = false });
        var pedido = (ManualOrder)queue.Invocations.Last(i => i.Method.Name == nameof(IPedidoQueueRepository.CreatePedidoAsync)).Arguments[2];
        Assert.Equal(((string?)null, (decimal?)null), (pedido.TipoPagamento, pedido.Troco));
        Assert.Null(pedido.StatusPagamento); // nasce pendente: nada foi recebido
    }

    [Fact]
    public async Task Gestao6C5_balcao_com_forma_nasce_pago_e_sem_forma_nasce_pendente()
    {
        var (service, queue) = Criar();
        ManualOrder Ultimo() => (ManualOrder)queue.Invocations.Last(i => i.Method.Name == nameof(IPedidoQueueRepository.CreatePedidoAsync)).Arguments[2];

        // Pago agora (e segue o fluxo da loja): o pedido já diz que foi pago, para ninguém cobrar de novo na entrega.
        await service.CreateBalcaoAsync(Loja, 7, Venda() with { Concluir = false });
        Assert.Equal("Pago", Ultimo().StatusPagamento);
        // Pagamento na retirada: a forma não vira recebimento.
        await service.CreateBalcaoAsync(Loja, 7, Venda(null) with { TipoPagamento = null, Concluir = false });
        Assert.Null(Ultimo().StatusPagamento);
    }

    [Fact]
    public async Task Gestao6C5_balcao_recusa_linha_avulsa_loja_sem_local_e_dinheiro_menor_que_o_total()
    {
        var (service, queue) = Criar();
        Assert.Equal("INVALID_ORDER", (await Assert.ThrowsAsync<DeliveryDomainException>(() =>
            service.CreateBalcaoAsync(Loja, 7, Venda(100m, new PedidoItemRequest { Nome = "Avulso", PrecoUnitario = 5 })))).Code);
        Assert.Equal("INVALID_ORDER", (await Assert.ThrowsAsync<DeliveryDomainException>(() => service.CreateBalcaoAsync(Loja, 7, Venda(50m)))).Code);
        var (semLocal, _) = Criar(latitude: null, longitude: null);
        Assert.Equal("LOJA_SEM_LOCAL", (await Assert.ThrowsAsync<DeliveryDomainException>(() => semLocal.CreateBalcaoAsync(Loja, 7, Venda()))).Code);
        queue.Verify(q => q.CreatePedidoAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<ManualOrder>()), Times.Never);
    }

    [Theory]
    [InlineData("din", "Dinheiro")]
    [InlineData("pix", "Pix")]
    [InlineData("deb", "Débito")]
    [InlineData("cre", "Crédito")]
    public void Gestao6C5_forma_do_caixa_vira_o_texto_que_a_venda_do_livro_le(string forma, string texto)
    {
        Assert.Equal(texto, BalcaoRegras.TipoPagamento(forma));
        // O texto gravado no pedido é reconhecido pela regra da venda (6C1) como a mesma forma.
        Assert.Equal(forma, APIBack.GestaoLogica.Vendas.VendaRegras.Forma(texto).Forma);
    }

    [Fact]
    public void Gestao6C5_balcao_valida_forma_chave_e_valor_recebido()
    {
        Assert.Equal("CAMPO_INVALIDO", Assert.Throws<GestaoException>(() => BalcaoRegras.TipoPagamento("ifood")).Codigo);
        Assert.Equal("CAMPO_INVALIDO", Assert.Throws<GestaoException>(() => BalcaoRegras.Chave(" ")).Codigo);
        Assert.Equal(100m, BalcaoRegras.ValorRecebido("din", 100m));
        Assert.Null(BalcaoRegras.ValorRecebido("pix", 100m));
        Assert.Equal("CAMPO_INVALIDO", Assert.Throws<GestaoException>(() => BalcaoRegras.ValorRecebido("din", null)).Codigo);
    }
}
