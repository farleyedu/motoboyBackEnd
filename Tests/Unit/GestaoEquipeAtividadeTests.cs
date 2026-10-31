using APIBack.GestaoLogica.Equipe;
using Xunit;

namespace APIBack.Tests.Unit;

/// <summary>Janela do dia usada para apurar as entregas do turno (hora da loja, com a virada da noite).</summary>
public class GestaoEquipeAtividadeTests
{
    private static readonly TimeZoneInfo Brasilia = TimeZoneInfo.CreateCustomTimeZone("loja", TimeSpan.FromHours(-3), "loja", "loja");
    private static readonly DateOnly Sexta = new(2026, 10, 23);

    [Fact]
    public void Turno_comum_vai_da_meia_noite_a_meia_noite_da_loja()
    {
        var (de, ate) = EquipeService.JanelaDoTurno(Brasilia, Sexta, "18:00", "23:00");
        Assert.Equal(new DateTimeOffset(2026, 10, 23, 3, 0, 0, TimeSpan.Zero), de.ToUniversalTime());
        Assert.Equal(new DateTimeOffset(2026, 10, 24, 3, 0, 0, TimeSpan.Zero), ate.ToUniversalTime());
    }

    [Fact]
    public void Turno_que_vira_a_noite_vai_ate_a_hora_de_saida_do_dia_seguinte()
    {
        var (_, ate) = EquipeService.JanelaDoTurno(Brasilia, Sexta, "20:00", "02:00");
        Assert.Equal(new DateTimeOffset(2026, 10, 24, 5, 0, 0, TimeSpan.Zero), ate.ToUniversalTime());
    }

    [Fact]
    public void Sem_horario_usa_o_dia_inteiro()
    {
        var (de, ate) = EquipeService.JanelaDoTurno(Brasilia, Sexta, null, null);
        Assert.Equal(TimeSpan.FromHours(24), ate - de);
    }
}
