using System;
using System.Collections.Generic;
using System.Linq;
using APIBack.Service;
using APIBack.Turnos;
using Xunit;

namespace APIBack.Tests.Unit;

public sealed class TurnoEntregaRegrasTests
{
    private static readonly TimeZoneInfo Fuso = HorarioRegras.ResolverFuso("America/Sao_Paulo");

    /// <summary>Hora local de São Paulo (UTC-3) em outubro de 2026; dia 20 é terça.</summary>
    private static DateTimeOffset Em(int dia, int hora, int minuto = 0) =>
        new DateTimeOffset(2026, 10, dia, hora, minuto, 0, TimeSpan.FromHours(-3)).ToUniversalTime();

    private static PeriodoAberto Aberto(DateTimeOffset de, DateTimeOffset ate) => new(de, ate);

    private static EstadoDosTurnos Inicial(DateTimeOffset em) => new(em, false, null, null, new Dictionary<DateOnly, int>());

    private static EstadoDosTurnos Ate(EstadoDosTurnos estado, DateTimeOffset agora, params PeriodoAberto[] periodos) =>
        TurnoEntregaRegras.Avancar(estado, periodos, agora, Fuso).Estado;

    private static readonly PeriodoAberto[] Exemplo =
    {
        Aberto(Em(20, 11), Em(20, 15)),
        Aberto(Em(20, 15, 30), Em(20, 15, 40)),
        Aberto(Em(20, 18), Em(20, 19)),
        Aberto(Em(20, 21, 30), Em(20, 23))
    };

    [Fact]
    public void Exemplo_do_dia_reabrir_cedo_continua_apos_2h_comeca_outro_e_o_terceiro_continua()
    {
        var onze = Ate(Inicial(Em(20, 9)), Em(20, 11), Exemplo);
        Assert.True(onze.Aberta);
        Assert.Equal(Em(20, 11), onze.Turno!.IniciadoEm);
        Assert.Equal(1, onze.Turno.NumeroNoDia);
        Assert.Equal(MotivosDoTurno.PrimeiroTurno, onze.Turno.Motivo);

        var quinzeEMeia = Ate(onze, Em(20, 15, 30), Exemplo);
        Assert.True(quinzeEMeia.Aberta);
        Assert.Equal(Em(20, 11), quinzeEMeia.Turno!.IniciadoEm);

        var dezoito = Ate(quinzeEMeia, Em(20, 18), Exemplo);
        Assert.Equal(Em(20, 18), dezoito.Turno!.IniciadoEm);
        Assert.Equal(2, dezoito.Turno.NumeroNoDia);

        var noite = Ate(dezoito, Em(20, 21, 30), Exemplo);
        Assert.True(noite.Aberta);
        Assert.Equal(Em(20, 18), noite.Turno!.IniciadoEm);
        Assert.Equal(2, noite.Turno.NumeroNoDia);
    }

    [Fact]
    public void Calculo_de_uma_vez_so_chega_no_mesmo_resultado_sem_ninguem_abrir_a_tela()
    {
        var avanco = TurnoEntregaRegras.Avancar(Inicial(Em(20, 9)), Exemplo, Em(20, 23, 30), Fuso);
        Assert.Equal(new[] { Em(20, 11), Em(20, 18) }, avanco.Novos.Select(t => t.IniciadoEm));
        Assert.False(avanco.Estado.Aberta);
        Assert.Equal(Em(20, 23), avanco.Estado.FechadaDesde);
        Assert.True(avanco.HouveAbertura);
    }

    [Fact]
    public void Previa_diz_se_abrir_agora_continua_ou_comeca()
    {
        var fechada = Ate(Inicial(Em(20, 9)), Em(20, 15, 10), Exemplo);
        var continua = TurnoEntregaRegras.Previa(fechada, Em(20, 15, 10), Fuso);
        Assert.False(continua.NovoTurno);
        Assert.Equal(MotivosDoTurno.FechadaPoucoTempo, continua.Motivo);
        Assert.Equal(Em(20, 11), continua.ContinuaDesde);

        var depois = TurnoEntregaRegras.Previa(Ate(fechada, Em(20, 17, 40), Exemplo.Take(2).ToArray()), Em(20, 17, 40), Fuso);
        Assert.True(depois.NovoTurno);
        Assert.Equal(MotivosDoTurno.FechadaTempoSuficiente, depois.Motivo);

        var doisHoje = Ate(Inicial(Em(20, 9)), Em(20, 23), Exemplo.Take(3).ToArray());
        var limite = TurnoEntregaRegras.Previa(doisHoje, Em(20, 23), Fuso);
        Assert.False(limite.NovoTurno);
        Assert.Equal(MotivosDoTurno.LimiteDoDia, limite.Motivo);
        Assert.Equal(Em(20, 18), limite.ContinuaDesde);
    }

    [Fact]
    public void O_dia_do_turno_vira_as_cinco_da_manha()
    {
        var periodos = new[]
        {
            Aberto(Em(20, 11), Em(20, 12)), Aberto(Em(20, 18), Em(20, 19)),
            Aberto(Em(21, 4), Em(21, 4, 30)), Aberto(Em(21, 8), Em(21, 9))
        };
        var madrugada = Ate(Inicial(Em(20, 9)), Em(21, 4), periodos);
        Assert.Equal(Em(20, 18), madrugada.Turno!.IniciadoEm); // 4h ainda é o dia 20: limite atingido
        var manha = Ate(madrugada, Em(21, 8), periodos);
        Assert.Equal(Em(21, 8), manha.Turno!.IniciadoEm);
        Assert.Equal(1, manha.Turno.NumeroNoDia);
        Assert.Equal(new DateOnly(2026, 10, 20), TurnoEntregaRegras.DiaDoTurno(Em(21, 4, 59), Fuso));
        Assert.Equal(new DateOnly(2026, 10, 21), TurnoEntregaRegras.DiaDoTurno(Em(21, 5), Fuso));
    }

    [Fact]
    public void Aberta_direto_renova_a_cada_24_horas()
    {
        var direto = new[] { Aberto(Em(20, 11), Em(23, 12)) };
        var avanco = TurnoEntregaRegras.Avancar(Inicial(Em(20, 11)), direto, Em(22, 20), Fuso);
        Assert.Equal(new[] { Em(20, 11), Em(21, 11), Em(22, 11) }, avanco.Novos.Select(t => t.IniciadoEm));
        Assert.All(avanco.Novos.Skip(1), t => Assert.Equal(MotivosDoTurno.Renovacao24h, t.Motivo));

        var antes = Ate(Inicial(Em(20, 11)), Em(21, 10, 59), direto);
        Assert.Equal(Em(20, 11), antes.Turno!.IniciadoEm);
    }

    [Fact]
    public void Pausa_curta_depois_de_24_horas_renova_na_reabertura()
    {
        var periodos = new[] { Aberto(Em(20, 11), Em(21, 10, 30)), Aberto(Em(21, 11, 30), Em(21, 20)) };
        var estado = Ate(Inicial(Em(20, 11)), Em(21, 11, 30), periodos);
        Assert.Equal(Em(21, 11, 30), estado.Turno!.IniciadoEm);
    }

    [Fact]
    public void Mudar_o_horario_nao_reescreve_o_passado()
    {
        var antes = Ate(Inicial(Em(20, 9)), Em(20, 14), Aberto(Em(20, 11), Em(20, 15)));
        // O horário novo diz que a loja só abria às 19h: o turno das 11:00 continua existindo, fechado a partir de agora.
        var depois = TurnoEntregaRegras.Avancar(antes, new[] { Aberto(Em(20, 19), Em(20, 23)) }, Em(20, 14), Fuso);
        Assert.Empty(depois.Novos);
        Assert.False(depois.Estado.Aberta);
        Assert.Equal(Em(20, 14), depois.Estado.FechadaDesde);
        Assert.Equal(Em(20, 11), depois.Estado.Turno!.IniciadoEm);
    }

    [Fact]
    public void Relogio_que_volta_nao_anda_para_tras()
    {
        var estado = Ate(Inicial(Em(20, 9)), Em(20, 14), Exemplo);
        var avanco = TurnoEntregaRegras.Avancar(estado, Exemplo, Em(20, 10), Fuso);
        Assert.Equal(Em(20, 14), avanco.Estado.AvaliadoEm);
        Assert.Empty(avanco.Novos);
    }

    [Fact]
    public void Periodos_seguem_a_regra_do_horario_com_virada_e_emenda()
    {
        var noite = new HorarioDia(false, new TimeSpan(18, 0, 0), new TimeSpan(2, 0, 0));
        var semana = new HorarioSemana
        {
            TemSemanal = true,
            Semanal = new Dictionary<DayOfWeek, HorarioDia> { [DayOfWeek.Tuesday] = noite },
            Especiais = new Dictionary<DateOnly, HorarioDia>()
        };
        var periodo = Assert.Single(TurnoEntregaRegras.PeriodosAbertos(semana, Fuso, Em(20, 0), Em(21, 12)));
        Assert.Equal(Em(20, 18), periodo.Inicio);
        Assert.Equal(Em(21, 2), periodo.Fim);

        var todoDia = new HorarioDia(false, TimeSpan.Zero, TimeSpan.Zero);
        var direto = new HorarioSemana
        {
            TemSemanal = true,
            Semanal = Enum.GetValues<DayOfWeek>().ToDictionary(d => d, _ => todoDia),
            Especiais = new Dictionary<DateOnly, HorarioDia>()
        };
        Assert.Single(TurnoEntregaRegras.PeriodosAbertos(direto, Fuso, Em(20, 12), Em(23, 12)));
        Assert.Empty(TurnoEntregaRegras.PeriodosAbertos(null, Fuso, Em(20, 12), Em(23, 12)));
    }

    [Fact]
    public void Fechar_pelo_painel_encerra_o_periodo_naquele_minuto()
    {
        var semana = new HorarioSemana
        {
            TemSemanal = true,
            Semanal = new Dictionary<DayOfWeek, HorarioDia> { [DayOfWeek.Tuesday] = new(false, new TimeSpan(11, 0, 0), new TimeSpan(23, 0, 0)) },
            Especiais = new Dictionary<DateOnly, HorarioDia> { [new DateOnly(2026, 10, 20)] = new(false, new TimeSpan(11, 0, 0), new TimeSpan(15, 0, 0)) }
        };
        var periodo = Assert.Single(TurnoEntregaRegras.PeriodosAbertos(semana, Fuso, Em(20, 12), Em(20, 20)));
        Assert.Equal(Em(20, 15), periodo.Fim);
    }
}
