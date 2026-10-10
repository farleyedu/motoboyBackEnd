using APIBack.Automation.Interfaces;
using APIBack.DTOs.Clientes;
using APIBack.Model.Cardapio;
using APIBack.Repository.Interface;
using APIBack.Service;
using Moq;
using Xunit;

namespace APIBack.Tests.Unit;

public sealed class ClienteAcessoTests
{
    private readonly Guid _est = Guid.NewGuid();
    private readonly Guid _client = Guid.NewGuid();
    private readonly Mock<IClienteEnderecoRepository> _addresses = new();
    private readonly Mock<IClienteCadastroRepository> _clients = new();
    private readonly Mock<ICardapioPedidoWebRepository> _orders = new();
    private readonly Mock<ICardapioRepository> _menu = new();
    private readonly Mock<IWabaPhoneRepository> _waba = new();
    private ClienteAcessoService Service => new(_addresses.Object, _clients.Object, _orders.Object, _menu.Object, _waba.Object);
    public ClienteAcessoTests()
    {
        _menu.Setup(m => m.ObterEstabelecimentoPublicoAsync(_est, null)).ReturnsAsync(new CardapioEstabelecimentoPublico { Id = _est, Publicado = true });
        _waba.Setup(w => w.ObterDisplayPhoneParaServicoAsync(_est, "cardapio_web")).ReturnsAsync("+553433330000");
        _clients.Setup(c => c.GetByTelefoneAsync(_est, It.IsAny<string?>())).ReturnsAsync(new ClienteDto { Id = _client, Nome = "Maria", Telefone = "+5534991230001" });
        _addresses.Setup(a => a.ListAsync(_est, _client)).ReturnsAsync(new[] { new ClienteEnderecoDto { Id = Guid.NewGuid(), Principal = true, Logradouro = "Rua A" } });
        _addresses.Setup(a => a.GetSessionAsync(_est, It.IsAny<string>())).ReturnsAsync(new ClienteSessao(_client, _est, "+5534991230001", "Maria", DateTimeOffset.UtcNow.AddDays(30)));
    }

    [Theory]
    [InlineData(-59, true)]
    [InlineData(-61, false)]
    [InlineData(-90, false)]
    [InlineData(5, false)]
    public async Task Apenas_entrada_na_ultima_hora_autentica(int minutes, bool authenticated)
    {
        _orders.Setup(o => o.ObterConversaPorTelefoneAsync(_est, It.IsAny<IReadOnlyList<string>>()))
            .ReturnsAsync(new ConversaPorTelefone(Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours(24), DateTimeOffset.UtcNow.AddMinutes(minutes)));
        var result = await Service.AuthenticateAsync(_est, "34991230001");
        Assert.Equal(authenticated, result.Autenticado);
        if (authenticated) { Assert.Equal(64, result.Token!.Length); Assert.Single(result.Enderecos); }
        else { Assert.Null(result.Token); Assert.Null(result.Nome); Assert.Empty(result.Enderecos); Assert.Contains("wa.me/553433330000", result.WhatsappUrl); }
    }

    [Fact]
    public async Task Janela_de_24h_sem_entrada_recente_nao_libera_dados()
    {
        _orders.Setup(o => o.ObterConversaPorTelefoneAsync(_est, It.IsAny<IReadOnlyList<string>>()))
            .ReturnsAsync(new ConversaPorTelefone(Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours(24), null));
        Assert.False((await Service.AuthenticateAsync(_est, "34991230001")).Autenticado);
        _clients.Verify(c => c.GetByTelefoneAsync(It.IsAny<Guid>(), It.IsAny<string?>()), Times.Never);
        _addresses.Verify(a => a.ListAsync(It.IsAny<Guid>(), It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task Cliente_logado_carrega_enderecos_sem_consultar_whatsapp()
    {
        var result = await Service.ProfileAsync(_est, new string('A', 64));
        Assert.True(result.Autenticado); Assert.Single(result.Enderecos);
        _orders.Verify(o => o.ObterConversaPorTelefoneAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<string>>()), Times.Never);
    }

    [Fact]
    public async Task Sessao_de_outra_loja_numero_diferente_e_credencial_ausente_sao_recusados()
    {
        Assert.Equal("CLIENT_UNAUTHENTICATED", (await Assert.ThrowsAsync<DeliveryDomainException>(() => Service.RequireAsync(Guid.NewGuid(), new string('A', 64)))).Code);
        Assert.Equal("CLIENT_UNAUTHENTICATED", (await Assert.ThrowsAsync<DeliveryDomainException>(() => Service.RequireAsync(_est, null))).Code);
        Assert.Equal("CLIENT_PHONE_MISMATCH", (await Assert.ThrowsAsync<DeliveryDomainException>(() => Service.RequireAsync(_est, new string('A', 64), "34998887777"))).Code);
    }

    [Theory]
    [InlineData("", "10", "MG")]
    [InlineData("Rua A", "", "MG")]
    [InlineData("Rua A", "10", "M")]
    public void Endereco_incompleto_nao_e_salvo(string rua, string numero, string uf) => Assert.Throws<DeliveryDomainException>(() =>
        ClienteEnderecoRules.Validate(new ClienteEnderecoRequest { Logradouro = rua, Numero = numero, Uf = uf, Bairro = "Centro", Cidade = "Uberlândia" }));
}
