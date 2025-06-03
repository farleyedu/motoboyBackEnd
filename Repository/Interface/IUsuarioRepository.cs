using APIBack.Model;
using System.Collections.Generic;

namespace APIBack.Repository
{
    public interface IUsuarioRepository
    {
        IEnumerable<Usuario> GetUsuarios();
        Usuario GetUsuario(int id);
        void AddUsuario(Usuario usuario);
        void UpdateUsuario(Usuario usuario);
        void DeleteUsuario(int id);
    }
}
