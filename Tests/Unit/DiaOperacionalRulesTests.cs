using System;
using System.Collections.Generic;
using APIBack.DTOs.Delivery;
using APIBack.Service;
using Xunit;

namespace APIBack.Tests.Unit;

public sealed class DiaOperacionalRulesTests
{
    private static OrderWindowDto Faixa(params OrderWindowShiftDto[] shifts) => new()
    {
        Mode = OrderWindowModes.Shifts,
        Shifts = new List<OrderWindowShiftDto>(shifts)
    };

    [Theory]
    [InlineData("last_hours")]
    [InlineData("custom")]
    public void Modo_deslizante_ou_livre_nao_pode_ser_salvo(string mode) =>
        Assert.Throws<DeliveryDomainException>(() => DiaOperacionalRules.ValidarNovaConfiguracao(
            new OrderWindowDto { Mode = mode, Hours = 10, CustomFrom = "2026-10-20T00:00" }));

    [Fact]
    public void Uma_faixa_de_seis_horas_pode_passar_da_meia_noite()
    {
        var window = DiaOperacionalRules.ValidarNovaConfiguracao(Faixa(new OrderWindowShiftDto
        {
            Days = new List<int> { 1, 2, 3, 4, 5 }, Start = "21:00", End = "03:00"
        }));
        var agora = new DateTimeOffset(2026, 10, 20, 1, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero),
            DiaOperacionalRules.InicioAtual(window, agora));
    }

    [Fact]
    public void Duas_faixas_no_mesmo_dia_sao_rejeitadas()
    {
        Assert.Throws<DeliveryDomainException>(() => DiaOperacionalRules.ValidarNovaConfiguracao(Faixa(
            new OrderWindowShiftDto { Days = new List<int> { 1 }, Start = "08:00", End = "14:00" },
            new OrderWindowShiftDto { Days = new List<int> { 1 }, Start = "15:00", End = "21:00" })));
    }

    [Fact]
    public void Faixas_que_se_sobrepoem_na_virada_sao_rejeitadas()
    {
        Assert.Throws<DeliveryDomainException>(() => DiaOperacionalRules.ValidarNovaConfiguracao(Faixa(
            new OrderWindowShiftDto { Days = new List<int> { 1 }, Start = "21:00", End = "05:00" },
            new OrderWindowShiftDto { Days = new List<int> { 2 }, Start = "04:00", End = "12:00" })));
    }

    [Fact]
    public void Proximo_inicio_padrao_e_a_proxima_meia_noite_de_brasilia()
    {
        var agora = new DateTimeOffset(2026, 10, 20, 1, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 3, 0, 0, TimeSpan.Zero),
            DiaOperacionalRules.ProximoInicio(OrderWindowRules.Default(), agora));
    }
}
