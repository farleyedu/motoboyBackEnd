using APIBack.Model;
using Dapper;
using Npgsql;
using System.Collections.Generic;
using System.Linq;

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
            {
                return connection.Query<Usuario>("SELECT * FROM usuario").ToList();
            }
        }

        public Usuario GetUsuario(int id)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            {
                return connection.QueryFirstOrDefault<Usuario>("SELECT * FROM usuario WHERE id = @id", new { Id = id });
            }
        }

        public void AddUsuario(Usuario usuario)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            {
                var sql = "INSERT INTO usuario (nome, email, senha) VALUES (@Nome, @Email, @Senha); SELECT CAST(SCOPE_IDENTITY() as int)";
                usuario.Id = connection.Query<int>(sql, usuario).Single();
            }
        }

        public void UpdateUsuario(Usuario usuario)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            {
                var sql = "UPDATE usuario SET nome = @Nome, Email = @Email WHERE Id = @Id";
                connection.Execute(sql, usuario);
            }
        }

        public void DeleteUsuario(int id)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            {
                var sql = "DELETE FROM usuario WHERE id = @Id";
                connection.Execute(sql, new { Id = id });
            }
        }
    }
}
