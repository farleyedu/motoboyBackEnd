using APIBack.Model;
using APIBack.Repository.Interface;
using Dapper;
using System.Data.Common;
using System.Data.SqlClient;

namespace APIBack.Repository
{
    public class MotoboyRepository : IMotoboyRepository
    {
        private readonly string _connectionString;

        public MotoboyRepository(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("TemplateDB");
        }

        public IEnumerable<Motoboy> GetMotoboy()
        {
            using (var connection = new SqlConnection(_connectionString))
            {
                return connection.Query<Motoboy>("SELECT * FROM Motoboy").ToList();
            }
        }

        public IEnumerable<Motoboy> ConvidarMotoboy()
        {
            using (var connection = new SqlConnection(_connectionString))
            {
                return connection.Query<Motoboy>("SELECT * FROM Motoboy").ToList();
            }
        }
        public async Task<Motoboy> ObterPorIdAsync(int id)
        {
            using (var connection = new SqlConnection(_connectionString))
            {
                var sql = "SELECT Id, Nome, Avatar FROM Motoboy WHERE Id = @Id";
                return await connection.QueryFirstOrDefaultAsync<Motoboy>(sql, new { Id = id });
            }
        }
        public IEnumerable<Motoboy> ListarOnline()
        {
            using (var connection = new SqlConnection(_connectionString))
            {
                var sql = "SELECT * FROM Motoboy WHERE Status = 1";
                return connection.Query<Motoboy>(sql).ToList();
            }
        }

        public async Task AtualizarAvatarAsync(int id, string caminhoAvatar)
        {
            using (var connection = new SqlConnection(_connectionString))
            {
                var sql = "UPDATE Motoboy SET Avatar = @Avatar WHERE Id = @Id";
                await connection.ExecuteAsync(sql, new { Avatar = caminhoAvatar, Id = id });
            }
        }
    }
}
