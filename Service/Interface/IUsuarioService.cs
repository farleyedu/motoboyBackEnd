using APIBack.Model;
using System.Collections.Generic;

namespace APIBack.Service.Interface
{
    public interface IUsuarioService
    {
        IEnumerable<Usuario> GetUsuarios();
        Usuario GetUsuario(int id);
        void AddUsuario(Usuario usuario);
        void UpdateUsuario(int id, Usuario usuario);
        void DeleteUsuario(int id);
    }
}
