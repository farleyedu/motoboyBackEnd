using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace APIBack.Service
{
    /// <summary>Um dia de funcionamento: aberto de <see cref="Abre"/> ate <see cref="Fecha"/> (fecha &lt;= abre = vira a meia-noite).</summary>
    public sealed record HorarioDia(bool Fechado, TimeSpan? Abre, TimeSpan? Fecha)
    {
        public bool Aberto => !Fechado && Abre.HasValue && Fecha.HasValue;
    }

    /// <summary>
    /// Horario da loja visto pela regra: grade semanal (chave = <see cref="DayOfWeek"/>, 0 = domingo) e excecoes por data.
    /// <see cref="TemSemanal"/> diz se a loja chegou a cadastrar a grade (mesmo com todos os dias fechados).
    /// </summary>
    public sealed class HorarioSemana
    {
        public bool TemSemanal { get; init; }
        public IReadOnlyDictionary<DayOfWeek, HorarioDia> Semanal { get; init; } = new Dictionary<DayOfWeek, HorarioDia>();
        public IReadOnlyDictionary<DateOnly, HorarioDia> Especiais { get; init; } = new Dictionary<DateOnly, HorarioDia>();
    }

    /// <summary>Resultado de "a loja esta dentro do horario agora?".</summary>
    public sealed record AberturaHorario(bool Dentro, string? Motivo, DateTime? ProximaAbertura, string? ProximaAberturaTexto)
    {
        public static readonly AberturaHorario Aberta = new(true, null, null, null);
    }

    /// <summary>O que gravar para fechar a loja agora: excecao da data informada, de ate "Fecha" (ou fechado o dia todo).</summary>
    public sealed record PlanoFechamento(DateOnly Data, bool Fechado, TimeSpan? Abre, TimeSpan? Fecha);

    /// <summary>
    /// A UNICA regra de "a loja esta no horario?". Cardapio web (cotacao e criacao), snapshot publico, bot, nucleo de pedidos e
    /// abrir/fechar do painel chamam estas funcoes. Sem grade semanal e sem excecao para o dia = FECHADA (SEM_HORARIO); fora do
    /// horario = FECHADA (FORA_HORARIO, com a proxima abertura). Aceita virada de meia-noite e excecoes por data.
    /// </summary>
    public static class HorarioRegras
    {
        public const string SemHorario = "SEM_HORARIO";
        public const string ForaHorario = "FORA_HORARIO";
        public const string Pausada = "PAUSADA";

        private static readonly string[] NomesDosDias = { "domingo", "segunda", "terça", "quarta", "quinta", "sexta", "sábado" };

        /// <summary>O dia vale pela excecao da data, se houver; senao pela grade semanal; senao nao tem nada cadastrado (nulo).</summary>
        public static HorarioDia? DiaEfetivo(HorarioSemana? horario, DateOnly data)
        {
            if (horario == null) return null;
            if (horario.Especiais.TryGetValue(data, out var especial)) return especial;
            return horario.Semanal.TryGetValue(data.DayOfWeek, out var dia) ? dia : null;
        }

        /// <summary>Formula de turno: o de hoje (inclusive o que vira a noite) ou a madrugada do turno de ontem.</summary>
        public static bool AbertoEm(HorarioDia? hoje, HorarioDia? ontem, TimeSpan hora) =>
            (hoje is { Aberto: true } && hora >= hoje.Abre && (hoje.Fecha <= hoje.Abre || hora < hoje.Fecha)) ||
            (ontem is { Aberto: true } && ontem.Fecha < ontem.Abre && hora < ontem.Fecha);

        public static bool DentroDoHorario(HorarioSemana? horario, DateTime localNow)
        {
            var data = DateOnly.FromDateTime(localNow);
            return AbertoEm(DiaEfetivo(horario, data), DiaEfetivo(horario, data.AddDays(-1)), localNow.TimeOfDay);
        }

        public static AberturaHorario Avaliar(HorarioSemana? horario, DateTime localNow)
        {
            if (DentroDoHorario(horario, localNow)) return AberturaHorario.Aberta;
            var data = DateOnly.FromDateTime(localNow);
            var semNada = horario == null || (!horario.TemSemanal && !horario.Especiais.ContainsKey(data));
            var proxima = ProximaAbertura(horario, localNow);
            return new AberturaHorario(false, semNada ? SemHorario : ForaHorario, proxima, proxima.HasValue ? Descrever(proxima.Value, localNow) : null);
        }

        /// <summary>Proxima abertura nos proximos 7 dias (hoje, se a loja ainda vai abrir hoje); nulo sem nenhum dia aberto.</summary>
        public static DateTime? ProximaAbertura(HorarioSemana? horario, DateTime localNow)
        {
            if (horario == null) return null;
            DateTime? melhor = null;
            for (var offset = 0; offset <= 7; offset++)
            {
                var data = DateOnly.FromDateTime(localNow).AddDays(offset);
                if (DiaEfetivo(horario, data) is not { Aberto: true } dia) continue;
                var candidato = data.ToDateTime(TimeOnly.FromTimeSpan(dia.Abre!.Value));
                if (candidato > localNow && (melhor == null || candidato < melhor)) melhor = candidato;
            }
            return melhor;
        }

        /// <summary>"hoje às 18:00", "amanhã às 11:00" ou "quarta às 11:00".</summary>
        public static string Descrever(DateTime abertura, DateTime localNow)
        {
            var hora = abertura.ToString("HH:mm", CultureInfo.InvariantCulture);
            return (abertura.Date - localNow.Date).Days switch
            {
                0 => $"hoje às {hora}",
                1 => $"amanhã às {hora}",
                _ => $"{NomesDosDias[(int)abertura.DayOfWeek]} às {hora}"
            };
        }

        /// <summary>
        /// Fechar agora, sem mexer na grade: no turno de hoje a excecao de hoje passa a fechar neste minuto; na madrugada de um turno que
        /// virou a noite a excecao de ontem e que fecha agora. Nulo = a loja ja esta fechada pelo horario (nada a fazer).
        /// </summary>
        public static PlanoFechamento? PlanejarFechamento(HorarioSemana? horario, DateTime localNow)
        {
            var data = DateOnly.FromDateTime(localNow);
            var hora = new TimeSpan(localNow.Hour, localNow.Minute, 0);
            var hoje = DiaEfetivo(horario, data);
            var ontem = DiaEfetivo(horario, data.AddDays(-1));

            if (hoje is { Aberto: true } && hora >= hoje.Abre && (hoje.Fecha <= hoje.Abre || hora < hoje.Fecha))
                return hora == hoje.Abre
                    ? new PlanoFechamento(data, true, null, null)
                    : new PlanoFechamento(data, false, hoje.Abre, hora);
            if (ontem is { Aberto: true } && ontem.Fecha < ontem.Abre && hora < ontem.Fecha)
                return new PlanoFechamento(data.AddDays(-1), false, ontem.Abre, hora);
            return null;
        }

        /// <summary>Converte "HH:mm"; texto invalido = nulo.</summary>
        public static TimeSpan? Hora(string? texto) =>
            TimeSpan.TryParse(texto, CultureInfo.InvariantCulture, out var valor) ? valor : null;

        /// <summary>Fuso da loja (estabelecimentos.timezone_iana); vazio ou invalido cai em America/Sao_Paulo.</summary>
        public static TimeZoneInfo ResolverFuso(string? timezoneIana)
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(string.IsNullOrWhiteSpace(timezoneIana) ? "America/Sao_Paulo" : timezoneIana.Trim());
            }
            catch (TimeZoneNotFoundException)
            {
                return PadraoSaoPaulo();
            }
            catch (InvalidTimeZoneException)
            {
                return PadraoSaoPaulo();
            }
        }

        private static TimeZoneInfo PadraoSaoPaulo()
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");
            }
            catch (TimeZoneNotFoundException)
            {
                return TimeZoneInfo.CreateCustomTimeZone("America/Sao_Paulo", TimeSpan.FromHours(-3), "Brasilia", "Brasilia");
            }
        }

        public static DateTime ParaHorarioLocal(DateTime utc, string? timezoneIana) =>
            TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), ResolverFuso(timezoneIana));
    }
}
