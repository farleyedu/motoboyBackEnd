using APIBack.Model;
using APIBack.Service;
using APIBack.Service.Interface;
using Microsoft.AspNetCore.Mvc;
using System.Collections.Generic;

namespace APIBack.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class UsuarioController : Controller
    {
        private readonly IUsuarioService _usuarioService;

        public UsuarioController(IUsuarioService usuarioService)
        {
            _usuarioService = usuarioService;
        }

        // GET: api/usuarios
        [HttpGet]
        public ActionResult<IEnumerable<Usuario>> GetUsuarios()
        {
            var usuarios = _usuarioService.GetUsuarios();
            return Ok(usuarios);
        }

        // GET: api/usuarios/1
        [HttpGet("{id}")]
        public ActionResult<Usuario> GetUsuario(int id)
        {
            var usuario = _usuarioService.GetUsuario(id);
            if (usuario == null)
            {
                return NotFound();
            }
            return Ok(usuario);
        }




        // POST: api/usuarios
        [HttpPost]
        public ActionResult<Usuario> PostUsuario(Usuario usuario)
        {
            _usuarioService.AddUsuario(usuario);
            return CreatedAtAction(nameof(GetUsuario), new { id = usuario.Id }, usuario);
        }

        // PUT: api/usuarios/1
        [HttpPut("{id}")]
        public IActionResult PutUsuario(int id, Usuario usuario)
        {
            var usuarioExistente = _usuarioService.GetUsuario(id);
            if (usuarioExistente == null)
            {
                return NotFound();
            }

            _usuarioService.UpdateUsuario(id, usuario);
            return NoContent();
        }

        // DELETE: api/usuarios/1
        [HttpDelete("{id}")]
        public IActionResult DeleteUsuario(int id)
        {
            var usuario = _usuarioService.GetUsuario(id);
            if (usuario == null)
            {
                return NotFound();
            }

            _usuarioService.DeleteUsuario(id);
            return NoContent();
        }
    }
}
