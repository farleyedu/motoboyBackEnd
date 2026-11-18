using APIBack.Equipe.Ponto;
using Xunit;

namespace APIBack.Tests.Unit.Equipe.Ponto;

public sealed class PontoRegrasTests
{
    private static readonly TimeZoneInfo SaoPaulo = TimeZoneInfo.FindSystemTimeZoneById(
        OperatingSystem.IsWindows() ? "E. South America Standard Time" : "America/Sao_Paulo");
    private static readonly TimeZoneInfo Manaus = TimeZoneInfo.FindSystemTimeZoneById(
        OperatingSystem.IsWindows() ? "Central Brazilian Standard Time" : "America/Manaus");

    [Fact]
    public void Fuso_da_loja_decide_dia_e_hora_nao_o_utc()
    {
        // 02:30 UTC de 18/11 = 23:30 de 17/11 em São Paulo e 22:30 em Manaus.
        var quando = new DateTimeOffset(2026, 11, 18, 2, 30, 0, TimeSpan.Zero);
        Assert.Equal(new DateOnly(2026, 11, 17), PontoRegras.DiaLocal(quando, SaoPaulo));
        Assert.Equal("23:30", PontoRegras.HoraLocal(quando, SaoPaulo));
        Assert.Equal("22:30", PontoRegras.HoraLocal(quando, Manaus));
        Assert.Equal(new DateOnly(2026, 11, 18), PontoRegras.DiaLocal(quando, TimeZoneInfo.Utc));
    }

    [Fact]
    public void Limites_do_dia_local_em_utc()
    {
        var (ini, fim) = PontoRegras.LimitesDoDia(new DateOnly(2026, 11, 17), SaoPaulo);
        Assert.Equal(new DateTimeOffset(2026, 11, 17, 3, 0, 0, TimeSpan.Zero), ini.ToUniversalTime());
        Assert.Equal(new DateTimeOffset(2026, 11, 18, 3, 0, 0, TimeSpan.Zero), fim.ToUniversalTime());
    }

    [Fact]
    public void Minutos_trabalhados_descontam_pausas_inclusive_a_aberta()
    {
        var e = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
        var pausas = new List<(DateTimeOffset, DateTimeOffset?)> { (e.AddMinutes(60), e.AddMinutes(90)), (e.AddMinutes(200), null) };
        Assert.Equal(30 + 40, PontoRegras.MinutosPausa(pausas, e.AddMinutes(240)));
        Assert.Equal(240 - 70, PontoRegras.MinutosTrabalhados(e, null, pausas, e.AddMinutes(240)));
        // Saída fecha a conta: depois dela o relógio não conta mais.
        Assert.Equal(210 - 40, PontoRegras.MinutosTrabalhados(e, e.AddMinutes(210), pausas, e.AddMinutes(999)));
    }

    [Theory]
    [InlineData("garcom", "fixo", "garcom")]
    [InlineData("gerente_salao", "fixo", "garcom")]
    [InlineData("bar", "fixo", "cozinha")]
    [InlineData("caixa", "diarista", "caixa")]
    [InlineData(null, "motoboy", "motoboy")]
    [InlineData(null, "fixo", null)]
    public void Modo_padrao_vem_da_funcao(string? funcao, string vinculo, string? esperado) =>
        Assert.Equal(esperado, PontoRegras.ModoPadrao(funcao, vinculo));

    [Fact]
    public void Sem_turno_ou_previsto_fora_do_livro_preenche_com_o_horario_real()
    {
        var sem = PontoRegras.DecidirTurno(null, "18:02", "23:41", null);
        Assert.Equal(new DecisaoTurno(AcaoTurno.Preencher, "18:02", "23:41"), sem);
        var previsto = PontoRegras.DecidirTurno(new TurnoAtual("previsto", "18:00", "23:00", false), "18:02", "23:41", null);
        Assert.Equal(AcaoTurno.Preencher, previsto.Acao);
        Assert.Equal("23:41", previsto.Saida);
    }

    [Fact]
    public void Virada_de_dia_preenche_entrada_da_noite_e_saida_da_madrugada()
    {
        var d = PontoRegras.DecidirTurno(null, "22:10", "02:05", null);
        Assert.Equal(("22:10", "02:05"), (d.Entrada, d.Saida));
    }

    [Theory]
    [InlineData("realizado", false)]
    [InlineData("falta", false)]
    [InlineData("cancelado", false)]
    [InlineData("previsto", true)]
    public void Turno_que_o_gerente_ja_decidiu_fica_como_esta(string status, bool noLivro)
    {
        var d = PontoRegras.DecidirTurno(new TurnoAtual(status, "18:00", "23:00", noLivro), "18:02", "23:41", null);
        Assert.Equal(AcaoTurno.Manter, d.Acao);
    }

    [Fact]
    public void Segundo_ponto_do_dia_estende_a_saida_se_ninguem_mexeu_no_turno()
    {
        var anterior = new PontoAnteriorNoDia(PontoRegras.Preenchido, "11:00", "15:00");
        var d = PontoRegras.DecidirTurno(new TurnoAtual("realizado", "11:00", "15:00", true), "18:00", "23:00", anterior);
        Assert.Equal(AcaoTurno.Preencher, d.Acao);
        Assert.Equal(("11:00", "23:00"), (d.Entrada, d.Saida));
        Assert.NotNull(d.Motivo);

        // O gerente trocou o horário depois do primeiro ponto: não sobrescreve.
        var editado = PontoRegras.DecidirTurno(new TurnoAtual("realizado", "11:00", "16:00", true), "18:00", "23:00", anterior);
        Assert.Equal(AcaoTurno.Manter, editado.Acao);
    }

    [Fact]
    public void Ponto_de_menos_de_um_minuto_nao_preenche()
    {
        Assert.Equal(AcaoTurno.NaoAplicar, PontoRegras.DecidirTurno(null, "18:00", "18:00", null).Acao);
    }
}
