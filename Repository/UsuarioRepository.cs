using APIBack.Model;
using Dapper;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;

namespace APIBack.Repository
{
    public class UsuarioRepository : IUsuarioRepository
    {
        private readonly string _connectionString;

        public UsuarioRepository(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("TemplateDB");
        }

        public IEnumerable<Usuario> GetUsuarios()
        {
            using (var connection = new SqlConnection(_connectionString))
            {
                return connection.Query<Usuario>("SELECT * FROM Usuario").ToList();
            }
        }

        public Usuario GetUsuario(int id)
        {
            using (var connection = new SqlConnection(_connectionString))
            {
                return connection.QueryFirstOrDefault<Usuario>("SELECT * FROM Usuario WHERE Id = @Id", new { Id = id });
            }
        }

        public void AddUsuario(Usuario usuario)
        {
            using (var connection = new SqlConnection(_connectionString))
            {
                var sql = "INSERT INTO Usuario (Nome, Email, Senha) VALUES (@Nome, @Email, @Senha); SELECT CAST(SCOPE_IDENTITY() as int)";
                usuario.Id = connection.Query<int>(sql, usuario).Single();
            }
        }

        public void UpdateUsuario(Usuario usuario)
        {
            using (var connection = new SqlConnection(_connectionString))
            {
                var sql = "UPDATE Usuario SET Nome = @Nome, Email = @Email WHERE Id = @Id";
                connection.Execute(sql, usuario);
            }
        }

        public void DeleteUsuario(int id)
        {
            using (var connection = new SqlConnection(_connectionString))
            {
                var sql = "DELETE FROM Usuario WHERE Id = @Id";
                connection.Execute(sql, new { Id = id });
            }
        }
    }
}
