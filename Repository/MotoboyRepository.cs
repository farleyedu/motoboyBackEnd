using APIBack.Model;
using APIBack.Repository.Interface;
using Dapper;
using Npgsql;
using System.Data.Common;

namespace APIBack.Repository
{
    public class MotoboyRepository : IMotoboyRepository
    {
        private readonly string _connectionString;

        public MotoboyRepository(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection");
        }

        public IEnumerable<Motoboy> GetMotoboy()
        {
            using var connection = new NpgsqlConnection(_connectionString);
            {
                return connection.Query<Motoboy>("SELECT * FROM motoboy").ToList();
            }
        }

        public IEnumerable<Motoboy> ConvidarMotoboy()
        {
            using var connection = new NpgsqlConnection(_connectionString);
            {
                return connection.Query<Motoboy>("SELECT * FROM motoboy").ToList();
            }
        }
        public async Task<Motoboy> ObterPorIdAsync(int id)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            {
                var sql = "SELECT id, nome, avatar FROM motoboy WHERE id = @Id";
                return await connection.QueryFirstOrDefaultAsync<Motoboy>(sql, new { Id = id });
            }
        }
        public IEnumerable<Motoboy> ListarOnline()
        {
            using var connection = new NpgsqlConnection(_connectionString);
            {
                var sql = "SELECT * FROM motoboy WHERE status = 1";
                return connection.Query<Motoboy>(sql).ToList();
            }
        }

        public async Task AtualizarAvatarAsync(int id, string caminhoAvatar)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            {
                var sql = "UPDATE motoboy SET avatar = @avatar WHERE id = @Id";
                await connection.ExecuteAsync(sql, new { Avatar = caminhoAvatar, Id = id });
            }
        }
    }
}
