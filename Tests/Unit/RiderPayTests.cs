using APIBack.DTOs.Delivery;
using APIBack.Service;
using Xunit;

namespace APIBack.Tests.Unit;
public sealed class RiderPayTests
{
    [Theory][InlineData("route")][InlineData("percent")][InlineData("hybrid")][InlineData("negotiated")][InlineData("production")]
    public void RejectedModesCannotBecomePaymentPlans(string mode) => Assert.Throws<ArgumentException>(()=>RiderPayRules.ValidatePlan(mode,10));
    [Theory][InlineData(0)][InlineData(-1)][InlineData(1.001)][InlineData(1000001)]
    public void InvalidRateCannotBeRoundedSilently(decimal value) => Assert.Throws<ArgumentException>(()=>RiderPayRules.ValidatePlan("delivery",value));
    [Fact] public void DistanceIsOnlyRateTimesKnownDistanceAndRoundsCents()
    {
        var plan=new RiderPayPlan(Guid.NewGuid(),"distance",2.50m,DateTimeOffset.UtcNow);
        Assert.Equal(8.34m,RiderPayRules.Quote(plan,3.334m).Amount);
        Assert.Null(RiderPayRules.Quote(plan,null).Amount);Assert.Null(RiderPayRules.Quote(plan,-1).Amount);
        Assert.Equal(2.50m,RiderPayRules.Quote(plan with {Mode="delivery"},800m).Amount);
        Assert.Null(RiderPayRules.Quote(plan with {Mode="hour"},3m).Amount);
    }
    [Fact] public void HourUsesConfirmedMinutesNotCalendarDuration()
    {
        var plan=new RiderPayPlan(Guid.NewGuid(),"hour",15m,DateTimeOffset.UtcNow.AddDays(-10));
        var r=new RiderPeriodRequest{From=DateTimeOffset.UtcNow.AddHours(-3),To=DateTimeOffset.UtcNow.AddHours(-1),WorkedMinutes=90};
        var q=RiderPayRules.PeriodAmount(plan,r);
        Assert.Equal(22.50m,q.Amount);Assert.Null(q.TotalSeconds);Assert.Null(q.WorkedSeconds);
        r.WorkedMinutes=121;Assert.Throws<ArgumentException>(()=>RiderPayRules.PeriodAmount(plan,r));
    }
    [Theory]
    [InlineData("day","2026-01-01T00:00:00-03:00","2026-01-02T00:00:00-03:00",86400L)]
    [InlineData("week","2026-01-05T00:00:00-03:00","2026-01-12T00:00:00-03:00",7*86400L)]
    [InlineData("fortnight","2026-01-01T00:00:00-03:00","2026-01-16T00:00:00-03:00",15*86400L)]
    [InlineData("fortnight","2026-01-16T00:00:00-03:00","2026-02-01T00:00:00-03:00",16*86400L)]
    [InlineData("month","2026-02-01T00:00:00-03:00","2026-03-01T00:00:00-03:00",28*86400L)]
    public void CalendarPeriodsUseFullAmountAndRealDayCountNotAHardcodedFifteenDays(string mode,string start,string end,long expectedTotalSeconds)
    {
        var plan=new RiderPayPlan(Guid.NewGuid(),mode,1200m,DateTimeOffset.Parse("2025-01-01Z"));
        var r=new RiderPeriodRequest{From=DateTimeOffset.Parse(start),To=DateTimeOffset.Parse(end)};
        var full=RiderPayRules.PeriodAmount(plan,r);
        Assert.Equal(1200m,full.Amount);Assert.Equal(expectedTotalSeconds,full.TotalSeconds);Assert.Equal(full.TotalSeconds,full.WorkedSeconds);
        // Um pedaço parcial do mesmo intervalo agora é proporcional (não rejeitado) - o motoboy que
        // trabalha só parte do período recebe a fração correspondente, nunca zero.
        r.To=r.To.AddMinutes(-1);
        var partial=RiderPayRules.PeriodAmount(plan,r);
        Assert.True(partial.Amount<1200m);Assert.Equal(expectedTotalSeconds,partial.TotalSeconds);Assert.Equal(expectedTotalSeconds-60,partial.WorkedSeconds);
        Assert.Equal(RiderPayRules.Money(1200m*(expectedTotalSeconds-60)/expectedTotalSeconds),partial.Amount);
    }
    [Fact] public void PartialWeekIsProratedByActualSecondsWorkedAndRoundsToCents()
    {
        var plan=new RiderPayPlan(Guid.NewGuid(),"week",350m,DateTimeOffset.Parse("2025-01-01Z"));
        // Segunda a quinta (3 de 7 dias): fração exata.
        var threeDays=new RiderPeriodRequest{From=DateTimeOffset.Parse("2026-01-05T00:00:00-03:00"),To=DateTimeOffset.Parse("2026-01-08T00:00:00-03:00")};
        var q=RiderPayRules.PeriodAmount(plan,threeDays);
        Assert.Equal(150m,q.Amount);Assert.Equal(7*86400L,q.TotalSeconds);Assert.Equal(3*86400L,q.WorkedSeconds);
        // Um único dia (1 de 7) com taxa que não divide exato por 7: arredonda pra centavo mais próximo.
        var oneDay=new RiderPeriodRequest{From=DateTimeOffset.Parse("2026-01-05T00:00:00-03:00"),To=DateTimeOffset.Parse("2026-01-06T00:00:00-03:00")};
        var rounded=new RiderPayPlan(plan.Id,"week",100m,plan.CreatedAtUtc);
        Assert.Equal(14.29m,RiderPayRules.PeriodAmount(rounded,oneDay).Amount);
    }
    [Fact] public void PeriodCannotCrossIntoTheNextCalendarOccurrence()
    {
        var plan=new RiderPayPlan(Guid.NewGuid(),"week",350m,DateTimeOffset.Parse("2025-01-01Z"));
        // Começa na semana de 05/01 (segunda) mas termina um dia dentro da semana seguinte.
        var r=new RiderPeriodRequest{From=DateTimeOffset.Parse("2026-01-05T00:00:00-03:00"),To=DateTimeOffset.Parse("2026-01-13T00:00:00-03:00")};
        Assert.Throws<ArgumentException>(()=>RiderPayRules.PeriodAmount(plan,r));
    }
    [Fact] public void PeriodCannotBeFutureRetroactiveOrDeliveryMode()
    {
        var now=DateTimeOffset.UtcNow;
        var p=new RiderPayPlan(Guid.NewGuid(),"shift",80,now.AddHours(-4));
        var r=new RiderPeriodRequest{From=now.AddHours(-3),To=now.AddHours(-1)};
        Assert.Equal(80,RiderPayRules.PeriodAmount(p,r).Amount);
        Assert.Throws<ArgumentException>(()=>RiderPayRules.PeriodAmount(p with{Mode="delivery"},r));
        r.From=now.AddDays(-2);Assert.Throws<ArgumentException>(()=>RiderPayRules.PeriodAmount(p,r));
        r.From=now.AddHours(-3);r.To=now.AddHours(1);Assert.Throws<ArgumentException>(()=>RiderPayRules.PeriodAmount(p,r));
    }
    [Theory][InlineData("draft","pay",false)][InlineData("draft","pay",true)][InlineData("reviewed","receive",true)][InlineData("paid","pay",false)][InlineData("received","cancel",false)]
    public void SettlementCannotSkipReviewOrBePaidUnilaterally(string status,string action,bool owner) => Assert.Throws<DeliveryDomainException>(()=>RiderPayRules.NextStatus(status,action,owner));
    [Fact] public void SettlementHasDistinctReviewPaymentAndAcknowledgment()
    {
        Assert.Equal("reviewed",RiderPayRules.NextStatus("draft","review",true));
        Assert.Equal("paid",RiderPayRules.NextStatus("reviewed","pay",false));
        Assert.Equal("received",RiderPayRules.NextStatus("paid","receive",true));
        Assert.Equal("disputed",RiderPayRules.NextStatus("reviewed","dispute",true));
    }
}
