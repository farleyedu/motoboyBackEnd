using APIBack.Security;
using Xunit;

namespace APIBack.Tests.Unit;

/// <summary>
/// Integração 6A: Caixa e Estoque existem só com o módulo Financeiro. Decisão do dono (27/10/2026): FINANCEIRO no Turbo e no Frota,
/// INTELIGENCIA só no Frota. As composições abaixo são as de 20261022_02_planos_v2 mais essa decisão (o banco muda pela tela de planos).
/// </summary>
public class PlanosGestaoTests
{
    private static readonly string[] Partida = { "GERAL", "CARDAPIO", "CARDAPIOWEB", "PEDIDOS", "CLIENTES", "IMPRESSAO", "IFOOD", "WHATSAPP" };
    private static readonly string[] Rota = Partida.Append("MAPA").ToArray();
    private static readonly string[] TurboHoje = Rota.Concat(new[] { "DELIVERY", "REMUNERACAO" }).ToArray();
    private static readonly string[] Turbo = TurboHoje.Append("FINANCEIRO").ToArray();
    private static readonly string[] Frota = TurboHoje.Concat(new[] { "INTELIGENCIA", "FINANCEIRO" }).ToArray();

    private static Dictionary<string, List<string>> Gerente(string[] modulos) =>
        EstablishmentPermissions.ResolveForAccess("{}", modulos, null, "gerente_estabelecimento");

    [Theory]
    [InlineData("turbo")]
    [InlineData("frota")]
    public void TurboEFrotaVeemCaixaEstoqueEFinanceiro(string plano)
    {
        var p = Gerente(plano == "turbo" ? Turbo : Frota);
        Assert.Contains("Caixa", p.Keys);
        Assert.Contains("Estoque", p.Keys);
        Assert.Contains("Financeiro", p.Keys);
        Assert.Equal(plano == "frota", p.ContainsKey("Inteligencia"));
    }

    [Theory]
    [InlineData("partida")]
    [InlineData("rota")]
    public void PartidaERotaNaoVeemCaixaNemEstoque(string plano)
    {
        var p = Gerente(plano == "partida" ? Partida : Rota);
        Assert.DoesNotContain("Caixa", p.Keys);
        Assert.DoesNotContain("Estoque", p.Keys);
        Assert.DoesNotContain("Financeiro", p.Keys);
    }

    [Fact]
    public void TurboComoEstaNaMigracaoAindaNaoTemCaixa()
    {
        // Enquanto o Financeiro não for ligado no Turbo pela tela de planos, a loja nova do Turbo não vê a Gestão.
        Assert.DoesNotContain("Caixa", Gerente(TurboHoje).Keys);
    }

    [Fact]
    public void LojaAntigaComBackfillDoFinanceiroVeCaixaMesmoNaPartida()
    {
        // 20261020_04_modulos_backfill deu FINANCEIRO às lojas antigas: elas continuam vendo, qualquer que seja o plano.
        Assert.Contains("Caixa", Gerente(Partida.Append("FINANCEIRO").ToArray()).Keys);
    }
}
