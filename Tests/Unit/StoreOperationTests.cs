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
        var schedule = new HorarioAtendimentoDto { SemHorarioSemanal = true, Especiais = [new() { Data = new(2026, 10, 9), Fechado = true }] };
        Assert.False(AtendimentoConfigRules.IsOpen(schedule, new(2026, 10, 9, 12, 0, 0)));
        Assert.Equal("amanhã às 00:00", AtendimentoConfigRules.ProximaAbertura(schedule, new(2026, 10, 9, 12, 0, 0)));
        Assert.True(AtendimentoConfigRules.IsOpen(schedule, new(2026, 10, 10, 12, 0, 0)));
    }

    [Fact]
    public async Task ControllerRequiresSelectedStoreAndUsesOnlyAuthenticatedStoreAndActor()
    {
        var repository = new Mock<IHorarioOperacaoRepository>();
        var context = new DefaultHttpContext();
        var controller = new EstablishmentOperationController(repository.Object) { ControllerContext = new() { HttpContext = context } };
        Assert.IsType<UnauthorizedObjectResult>(await controller.Status(default));
        context.Items["UserId"] = 7;
        Assert.IsType<BadRequestObjectResult>(await controller.Status(default));
        var store = Guid.NewGuid(); context.Items["EstabelecimentoId"] = store;
        var request = new OpenStoreTodayRequest { EstabelecimentoId = store, DataLocal = "2026-10-09", FechaAs = "23:00" };
        repository.Setup(r => r.AbrirHojeAsync(store, 7, request, default)).ReturnsAsync(new StoreOperationDto { EstabelecimentoId = store, AbertoAgora = true });
        Assert.IsType<OkObjectResult>(await controller.OpenToday(request, default));
        repository.Verify(r => r.AbrirHojeAsync(store, 7, request, default), Times.Once);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
        var read = typeof(EstablishmentOperationController).GetMethod(nameof(EstablishmentOperationController.Status))!.GetCustomAttributes(typeof(RequirePermissionAttribute), true).Cast<RequirePermissionAttribute>().Single();
        var write = typeof(EstablishmentOperationController).GetMethod(nameof(EstablishmentOperationController.OpenToday))!.GetCustomAttributes(typeof(RequirePermissionAttribute), true).Cast<RequirePermissionAttribute>().Single();
        Assert.Equal(("Configuracoes", "visualizar"), (read.Modulo, read.Acao));
        Assert.Equal(("Configuracoes", "editar"), (write.Modulo, write.Acao));
    }
}
