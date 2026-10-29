using APIBack.GestaoLogica.Compras;
using Xunit;

namespace APIBack.Tests.Unit;

/// <summary>Destino da conferência (Integração 6B): "não controlar" não reaparece como consumo ao recarregar.</summary>
public class GestaoComprasConferenciaTests
{
    [Theory]
    [InlineData("{\"destino\":\"estoque\"}", StatusLinha.Conferido)]
    [InlineData("{\"destino\":\"consumo\",\"consumoDireto\":true}", StatusLinha.ConsumoDireto)]
    [InlineData("{\"destino\":\"nao_controlar\",\"consumoDireto\":false}", StatusLinha.NaoControlar)]
    [InlineData("{\"consumoDireto\":true}", StatusLinha.ConsumoDireto)]
    [InlineData("{\"consumoDireto\":false}", StatusLinha.Conferido)]
    [InlineData("{}", StatusLinha.Conferido)]
    public void Status_da_linha_sai_do_destino_gravado(string dados, string esperado) =>
        Assert.Equal(esperado, ComprasService.StatusDaConferencia(dados));
}
