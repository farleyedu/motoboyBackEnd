using APIBack.Acontecimentos;
using APIBack.Security;
using Xunit;

namespace APIBack.Tests.Unit;

/// <summary>Regra 11: gerente ve tudo; operador de caixa ve so o Caixa do dia e o Estoque e conta as cegas.</summary>
public class GestaoPapeisTests
{
    private static readonly DateOnly Hoje = new(2026, 10, 27);

    private static Func<string, string, bool> Com(params string[] permissoes) =>
        (modulo, acao) => permissoes.Contains($"{modulo}:{acao}");

    private static readonly GestaoPapeis.Acesso Gerente = GestaoPapeis.Resolver(
        Com("Financeiro:visualizar", "Financeiro:criar", "Financeiro:editar", "Financeiro:fechar_mes"), false);
    private static readonly GestaoPapeis.Acesso Operador = GestaoPapeis.Resolver(
        Com("Caixa:visualizar", "Caixa:lancar", "Caixa:fechar", "Estoque:visualizar", "Estoque:contar"), false);

    private static string Codigo(Action acao) => Assert.Throws<GestaoException>(acao).Codigo;

    [Fact]
    public void PapelSaiDasPermissoes()
    {
        Assert.Equal(GestaoPapeis.Gerente, Gerente.Papel);
        Assert.Equal(GestaoPapeis.OperadorCaixa, Operador.Papel);
        Assert.Null(GestaoPapeis.Resolver(Com("Delivery:visualizar"), false).Papel);
        Assert.Equal(GestaoPapeis.Gerente, GestaoPapeis.Resolver(Com(), true).Papel);
    }

    [Fact]
    public void GerenteVeTudoComHistoricoMenosACuradoria()
    {
        Assert.True(Gerente.Historico);
        Assert.True(Gerente.SaldoEsperadoNoFechamento);
        Assert.DoesNotContain("curadoria", Gerente.Areas);
        Assert.Contains("financeiro", Gerente.Areas);
        Assert.Contains("curadoria", GestaoPapeis.Resolver(Com(), true).Areas);
        Assert.True(Gerente.PodeFecharMes);
    }

    [Fact]
    public void OperadorVeSoCaixaEEstoqueEContaAsCegas()
    {
        Assert.Equal(new[] { "caixa", "estoque" }, Operador.Areas);
        Assert.False(Operador.Historico);
        Assert.False(Operador.SaldoEsperadoNoFechamento);
        Assert.False(Operador.PodeCorrigir);
        Assert.False(Operador.PodeFecharMes);
        var semEstoque = GestaoPapeis.Resolver(Com("Caixa:visualizar"), false);
        Assert.Equal(new[] { "caixa" }, semEstoque.Areas);
    }

    [Fact]
    public void OperadorLeSoOCaixaDeHoje()
    {
        var f = GestaoPapeis.RestringirLeitura(Operador, new PeriodoFiltro(), Hoje);
        Assert.Equal(Hoje, f.Dia);
        Assert.Equal(new DateOnly(2026, 10, 1), f.Mes);
        Assert.Equal(ModuloGestao.Caixa, f.Modulo);
        Assert.Equal("SEM_PERMISSAO", Codigo(() => GestaoPapeis.RestringirLeitura(Operador, new PeriodoFiltro { Dia = Hoje.AddDays(-1) }, Hoje)));
        Assert.Equal("SEM_PERMISSAO", Codigo(() => GestaoPapeis.RestringirLeitura(Operador, new PeriodoFiltro { Mes = new DateOnly(2026, 9, 1) }, Hoje)));
        Assert.Equal("SEM_PERMISSAO", Codigo(() => GestaoPapeis.RestringirLeitura(Operador, new PeriodoFiltro { Dia = Hoje, Ate = Hoje.AddDays(3) }, Hoje)));
        Assert.Equal("SEM_PERMISSAO", Codigo(() => GestaoPapeis.RestringirLeitura(Operador, new PeriodoFiltro { Modulo = "financeiro" }, Hoje)));
        Assert.Equal(ModuloGestao.Estoque, GestaoPapeis.RestringirLeitura(Operador, new PeriodoFiltro { Modulo = "estoque" }, Hoje).Modulo);
        Assert.Equal("SEM_PERMISSAO", Codigo(() => GestaoPapeis.RestringirLeitura(GestaoPapeis.Resolver(Com(), false), new PeriodoFiltro(), Hoje)));
    }

    [Fact]
    public void GerenteLeQualquerMes()
    {
        var f = new PeriodoFiltro { Mes = new DateOnly(2025, 3, 1), Modulo = "compras" };
        Assert.Same(f, GestaoPapeis.RestringirLeitura(Gerente, f, Hoje));
    }

    [Fact]
    public void OperadorRegistraSoNoCaixaENoEstoqueDeHoje()
    {
        var caixa = new PublicarAcontecimentoRequest { Modulo = ModuloGestao.Caixa };
        GestaoPapeis.ValidarPublicacao(Operador, caixa, Hoje, Hoje);
        GestaoPapeis.ValidarPublicacao(Operador, new PublicarAcontecimentoRequest { Modulo = ModuloGestao.Estoque, Movimentos = new() { new() } }, Hoje, Hoje);
        Assert.Equal("SEM_PERMISSAO", Codigo(() => GestaoPapeis.ValidarPublicacao(Operador, caixa, Hoje.AddDays(-1), Hoje)));
        Assert.Equal("SEM_PERMISSAO", Codigo(() => GestaoPapeis.ValidarPublicacao(Operador,
            new PublicarAcontecimentoRequest { Modulo = ModuloGestao.Despesas }, Hoje, Hoje)));
        Assert.Equal("SEM_PERMISSAO", Codigo(() => GestaoPapeis.ValidarPublicacao(Operador,
            new PublicarAcontecimentoRequest { Modulo = ModuloGestao.Caixa, Movimentos = new() { new() } }, Hoje, Hoje)));
        Assert.Equal("SEM_PERMISSAO", Codigo(() => GestaoPapeis.ExigirCorrecao(Operador)));
        Assert.Equal("SEM_PERMISSAO", Codigo(() => GestaoPapeis.ExigirFecharMes(Operador)));
        GestaoPapeis.ValidarPublicacao(Gerente, new PublicarAcontecimentoRequest { Modulo = ModuloGestao.Despesas }, Hoje.AddDays(-40), Hoje);
        GestaoPapeis.ExigirCorrecao(Gerente);
    }

    [Fact]
    public void GerenteSemCriarNaoLanca()
    {
        var soVe = GestaoPapeis.Resolver(Com("Financeiro:visualizar"), false);
        Assert.Equal("SEM_PERMISSAO", Codigo(() => GestaoPapeis.ValidarPublicacao(soVe, new PublicarAcontecimentoRequest(), Hoje, Hoje)));
        Assert.Equal("SEM_PERMISSAO", Codigo(() => GestaoPapeis.ExigirCorrecao(soVe)));
    }

    [Fact]
    public void CaixaEEstoqueEntramNoCatalogoComOFinanceiro()
    {
        var catalogo = EstablishmentPermissions.Catalog();
        Assert.Equal(GestaoPapeis.AcoesCaixa, catalogo["Caixa"]);
        Assert.Contains("fechar_mes", catalogo["Financeiro"]);

        var operador = "{\"Caixa\":[\"visualizar\",\"lancar\"],\"Estoque\":[\"contar\"]}";
        var comFinanceiro = EstablishmentPermissions.Resolve(operador, new[] { "PEDIDOS", "FINANCEIRO" });
        Assert.Contains("lancar", comFinanceiro["Caixa"]);
        Assert.Contains("contar", comFinanceiro["Estoque"]);
        var semFinanceiro = EstablishmentPermissions.Resolve(operador, new[] { "PEDIDOS" });
        Assert.False(semFinanceiro.ContainsKey("Caixa"));

        var gerente = EstablishmentPermissions.ResolveForAccess(null, new[] { "PEDIDOS", "FINANCEIRO" }, "colaborador", "gerente_estabelecimento");
        Assert.Equal(GestaoPapeis.AcoesEstoque.Order(), gerente["Estoque"].Order());
        Assert.Contains("fechar_mes", gerente["Financeiro"]);
    }

    [Fact]
    public void OperadorDeCaixaEUmPapelDeRestaurante()
    {
        Assert.Equal("operador_caixa", RoleCatalog.Normalize("Caixa"));
        Assert.True(RoleCatalog.IsAllowedForEstablishmentType("operador_caixa", "restaurante"));
        Assert.False(RoleCatalog.IsManagerRole("operador_caixa"));
        var padrao = GestaoPapeis.PermissoesPadrao(GestaoPapeis.OperadorCaixa);
        Assert.False(padrao.ContainsKey("Financeiro"));
        Assert.Equal(new[] { "visualizar", "contar" }, padrao["Estoque"]);
        Assert.Null(EstablishmentPermissions.Validate(padrao.ToDictionary(p => p.Key, p => p.Value.ToList()), new(), true));
    }
}
