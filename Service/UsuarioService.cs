using APIBack.Model;
using APIBack.Repository;
using APIBack.Service.Interface;
using System.Collections.Generic;
using System.Net.Mail;

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
            ValidateUsuario(usuario, ignoreId: null, requirePassword: true);
            _usuarioRepository.AddUsuario(usuario);
        }

        public void UpdateUsuario(int id, Usuario usuario)
        {
            usuario.Id = id;
            ValidateUsuario(usuario, id, requirePassword: false);
            _usuarioRepository.UpdateUsuario(usuario);
        }

        public void DeleteUsuario(int id)
        {
            _usuarioRepository.DeleteUsuario(id);
        }

        private void ValidateUsuario(Usuario usuario, int? ignoreId, bool requirePassword)
        {
            var errors = new Dictionary<string, List<string>>(System.StringComparer.OrdinalIgnoreCase);

            if (usuario == null)
            {
                AddError(errors, "body", "Corpo da requisicao obrigatorio.");
                throw new RequestValidationException("Dados invalidos.", errors);
            }

            if (string.IsNullOrWhiteSpace(usuario.Nome))
            {
                AddError(errors, "nome", "Nome obrigatorio.");
            }

            if (string.IsNullOrWhiteSpace(usuario.Email))
            {
                AddError(errors, "email", "E-mail obrigatorio.");
            }
            else if (!IsValidEmail(usuario.Email))
            {
                AddError(errors, "email", "E-mail invalido.");
            }
            else if (_usuarioRepository.EmailExiste(usuario.Email.Trim(), ignoreId))
            {
                AddError(errors, "email", "E-mail ja cadastrado.");
            }

            if (requirePassword && string.IsNullOrWhiteSpace(usuario.Senha))
            {
                AddError(errors, "senha", "Senha obrigatoria.");
            }
            else if (!string.IsNullOrWhiteSpace(usuario.Senha) && usuario.Senha.Trim().Length < 6)
            {
                AddError(errors, "senha", "Senha deve conter ao menos 6 caracteres.");
            }

            if (errors.Count > 0)
            {
                throw new RequestValidationException("Dados invalidos.", errors);
            }
        }

        private static bool IsValidEmail(string email)
        {
            try
            {
                var parsed = new MailAddress(email.Trim());
                return string.Equals(parsed.Address, email.Trim(), System.StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static void AddError(Dictionary<string, List<string>> errors, string field, string message)
        {
            if (!errors.TryGetValue(field, out var items))
            {
                items = new List<string>();
                errors[field] = items;
            }

            if (!items.Contains(message))
            {
                items.Add(message);
            }
        }
    }
}
