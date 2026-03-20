using System.Collections.Generic;
using APIBack.Attributes;
using APIBack.Model;
using APIBack.Service;
using APIBack.Service.Interface;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class UsuarioController : ApiControllerBase
    {
        private readonly IUsuarioService _usuarioService;

        public UsuarioController(IUsuarioService usuarioService)
        {
            _usuarioService = usuarioService;
        }

        // GET: api/usuarios
        [HttpGet]
        [RequirePermission("Configuracoes", "visualizar")]
        public ActionResult<IEnumerable<Usuario>> GetUsuarios()
        {
            var usuarios = _usuarioService.GetUsuarios();
            return Ok(usuarios);
        }

        // GET: api/usuarios/1
        [HttpGet("{id}")]
        [RequirePermission("Configuracoes", "visualizar")]
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
        [RequirePermission("Configuracoes", "criar")]
        public ActionResult<Usuario> PostUsuario(Usuario usuario)
        {
            try
            {
                _usuarioService.AddUsuario(usuario);
                return CreatedAtAction(nameof(GetUsuario), new { id = usuario.Id }, usuario);
            }
            catch (RequestValidationException ex)
            {
                return ValidationErrorResponse(ex);
            }
        }

        // PUT: api/usuarios/1
        [HttpPut("{id}")]
        [RequirePermission("Configuracoes", "editar")]
        public IActionResult PutUsuario(int id, Usuario usuario)
        {
            var usuarioExistente = _usuarioService.GetUsuario(id);
            if (usuarioExistente == null)
            {
                return NotFoundErrorResponse("Usuario nao encontrado.");
            }

            try
            {
                _usuarioService.UpdateUsuario(id, usuario);
                return NoContent();
            }
            catch (RequestValidationException ex)
            {
                return ValidationErrorResponse(ex);
            }
        }

        // DELETE: api/usuarios/1
        [HttpDelete("{id}")]
        [RequirePermission("Configuracoes", "deletar")]
        public IActionResult DeleteUsuario(int id)
        {
            var usuario = _usuarioService.GetUsuario(id);
            if (usuario == null)
            {
                return NotFoundErrorResponse("Usuario nao encontrado.");
            }

            _usuarioService.DeleteUsuario(id);
            return NoContent();
        }
    }
}
