using APIBack.Repository;
using APIBack.Service;
using Xunit;

namespace APIBack.Tests.Unit;

public sealed class PedidoConsultaFiltroPeriodoTests
{
    private static string Where(bool nativeDate) =>
        PedidoConsultaRepository.BuildWhere(Guid.NewGuid(), new PedidoFiltro { De = "2026-10-01", Ate = "2026-10-31" }, "p.origem", false, nativeDate).Where;

    [Fact]
    public void Coluna_date_ou_timestamp_compara_em_faixa_sem_converter_a_coluna_em_texto()
    {
        var where = Where(nativeDate: true);
        Assert.Contains("p.data_pedido >= CAST(@De AS date)", where);
        Assert.Contains("p.data_pedido < CAST(@Ate AS date) + 1", where);
        Assert.DoesNotContain("LEFT(p.data_pedido", where);
    }

    [Fact]
    public void Coluna_de_texto_legada_continua_comparando_o_texto_AAAA_MM_DD()
    {
        var where = Where(nativeDate: false);
        Assert.Contains("LEFT(p.data_pedido::text, 10) >= @De", where);
        Assert.Contains("LEFT(p.data_pedido::text, 10) <= @Ate", where);
    }

    [Fact]
    public void Versao_da_lista_sobe_no_commit_e_cobre_pedido_e_snapshot_ifood()
    {
        var sql = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", "20261023_03_pedido_lista_versao.sql"));
        Assert.Contains("CREATE CONSTRAINT TRIGGER trg_pedido_lista_versao", sql);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(sql, "DEFERRABLE INITIALLY DEFERRED").Count);
        Assert.Contains("ON ifood_order_snapshots", sql);
        Assert.DoesNotContain("DROP TABLE", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sem_periodo_nao_filtra_por_data()
    {
        var where = PedidoConsultaRepository.BuildWhere(Guid.NewGuid(), new PedidoFiltro(), "p.origem", false, true).Where;
        Assert.DoesNotContain("data_pedido", where);
    }
}
