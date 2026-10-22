using APIBack.Attributes;
using APIBack.Controllers;
using APIBack.DTOs.Atendimento;
using APIBack.DTOs.Configuracoes;
using APIBack.Repository.Interface;
using APIBack.Service;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace APIBack.Tests.Unit;

public sealed class StoreOperationTests
{
    [Fact]
    public void DailyExceptionTakesPrecedenceButDoesNotLeakIntoNextWeek()
    {
        var schedule = new HorarioAtendimentoDto
        {
            Dias = [new() { Dia = 5, Abre = "18:00", Fecha = "22:00" }],
            Especiais = [new() { Data = new(2026, 10, 9), Abre = "12:00", Fecha = "14:00" }]
        };
        Assert.True(AtendimentoConfigRules.IsOpen(schedule, new(2026, 10, 9, 12, 0, 0)));
        Assert.False(AtendimentoConfigRules.IsOpen(schedule, new(2026, 10, 9, 14, 0, 0)));
        Assert.False(AtendimentoConfigRules.IsOpen(schedule, new(2026, 10, 9, 19, 0, 0)));
        Assert.Equal("sexta às 18:00", AtendimentoConfigRules.ProximaAbertura(schedule, new(2026, 10, 9, 14, 0, 0)));
        Assert.True(AtendimentoConfigRules.IsOpen(schedule, new(2026, 10, 16, 19, 0, 0)));
    }

    [Fact]
    public void SpecialDayAlsoWorksForStoreWithoutWeeklySchedule()
    {
        // Sem grade semanal so abre no dia da excecao; nos demais dias a loja esta fechada (nao ha mais "sem horario = aberta").
        var schedule = new HorarioAtendimentoDto
        {
            SemHorarioSemanal = true,
            Especiais = [new() { Data = new(2026, 10, 9), Abre = "12:00", Fecha = "14:00" }]
        };
        Assert.True(AtendimentoConfigRules.IsOpen(schedule, new(2026, 10, 9, 12, 0, 0)));
        Assert.False(AtendimentoConfigRules.IsOpen(schedule, new(2026, 10, 9, 15, 0, 0)));
        Assert.False(AtendimentoConfigRules.IsOpen(schedule, new(2026, 10, 10, 12, 0, 0)));
        Assert.Null(AtendimentoConfigRules.ProximaAbertura(schedule, new(2026, 10, 9, 15, 0, 0)));
    }

    [Fact]
    public async Task ControllerRequiresSelectedStoreAndUsesOnlyAuthenticatedStoreAndActor()
    {
        var operacao = new Mock<IOperacaoLojaService>();
        var context = new DefaultHttpContext();
        var controller = new EstablishmentOperationController(operacao.Object) { ControllerContext = new() { HttpContext = context } };
        Assert.IsType<UnauthorizedObjectResult>(await controller.Status(default));
        context.Items["UserId"] = 7;
        Assert.IsType<BadRequestObjectResult>(await controller.Status(default));
        var store = Guid.NewGuid(); context.Items["EstabelecimentoId"] = store;
        var request = new OpenStoreTodayRequest { EstabelecimentoId = store, DataLocal = "2026-10-09", FechaAs = "23:00" };
        operacao.Setup(r => r.AbrirHojeAsync(store, 7, request, default)).ReturnsAsync(new StoreOperationDto { EstabelecimentoId = store, AbertoAgora = true });
        Assert.IsType<OkObjectResult>(await controller.OpenToday(request, default));
        operacao.Verify(r => r.AbrirHojeAsync(store, 7, request, default), Times.Once);
        operacao.Setup(r => r.FecharAsync(store, 7, default)).ReturnsAsync(new StoreOperationDto { EstabelecimentoId = store });
        Assert.IsType<OkObjectResult>(await controller.CloseNow(default));
        operacao.Verify(r => r.FecharAsync(store, 7, default), Times.Once);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
        var read = typeof(EstablishmentOperationController).GetMethod(nameof(EstablishmentOperationController.Status))!.GetCustomAttributes(typeof(RequirePermissionAttribute), true).Cast<RequirePermissionAttribute>().Single();
        var write = typeof(EstablishmentOperationController).GetMethod(nameof(EstablishmentOperationController.OpenToday))!.GetCustomAttributes(typeof(RequirePermissionAttribute), true).Cast<RequirePermissionAttribute>().Single();
        var close = typeof(EstablishmentOperationController).GetMethod(nameof(EstablishmentOperationController.CloseNow))!.GetCustomAttributes(typeof(RequirePermissionAttribute), true).Cast<RequirePermissionAttribute>().Single();
        Assert.Equal(("Configuracoes", "visualizar"), (read.Modulo, read.Acao));
        Assert.Equal(("Configuracoes", "editar"), (write.Modulo, write.Acao));
        Assert.Equal(("Configuracoes", "editar"), (close.Modulo, close.Acao));
    }
}
