using APIBack.Attributes;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/[controller]")]
public class EntregasController : Controller
{
    public EntregasController(IHttpClientFactory httpClientFactory) { }

    [HttpPost("confirmar")]
    [RequirePermission("Delivery", "integracao_ifood")]
    public Task<IActionResult> ConfirmarEntrega([FromBody] ConfirmarEntregaRequest request) =>
        Task.FromResult<IActionResult>(Conflict(new
        {
            code = "IFOOD_LEGACY_HANDSHAKE_DISABLED",
            mensagem = "Use a operação persistente de confirmação oficial vinculada ao pedido e à sessão."
        }));
}