using System;
using APIBack.Repository;
using Xunit;

namespace APIBack.Tests.Unit;

public sealed class HorarioOperacaoViradaTests
{
    [Theory]
    [InlineData(18, 3, 23, true)]
    [InlineData(18, 3, 4, false)]
    [InlineData(9, 17, 12, true)]
    [InlineData(9, 17, 18, false)]
    public void TurnoDoDia(int abre, int fecha, int agora, bool esperado)
    {
        Assert.Equal(esperado, HorarioOperacaoRepository.HorarioAbertoEm(
            TimeSpan.FromHours(abre), TimeSpan.FromHours(fecha), false,
            null, null, true, TimeSpan.FromHours(agora)));
    }

    [Fact]
    public void TurnoDeOntemContinuaAposMeiaNoite()
    {
        Assert.True(HorarioOperacaoRepository.HorarioAbertoEm(
            null, null, true, TimeSpan.FromHours(18), TimeSpan.FromHours(3), false,
            TimeSpan.FromHours(2)));
        Assert.False(HorarioOperacaoRepository.HorarioAbertoEm(
            null, null, true, TimeSpan.FromHours(18), TimeSpan.FromHours(3), false,
            TimeSpan.FromHours(3)));
    }
}
