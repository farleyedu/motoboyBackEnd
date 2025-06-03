using APIBack.Model;
using APIBack.Repository;
using APIBack.Service.Interface;
using System.Collections.Generic;

namespace APIBack.Service
{
    public class UsuarioService : IUsuarioService
    {
        private readonly IUsuarioRepository _usuarioRepository;

        public UsuarioService(IUsuarioRepository usuarioRepository)
        {
            _usuarioRepository = usuarioRepository;
        }

        public IEnumerable<Usuario> GetUsuarios()
        {
            return _usuarioRepository.GetUsuarios();
        }

        public Usuario GetUsuario(int id)
        {
            return _usuarioRepository.GetUsuario(id);
        }

        public void AddUsuario(Usuario usuario)
        {
            _usuarioRepository.AddUsuario(usuario);
        }

        public void UpdateUsuario(int id, Usuario usuario)
        {
            usuario.Id = id;
            _usuarioRepository.UpdateUsuario(usuario);
        }

        public void DeleteUsuario(int id)
        {
            _usuarioRepository.DeleteUsuario(id);
        }
    }
}
