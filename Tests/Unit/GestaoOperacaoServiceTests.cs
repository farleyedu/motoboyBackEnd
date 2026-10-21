using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Repository.Interface;
using APIBack.Service;
using Moq;
using Xunit;

namespace APIBack.Tests.Unit;

public class GestaoOperacaoServiceTests
{
    private static readonly Guid Empresa = Guid.NewGuid();
    private static readonly OperacaoLojaRow Turbo = new() { Id = Guid.NewGuid(), Nome = "Centro", Situacao = "ativo", Modulos = new[] { "PEDIDOS", "MAPA", "DELIVERY", "IFOOD" } };
    private static readonly OperacaoLojaRow Partida = new() { Id = Guid.NewGuid(), Nome = "Bairro", Situacao = "ativo", Modulos = new[] { "PEDIDOS" } };
    private static readonly OperacaoLojaRow Bloqueada = new() { Id = Guid.NewGuid(), Nome = "Parada", Situacao = "bloqueado", Modulos = new[] { "PEDIDOS", "DELIVERY" } };
    private static readonly OperacaoLojaRow Teste = new() { Id = Guid.NewGuid(), Nome = "Nova", Situacao = "teste", Modulos = new[] { "GERAL" } };

    private readonly Mock<IGestaoOperacaoRepository> _repository = new();

    private GestaoOperacaoService Service(params OperacaoLojaRow[] lojas)
    {
        _repository.Setup(r => r.ListarLojasAsync(Empresa, 7, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(lojas);
        _repository.Setup(r => r.ContadoresAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<Guid> ids, CancellationToken _) => ids.Select(id => new OperacaoContadoresRow
            { EstabelecimentoId = id, Pendentes = 2, EmRota = 3, MotoboysOnline = 4, TransferenciasPendentes = 1 }).ToList());
        _repository.Setup(r => r.AlertasIfoodAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<Guid> ids, CancellationToken _) => ids.Select(id => new OperacaoIfoodRow { EstabelecimentoId = id, Criticos = 1, Avisos = 5 }).ToList());
        return new GestaoOperacaoService(_repository.Object);
    }

    [Theory]
    [InlineData("dono", false, true)]
    [InlineData("gerente_empresa", false, true)]
    [InlineData("GERENTE_EMPRESA", false, true)]
    [InlineData("gerente_estabelecimento", false, false)]
    [InlineData(null, false, false)]
    [InlineData(null, true, true)]
    public void Gestor_da_empresa_e_super_admin_veem_todas_as_lojas(string? papel, bool superAdmin, bool todas) =>
        Assert.Equal(todas, GestaoOperacaoService.VeTodasAsLojas(papel, superAdmin));

    [Fact]
    public async Task Contador_so_aparece_quando_a_loja_tem_o_modulo()
    {
        var result = await Service(Turbo, Partida).VisaoGeralAsync(7, Empresa, "dono", false, podeVerIfood: true);
        var turbo = result.Lojas.Single(l => l.EstabelecimentoId == Turbo.Id);
        Assert.Equal((2, 3, 4, 1), (turbo.PedidosPendentes, turbo.PedidosEmRota, turbo.MotoboysOnline, turbo.TransferenciasPendentes));
        Assert.Equal((1, 5), (turbo.IfoodAlertasCriticos, turbo.IfoodAlertasAviso));
        var partida = result.Lojas.Single(l => l.EstabelecimentoId == Partida.Id);
        Assert.Equal((2, 3), (partida.PedidosPendentes, partida.PedidosEmRota));
        Assert.Null(partida.MotoboysOnline); Assert.Null(partida.TransferenciasPendentes); Assert.Null(partida.IfoodAlertasCriticos);
    }

    [Fact]
    public async Task Loja_bloqueada_aparece_sem_contadores_e_nao_entra_nas_consultas()
    {
        var result = await Service(Turbo, Bloqueada).VisaoGeralAsync(7, Empresa, "dono", false, podeVerIfood: true);
        var bloqueada = result.Lojas.Single(l => l.EstabelecimentoId == Bloqueada.Id);
        Assert.Equal("bloqueado", bloqueada.SituacaoComercial);
        Assert.Null(bloqueada.PedidosPendentes); Assert.Null(bloqueada.MotoboysOnline);
        _repository.Verify(r => r.ContadoresAsync(It.Is<IReadOnlyCollection<Guid>>(ids => !ids.Contains(Bloqueada.Id)), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Loja_em_teste_tem_tudo_liberado()
    {
        var teste = (await Service(Teste).VisaoGeralAsync(7, Empresa, "dono", false, podeVerIfood: true)).Lojas.Single();
        Assert.True(teste.TemDelivery); Assert.True(teste.TemIfood);
        Assert.Equal(4, teste.MotoboysOnline);
    }

    [Fact]
    public async Task Sem_permissao_de_ifood_nao_consulta_alertas()
    {
        var result = await Service(Turbo).VisaoGeralAsync(7, Empresa, "dono", false, podeVerIfood: false);
        Assert.Null(result.Lojas.Single().IfoodAlertasCriticos);
        _repository.Verify(r => r.AlertasIfoodAsync(It.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 0), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Escopo_do_usuario_comum_vai_para_a_consulta_de_lojas()
    {
        await Service(Turbo).VisaoGeralAsync(7, Empresa, "gerente_estabelecimento", false, podeVerIfood: false);
        _repository.Verify(r => r.ListarLojasAsync(Empresa, 7, false, It.IsAny<CancellationToken>()), Times.Once);
    }
}
