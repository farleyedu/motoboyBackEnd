using System;
using System.Collections.Generic;
using System.Linq;
using APIBack.Service;

namespace APIBack.Turnos;

/// <summary>Motivos devolvidos pelo servidor (o painel escreve o texto a partir deles).</summary>
public static class MotivosDoTurno
{
    public const string PrimeiroTurno = "primeiro_turno";
    public const string FechadaTempoSuficiente = "fechada_2h";
    public const string Renovacao24h = "renovacao_24h";
    public const string FechadaPoucoTempo = "fechada_menos_de_2h";
    public const string LimiteDoDia = "limite_diario";
    public const string JaAberta = "ja_aberta";
}

/// <summary>Um turno de entrega. Id 0 = ainda não gravado.</summary>
public sealed record TurnoEntrega(long Id, DateTimeOffset IniciadoEm, DateOnly Dia, int NumeroNoDia, string Motivo);

/// <summary>Como a loja estava na última avaliação. <see cref="TurnosPorDia"/> conta os turnos já criados em cada dia do turno.</summary>
public sealed record EstadoDosTurnos(
    DateTimeOffset AvaliadoEm, bool Aberta, DateTimeOffset? FechadaDesde, TurnoEntrega? Turno,
    IReadOnlyDictionary<DateOnly, int> TurnosPorDia);

/// <summary>Período em que a loja fica aberta, [Inicio, Fim).</summary>
public readonly record struct PeriodoAberto(DateTimeOffset Inicio, DateTimeOffset Fim);

public sealed record AvancoDosTurnos(EstadoDosTurnos Estado, IReadOnlyList<TurnoEntrega> Novos, bool HouveAbertura);

public sealed record PreviaDeAbertura(bool NovoTurno, string Motivo, DateTimeOffset? ContinuaDesde);

/// <summary>
/// Regra única do turno de entrega (sem banco, para testar). O turno começa quando a loja abre e termina quando fecha.
/// Turno novo (créditos zerados) só depois de 2 horas seguidas fechada, no máximo 2 por dia (o dia vira às 5h no fuso da
/// loja); fora disso continua o anterior. Loja aberta direto renova a cada 24 horas. "Fechada" é fora do horário efetivo
/// (grade, exceções e fechar pelo painel): pausar pedidos não fecha.
/// </summary>
public static class TurnoEntregaRegras
{
    public static readonly TimeSpan FechadaParaTurnoNovo = TimeSpan.FromHours(2);
    public static readonly TimeSpan DuracaoMaximaAberta = TimeSpan.FromHours(24);
    public const int TurnosNovosPorDia = 2;
    public static readonly TimeSpan ViradaDoDia = TimeSpan.FromHours(5);
    /// <summary>Quanto tempo para trás se reconstrói quando o servidor ficou muito tempo sem avaliar.</summary>
    public static readonly TimeSpan ReconstrucaoMaxima = TimeSpan.FromDays(35);

    public static DateOnly DiaDoTurno(DateTimeOffset instante, TimeZoneInfo fuso) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instante, fuso).DateTime - ViradaDoDia);

    /// <summary>
    /// Leva o estado até <paramref name="agora"/> com os períodos abertos desse intervalo. Só anda para a frente: o que
    /// já foi avaliado nunca é recalculado, mesmo que o horário cadastrado mude depois.
    /// </summary>
    public static AvancoDosTurnos Avancar(
        EstadoDosTurnos estado, IReadOnlyList<PeriodoAberto> abertos, DateTimeOffset agora, TimeZoneInfo fuso)
    {
        if (agora < estado.AvaliadoEm) agora = estado.AvaliadoEm;
        var cursor = estado.AvaliadoEm;
        var aberta = estado.Aberta;
        var fechadaDesde = estado.FechadaDesde;
        var turno = estado.Turno;
        var porDia = new Dictionary<DateOnly, int>(estado.TurnosPorDia);
        var novos = new List<TurnoEntrega>();
        var houveAbertura = false;

        void Criar(DateTimeOffset em, string motivo)
        {
            var dia = DiaDoTurno(em, fuso);
            var numero = porDia.GetValueOrDefault(dia) + 1;
            porDia[dia] = numero;
            turno = new TurnoEntrega(0, em, dia, numero, motivo);
            novos.Add(turno);
        }

        var relevantes = abertos.Where(p => p.Fim > estado.AvaliadoEm && p.Inicio <= agora).OrderBy(p => p.Inicio).ToList();
        foreach (var periodo in relevantes)
        {
            if (periodo.Fim <= cursor) continue;
            var inicio = periodo.Inicio > cursor ? periodo.Inicio : cursor;
            if (inicio > cursor && aberta)
            {
                aberta = false;
                fechadaDesde = cursor;
            }
            if (!aberta)
            {
                var motivo = MotivoDoTurnoNovo(turno, fechadaDesde, inicio, porDia, fuso);
                if (motivo != null) Criar(inicio, motivo);
                aberta = true;
                fechadaDesde = null;
                houveAbertura = true;
            }

            var ate = periodo.Fim < agora ? periodo.Fim : agora;
            if (turno != null && inicio - turno.IniciadoEm >= DuracaoMaximaAberta) Criar(inicio, MotivosDoTurno.Renovacao24h);
            while (turno != null && turno.IniciadoEm + DuracaoMaximaAberta <= ate && turno.IniciadoEm + DuracaoMaximaAberta < periodo.Fim)
                Criar(turno.IniciadoEm + DuracaoMaximaAberta, MotivosDoTurno.Renovacao24h);

            cursor = ate;
            if (periodo.Fim <= agora)
            {
                aberta = false;
                fechadaDesde = periodo.Fim;
            }
        }

        // Estava aberta e agora não está (inclusive quando a loja acabou de ser fechada, no mesmo instante).
        if (aberta && !abertos.Any(p => p.Inicio <= agora && agora < p.Fim))
        {
            aberta = false;
            fechadaDesde = cursor;
        }

        return new AvancoDosTurnos(new EstadoDosTurnos(agora, aberta, fechadaDesde, turno, porDia), novos, houveAbertura);
    }

    /// <summary>"Se a loja abrir agora": começa um turno novo ou continua o anterior.</summary>
    public static PreviaDeAbertura Previa(EstadoDosTurnos estado, DateTimeOffset agora, TimeZoneInfo fuso)
    {
        if (estado.Aberta) return new PreviaDeAbertura(false, MotivosDoTurno.JaAberta, estado.Turno?.IniciadoEm);
        var motivo = MotivoDoTurnoNovo(estado.Turno, estado.FechadaDesde, agora, estado.TurnosPorDia, fuso);
        if (motivo != null) return new PreviaDeAbertura(true, motivo, null);
        var fechadaTempoSuficiente = estado.FechadaDesde.HasValue && agora - estado.FechadaDesde.Value >= FechadaParaTurnoNovo;
        return new PreviaDeAbertura(false,
            fechadaTempoSuficiente ? MotivosDoTurno.LimiteDoDia : MotivosDoTurno.FechadaPoucoTempo, estado.Turno!.IniciadoEm);
    }

    /// <summary>Nulo = a abertura neste instante continua o turno anterior.</summary>
    private static string? MotivoDoTurnoNovo(TurnoEntrega? turno, DateTimeOffset? fechadaDesde, DateTimeOffset abertura,
        IReadOnlyDictionary<DateOnly, int> porDia, TimeZoneInfo fuso)
    {
        if (turno == null) return MotivosDoTurno.PrimeiroTurno;
        if (fechadaDesde.HasValue && abertura - fechadaDesde.Value >= FechadaParaTurnoNovo &&
            porDia.GetValueOrDefault(DiaDoTurno(abertura, fuso)) < TurnosNovosPorDia)
            return MotivosDoTurno.FechadaTempoSuficiente;
        return abertura - turno.IniciadoEm >= DuracaoMaximaAberta ? MotivosDoTurno.Renovacao24h : null;
    }

    /// <summary>Períodos abertos entre <paramref name="de"/> e <paramref name="ate"/>, já emendados, pela regra de <see cref="HorarioRegras"/>.</summary>
    public static IReadOnlyList<PeriodoAberto> PeriodosAbertos(HorarioSemana? horario, TimeZoneInfo fuso, DateTimeOffset de, DateTimeOffset ate)
    {
        var lista = new List<PeriodoAberto>();
        if (horario == null) return lista;
        var primeiro = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(de, fuso).DateTime).AddDays(-1);
        var ultimo = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(ate, fuso).DateTime).AddDays(1);
        for (var data = primeiro; data <= ultimo; data = data.AddDays(1))
        {
            if (HorarioRegras.DiaEfetivo(horario, data) is not { Aberto: true } dia) continue;
            var abre = data.ToDateTime(TimeOnly.MinValue) + dia.Abre!.Value;
            // Fecha antes de abrir = vira a noite; fecha igual a abrir = vai até a meia-noite.
            var fecha = dia.Fecha > dia.Abre ? data.ToDateTime(TimeOnly.MinValue) + dia.Fecha!.Value
                : data.AddDays(1).ToDateTime(TimeOnly.MinValue) + (dia.Fecha < dia.Abre ? dia.Fecha!.Value : TimeSpan.Zero);
            var periodo = new PeriodoAberto(ParaUtc(abre, fuso), ParaUtc(fecha, fuso));
            if (lista.Count > 0 && periodo.Inicio <= lista[^1].Fim)
            {
                if (periodo.Fim > lista[^1].Fim) lista[^1] = new PeriodoAberto(lista[^1].Inicio, periodo.Fim);
            }
            else lista.Add(periodo);
        }
        return lista.Where(p => p.Fim > de && p.Inicio <= ate).ToList();
    }

    private static DateTimeOffset ParaUtc(DateTime local, TimeZoneInfo fuso)
    {
        var semFuso = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        // Hora que não existe (início de horário de verão) vale a hora seguinte.
        if (fuso.IsInvalidTime(semFuso)) semFuso = semFuso.AddHours(1);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(semFuso, fuso), TimeSpan.Zero);
    }
}
