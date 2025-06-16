using Microsoft.AspNetCore.Mvc;
using APIBack.Service;
using APIBack.Service.Interface;

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
        public async Task<IActionResult> Get([FromQuery] string endereco)
        {
            if (string.IsNullOrWhiteSpace(endereco))
                return BadRequest("Endereço é obrigatório.");

            var coordenadas = await _service.ObterCoordenadasAsync(endereco);

            if (coordenadas == null)
                return NotFound("Coordenadas não encontradas.");

            return Ok(new
            {
                latitude = coordenadas.Value.Latitude,
                longitude = coordenadas.Value.Longitude
            });
        }
    }
}
