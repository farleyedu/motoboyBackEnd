using Microsoft.AspNetCore.Mvc;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

[ApiController]
[Route("api/[controller]")]
public class EntregasController : Controller
{
    private readonly HttpClient _httpClient;

    public EntregasController(IHttpClientFactory httpClientFactory)
    {
        _httpClient = httpClientFactory.CreateClient();
    }

    [HttpPost("confirmar")]
    public async Task<IActionResult> ConfirmarEntrega([FromBody] ConfirmarEntregaRequest request)
    {
        try
        {
            // 1. Buscar orderId pelo localizador
            var response = await _httpClient.GetAsync($"https://merchant-api.ifood.com.br/marketplace-delivery-handshake/order-available/localizers/{request.Localizador}");
            if (!response.IsSuccessStatusCode)
                return BadRequest("Localizador inválido ou não encontrado.");

            var json = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<IfoodLocalizadorResponse>(json);

            // 2. Confirmar com o handshakeCode
            var payload = new
            {
                orderId = result.orderId,
                handshakeCode = request.CodigoCliente
            };

            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            var confirm = await _httpClient.PostAsync("https://merchant-api.ifood.com.br/marketplace-delivery-handshake/confirm", content);

            if (!confirm.IsSuccessStatusCode)
                return BadRequest("Código de cliente inválido ou pedido já confirmado.");

            return Ok(new
            {
                mensagem = "Entrega confirmada com sucesso",
                cliente = result.customerName,
                pedido = result.shortId
            });
        }
        catch
        {
            return StatusCode(500, "Erro interno ao confirmar entrega.");
        }
    }
}
