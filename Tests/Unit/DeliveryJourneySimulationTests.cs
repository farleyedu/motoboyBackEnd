using APIBack.DTOs.Delivery;
using APIBack.Service;
using Xunit;

namespace APIBack.Tests.Unit;

public sealed class DeliveryJourneySimulationTests
{
    private static RestaurantSettingsDto Loja() => new() { Latitude = -18.9, Longitude = -48.2, TaxaEntregaFixa = 4, TempoPreparoMin = 10 };

    [Fact]
    public void SimulaRascunhoSemAlterarConfiguracaoOriginal()
    {
        var loja = Loja();
        var request = new SimularSlaRequest { Latitude = -18.9, Longitude = -48.2,
            Rascunho = new() { Restaurante = new() { Latitude = -18.9, Longitude = -48.2, TaxaEntregaFixa = 9, TempoPreparoMin = 35 } } };
        var resultado = DeliverySlaSimulator.Simular(loja, Array.Empty<DeliveryZonaDto>(), request, true);
        Assert.Equal(9, resultado.TaxaEntrega);
        Assert.Equal(35, resultado.PrevisaoMinMinutos);
        Assert.Equal(4, loja.TaxaEntregaFixa);
        Assert.Equal(10, loja.TempoPreparoMin);
    }

    [Fact]
    public void ZonasDoRascunhoSubstituemSalvasEInativasNaoAfetamTaxa()
    {
        var request = new SimularSlaRequest { Latitude = -18.9, Longitude = -48.2,
            Rascunho = new() { Restaurante = new() { Latitude = -18.9, Longitude = -48.2, TaxaEntregaFixa = 7 },
                Zonas = new() { new() { Nome = "Inativa", RaioAteKm = 1, Taxa = 1, Ativo = false }, new() { Nome = "Rascunho", RaioAteKm = 3, Taxa = 6 } } } };
        var resultado = DeliverySlaSimulator.Simular(Loja(), new[] { new DeliveryZonaDto { Nome = "Salva", RaioAteKm = 1, Taxa = 2 } }, request, true);
        Assert.Equal("Rascunho", resultado.ZonaNome); Assert.Equal(6, resultado.TaxaEntrega);
        request.Rascunho.Zonas.Clear();
        Assert.Equal(7, DeliverySlaSimulator.Simular(Loja(), Array.Empty<DeliveryZonaDto>(), request, true).TaxaEntrega);
    }

    [Fact]
    public void SemRascunhoMantemContratoExistenteEPausaImpedeAbertura()
    {
        var loja = Loja(); var request = new SimularSlaRequest { Latitude = -18.9, Longitude = -48.2 };
        Assert.Equal(4, DeliverySlaSimulator.Simular(loja, Array.Empty<DeliveryZonaDto>(), request, true).TaxaEntrega);
        loja.AceitaPedidos = false;
        Assert.False(DeliverySlaSimulator.Simular(loja, Array.Empty<DeliveryZonaDto>(), request, true).EstabelecimentoAbertoAgora);
    }

    [Theory]
    [InlineData(91, 0)] [InlineData(0, 181)] [InlineData(0, 0)] [InlineData(double.NaN, 1)]
    public void CoordenadasInvalidasSaoRecusadas(double lat, double lng) => Assert.Throws<DeliveryDomainException>(() =>
        DeliverySlaSimulator.Simular(Loja(), Array.Empty<DeliveryZonaDto>(), new() { Latitude = lat, Longitude = lng }, true));

    [Fact]
    public void AplicaMesmasValidacoesDeRegrasEZonasDoCadastro()
    {
        var request = new SimularSlaRequest { Latitude = -18.9, Longitude = -48.2, Rascunho = new() { Restaurante = new() { TempoPreparoMin = -1 } } };
        Assert.Throws<DeliveryDomainException>(() => DeliverySlaSimulator.Simular(Loja(), Array.Empty<DeliveryZonaDto>(), request, true));
        request.Rascunho.Restaurante.TempoPreparoMin = 20;
        request.Rascunho.Zonas.Add(new() { Nome = "Sem raio", Taxa = 3, RaioAteKm = 0 });
        Assert.Throws<DeliveryDomainException>(() => DeliverySlaSimulator.Simular(Loja(), Array.Empty<DeliveryZonaDto>(), request, true));
    }
}
