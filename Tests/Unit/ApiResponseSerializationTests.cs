using System.Text.Json;
using APIBack.DTOs.Common;
using Xunit;

namespace APIBack.Tests.Unit;

public class ApiResponseSerializationTests
{
    // ApiResponse<bool> nao serializa (WhenWritingNull em tipo-valor): os controllers de confirmacao devolvem object.
    [Fact]
    public void ApiResponse_de_object_com_true_serializa_data_true()
    {
        var json = JsonSerializer.Serialize(ApiResponse<object>.Ok(true));

        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("data").GetBoolean());
    }

    [Fact]
    public void ApiResponse_de_tipo_valor_continua_inválido_e_deve_ser_evitado()
    {
        Assert.Throws<System.InvalidOperationException>(() => JsonSerializer.Serialize(ApiResponse<bool>.Ok(true)));
    }
}
