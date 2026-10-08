using APIBack.DTOs.Delivery;

namespace APIBack.Service;

public static class RiderPayRules
{
    public static readonly string[] Modes = ["delivery", "distance", "hour", "shift", "day", "week", "fortnight", "month"];
    public static decimal Money(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);
    public static void ValidatePlan(string mode, decimal rate)
    {
        if (!Modes.Contains(mode) || rate <= 0 || rate > 1_000_000 || Money(rate) != rate)
            throw new ArgumentException("Informe uma modalidade permitida e um valor positivo com até duas casas decimais.");
    }
    public static RiderPayQuote Quote(RiderPayPlan plan, decimal? distance)
    {
        ValidatePlan(plan.Mode, plan.Rate);
        var km = distance is >= 0 and <= 10000 ? distance : null;
        return new(plan.Id, plan.Mode, plan.Rate, plan.Mode == "distance" ? km : null,
            plan.Mode == "delivery" ? plan.Rate : plan.Mode == "distance" && km.HasValue ? Money(plan.Rate * km.Value) : null);
    }
    public static RiderPeriodQuote PeriodAmount(RiderPayPlan plan, RiderPeriodRequest request)
    {
        ValidatePlan(plan.Mode, plan.Rate);
        if (plan.Mode is "delivery" or "distance" || request.From >= request.To || request.To > DateTimeOffset.UtcNow || request.From < plan.CreatedAtUtc)
            throw new ArgumentException("O período deve estar encerrado, dentro da vigência da regra e sem remuneração por entrega.");
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");
        var from = TimeZoneInfo.ConvertTime(request.From, zone);
        var to = TimeZoneInfo.ConvertTime(request.To, zone);
        if (plan.Mode == "hour")
        {
            if (request.WorkedMinutes is not > 0 || request.WorkedMinutes > (request.To - request.From).TotalMinutes || (request.To - request.From).TotalDays > 31)
                throw new ArgumentException("Informe minutos trabalhados conferidos, sem ultrapassar a duração do período.");
            return new(Money(plan.Rate * request.WorkedMinutes.Value / 60m), null, null);
        }
        if (request.WorkedMinutes.HasValue) throw new ArgumentException("Minutos trabalhados são exclusivos do pagamento por hora.");
        if (plan.Mode == "shift")
        {
            if ((request.To - request.From).TotalHours > 24) throw new ArgumentException("Um turno deve durar no máximo 24 horas.");
            return new(plan.Rate, null, null);
        }
        // Dia/semana/quinzena/mês são proporcionais à ocorrência de calendário que contém o início:
        // um pedaço parcial (motoboy começou no meio, ou a regra mudou no meio) recebe fração do valor
        // cheio em vez de ficar sem remuneração nenhuma até fechar o período inteiro.
        var (instanceStart, instanceEnd) = PeriodInstance(plan.Mode, from);
        if (to > instanceEnd)
            throw new ArgumentException("O período cruza para a próxima ocorrência da regra (dia/semana/quinzena/mês seguinte). Registre em duas partes, uma para cada ocorrência.");
        var totalSeconds = (long)(instanceEnd - instanceStart).TotalSeconds;
        var workedSeconds = (long)(to - from).TotalSeconds;
        return new(Money(plan.Rate * workedSeconds / totalSeconds), totalSeconds, workedSeconds);
    }
    private static (DateTimeOffset Start, DateTimeOffset End) PeriodInstance(string mode, DateTimeOffset from)
    {
        DateTimeOffset At(DateTime local) => new(local, from.Offset);
        var monthStart = new DateTime(from.Year, from.Month, 1);
        var mondayOffset = ((int)from.Date.DayOfWeek + 6) % 7;
        return mode switch
        {
            "day" => (At(from.Date), At(from.Date.AddDays(1))),
            "week" => (At(from.Date.AddDays(-mondayOffset)), At(from.Date.AddDays(-mondayOffset + 7))),
            "fortnight" => from.Day <= 15 ? (At(monthStart), At(monthStart.AddDays(15))) : (At(monthStart.AddDays(15)), At(monthStart.AddMonths(1))),
            "month" => (At(monthStart), At(monthStart.AddMonths(1))),
            _ => throw new ArgumentException("Modalidade sem período de calendário.")
        };
    }
    public static string NextStatus(string current, string action, bool owner)
    {
        return (current, action, owner) switch
        {
            ("draft" or "disputed", "review", true) => "reviewed",
            ("draft" or "reviewed", "dispute", true) => "disputed",
            ("paid", "receive", true) => "received",
            ("reviewed", "pay", false) => "paid",
            ("draft" or "reviewed" or "disputed", "cancel", false) => "cancelled",
            _ => throw new DeliveryDomainException(409, "SETTLEMENT_STATE", "A situação do acerto mudou ou esta ação não é permitida. Atualize os dados.")
        };
    }
}
