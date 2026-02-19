using System.Threading.Tasks;
using APIBack.Attributes;
using APIBack.Service.Interface;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class LocalizacaoController : Controller
    {
        private readonly ILocalizacaoService _service;

        public LocalizacaoController(ILocalizacaoService service)
        {
            _service = service;
        }

        [HttpGet]
        [RequirePermission("Delivery", "visualizar")]
        public async Task<IActionResult> Get([FromQuery] string endereco)
        {
            if (string.IsNullOrWhiteSpace(endereco))
            {
                return BadRequest("Endereco e obrigatorio.");
            }

            var coordenadas = await _service.ObterCoordenadasAsync(endereco);

            if (coordenadas == null)
            {
                return NotFound("Coordenadas nao encontradas.");
            }

            return Ok(new
            {
                latitude = coordenadas.Value.Latitude,
                longitude = coordenadas.Value.Longitude
            });
        }
    }
}
