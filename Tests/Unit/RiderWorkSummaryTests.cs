using APIBack.Service;
using Xunit;

namespace APIBack.Tests.Unit;

public sealed class RiderWorkSummaryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Sem_datas_usa_os_ultimos_30_dias_ate_agora()
    {
        var (from, to) = MotoboyWorkService.SummaryWindow(null, null, Now);
        Assert.Equal(Now, to);
        Assert.Equal(Now.AddDays(-30), from);
    }

    [Fact]
    public void So_com_from_vai_ate_agora_e_so_com_to_volta_30_dias()
    {
        var onlyFrom = MotoboyWorkService.SummaryWindow(Now.AddDays(-7), null, Now);
        Assert.Equal((Now.AddDays(-7), Now), onlyFrom);
        var onlyTo = MotoboyWorkService.SummaryWindow(null, Now.AddDays(-10), Now);
        Assert.Equal((Now.AddDays(-40), Now.AddDays(-10)), onlyTo);
    }

    [Theory]
    [InlineData(-94, 0)]
    [InlineData(0, -1)]
    public void Periodo_invertido_ou_maior_que_93_dias_e_recusado(int fromDays, int toDays)
    {
        Assert.Throws<ArgumentException>(() => MotoboyWorkService.SummaryWindow(Now.AddDays(fromDays), Now.AddDays(toDays), Now));
    }

    [Fact]
    public void Id_do_plano_no_lote_e_estavel_por_operacao_e_motoboy()
    {
        var operation = Guid.NewGuid();
        Assert.Equal(MotoboyWorkService.BatchPlanId(operation, 7), MotoboyWorkService.BatchPlanId(operation, 7));
        Assert.NotEqual(MotoboyWorkService.BatchPlanId(operation, 7), MotoboyWorkService.BatchPlanId(operation, 8));
        Assert.NotEqual(MotoboyWorkService.BatchPlanId(operation, 7), MotoboyWorkService.BatchPlanId(Guid.NewGuid(), 7));
    }
}
