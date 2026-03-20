using APIBack.Model;
using System.Collections.Generic;

namespace APIBack.Repository
{
    public interface IUsuarioRepository
    {
        IEnumerable<Usuario> GetUsuarios();
        Usuario GetUsuario(int id);
        bool EmailExiste(string email, int? ignoreId = null);
        void AddUsuario(Usuario usuario);
        void UpdateUsuario(Usuario usuario);
        void DeleteUsuario(int id);
    }
}
