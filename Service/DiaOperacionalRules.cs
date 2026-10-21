using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using APIBack.DTOs.Delivery;

namespace APIBack.Service;

/// <summary>Um único início por dia da semana, usado pelo reset do mapa e pelas vagas.</summary>
public static class DiaOperacionalRules
{
    public static readonly TimeSpan DuracaoMinima = TimeSpan.FromHours(6);
    public static readonly TimeSpan IntervaloMinimoEntreMudancas = TimeSpan.FromDays(7);

    public static OrderWindowDto ValidarNovaConfiguracao(OrderWindowDto? pedido)
    {
        var window = OrderWindowRules.Validate(pedido);
        if (window.Mode == OrderWindowModes.OperationalDay) return window;
        if (window.Mode != OrderWindowModes.Shifts)
            throw Invalid("Use dia operacional ou uma faixa única por dia da semana.");

        var dias = new HashSet<int>();
        foreach (var shift in window.Shifts)
        {
            var inicio = TimeOnly.ParseExact(shift.Start, "HH:mm", CultureInfo.InvariantCulture);
            var fim = TimeOnly.ParseExact(shift.End, "HH:mm", CultureInfo.InvariantCulture);
            var duracao = TimeSpan.FromTicks((fim.ToTimeSpan().Ticks - inicio.ToTimeSpan().Ticks + TimeSpan.TicksPerDay) % TimeSpan.TicksPerDay);
            if (duracao < DuracaoMinima)
                throw Invalid("O dia operacional precisa ter pelo menos 6 horas.");
            foreach (var dia in shift.Days)
                if (!dias.Add(dia)) throw Invalid("Cada dia da semana pode ter somente uma faixa.");
        }

        var hoje = new DateOnly(2026, 10, 18); // domingo; a checagem se repete a cada sete dias.
        var ocorrencias = new List<(DateTimeOffset Inicio, DateTimeOffset Fim)>();
        for (var deslocamento = 0; deslocamento < 14; deslocamento++)
        {
            var data = hoje.AddDays(deslocamento);
            foreach (var shift in window.Shifts.Where(s => s.Days.Contains((int)data.DayOfWeek)))
            {
                var inicio = TimeOnly.ParseExact(shift.Start, "HH:mm", CultureInfo.InvariantCulture);
                var fim = TimeOnly.ParseExact(shift.End, "HH:mm", CultureInfo.InvariantCulture);
                var de = OperationalDayWindow.LocalToUtc(data.ToDateTime(inicio));
                var ate = OperationalDayWindow.LocalToUtc((fim <= inicio ? data.AddDays(1) : data).ToDateTime(fim));
                ocorrencias.Add((de, ate));
            }
        }
        for (var i = 1; i < ocorrencias.Count; i++)
            if (ocorrencias[i - 1].Fim > ocorrencias[i].Inicio)
                throw Invalid("Os dias operacionais não podem se sobrepor.");
        return window;
    }

    public static DateTimeOffset InicioAtual(OrderWindowDto window, DateTimeOffset agoraUtc) =>
        OrderWindowRules.Resolve(window, agoraUtc).FromUtc
        ?? OperationalDayWindow.ToUtcRange(OperationalDayWindow.Today(agoraUtc)).FromUtc;

    public static DateTimeOffset ProximoInicio(OrderWindowDto window, DateTimeOffset agoraUtc)
    {
        if (window.Mode != OrderWindowModes.Shifts || window.Shifts.Count == 0)
            return OperationalDayWindow.ToUtcRange(OperationalDayWindow.Today(agoraUtc).AddDays(1)).FromUtc;
        var hoje = OperationalDayWindow.Today(agoraUtc);
        for (var deslocamento = 0; deslocamento <= 8; deslocamento++)
        {
            var data = hoje.AddDays(deslocamento);
            DateTimeOffset? proximoNoDia = null;
            foreach (var shift in window.Shifts.Where(s => s.Days.Contains((int)data.DayOfWeek)))
            {
                var inicio = TimeOnly.ParseExact(shift.Start, "HH:mm", CultureInfo.InvariantCulture);
                var candidato = OperationalDayWindow.LocalToUtc(data.ToDateTime(inicio));
                if (candidato > agoraUtc && (proximoNoDia == null || candidato < proximoNoDia))
                    proximoNoDia = candidato;
            }
            if (proximoNoDia.HasValue) return proximoNoDia.Value;
        }
        throw Invalid("Não há próximo dia operacional configurado.");
    }

    private static DeliveryDomainException Invalid(string mensagem) =>
        new(422, "DIA_OPERACIONAL_INVALIDO", mensagem);
}
