using APIBack.DTOs;
using APIBack.Model;
using APIBack.Service;
using APIBack.Service.Interface;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class MotoboyController : Controller
    {
        private readonly IMotoboyService _motoboyService;

        public MotoboyController(IMotoboyService motoboyService)
        {
            _motoboyService = motoboyService;
        }

        // GET: api/motoboys
        [HttpGet]
        public ActionResult<IEnumerable<Motoboy>> GetMotoboys()
        {
            var motoboys = _motoboyService.GetMotoboy();
            return Ok(motoboys);
        }
        // GET: api/motoboys/1
        [HttpGet("com-pedidos")]
        public ActionResult<MotoboyComPedidosDTO> GetMotoboysOnline()
        {
            var motoboy = _motoboyService.GetMotoboysOnline();
            if (motoboy == null)
                return NotFound();

            return Ok(motoboy);
        }

        // GET: api/motoboys/convidar
        [HttpGet("convidar")]
        public ActionResult<IEnumerable<Motoboy>> ConvidarMotoboys()
        {
            var motoboys = _motoboyService.ConvidarMotoboy();
            return Ok(motoboys);
        }

        [HttpPost("{id}/upload-avatar")]
        public async Task<IActionResult> UploadAvatar(int id, IFormFile avatar)
        {
            if (avatar == null || avatar.Length == 0)
                return BadRequest("Imagem inválida");

            var resultado = await _motoboyService.UploadAvatarAsync(id, avatar);

            if (!resultado.Sucesso)
                return BadRequest(resultado.Mensagem);

            return Ok(new { avatar = resultado.CaminhoAvatar });
        }
    }
}
