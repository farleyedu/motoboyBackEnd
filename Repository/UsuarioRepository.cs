using System.Collections.Generic;
using System.Linq;
using APIBack.Model;
using APIBack.Security;
using Dapper;
using Npgsql;

namespace APIBack.Repository
{
    public class UsuarioRepository : IUsuarioRepository
    {
        private readonly string _connectionString;

        public UsuarioRepository(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection");
        }

        public IEnumerable<Usuario> GetUsuarios()
        {
            using var connection = new NpgsqlConnection(_connectionString);
            return connection.Query<Usuario>("SELECT * FROM usuario").ToList();
        }

        public Usuario GetUsuario(int id)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            return connection.QueryFirstOrDefault<Usuario>("SELECT * FROM usuario WHERE id = @Id", new { Id = id });
        }

        public bool EmailExiste(string email, int? ignoreId = null)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            const string sql = @"
SELECT EXISTS(
    SELECT 1
      FROM usuario
     WHERE LOWER(email) = LOWER(@Email)
       AND deleted_at IS NULL
       AND (@IgnoreId IS NULL OR id <> @IgnoreId)
)";

            return connection.ExecuteScalar<bool>(sql, new { Email = email, IgnoreId = ignoreId });
        }

        public void AddUsuario(Usuario usuario)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            const string sql = @"
INSERT INTO usuario (nome, email, senha, is_super_admin, created_at, updated_at)
VALUES (@Nome, @Email, @Senha, FALSE, NOW(), NOW())
RETURNING id";

            if (!string.IsNullOrWhiteSpace(usuario.Senha))
            {
                usuario.Senha = PasswordSecurity.Hash(usuario.Senha);
            }

            usuario.Id = connection.Query<int>(sql, usuario).Single();
        }

        public void UpdateUsuario(Usuario usuario)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            const string sql = "UPDATE usuario SET nome = @Nome, email = @Email, updated_at = NOW() WHERE id = @Id";
            connection.Execute(sql, usuario);
        }

        public void DeleteUsuario(int id)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            const string sql = "DELETE FROM usuario WHERE id = @Id";
            connection.Execute(sql, new { Id = id });
        }
    }
}
