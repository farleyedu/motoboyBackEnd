// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Threading.Tasks;
using APIBack.Automation.Interfaces;
using Dapper;
using Npgsql;

namespace APIBack.Automation.Infra
{
    public class SqlClienteRepository : IClienteRepository
    {
        private readonly string _connectionString;
        public SqlClienteRepository(Microsoft.Extensions.Configuration.IConfiguration config)
        {
            _connectionString = config.GetConnectionString("DefaultConnection")
                                 ?? config["ConnectionStrings:DefaultConnection"]
                                 ?? throw new InvalidOperationException("Connection string 'DefaultConnection' não encontrada.");
        }

        public async Task<Guid> GarantirClienteAsync(string telefoneE164, Guid idEstabelecimento)
        {
            if (string.IsNullOrWhiteSpace(telefoneE164))
                throw new ArgumentException("telefoneE164 obrigatório", nameof(telefoneE164));
            if (idEstabelecimento == Guid.Empty)
                throw new ArgumentException("idEstabelecimento obrigatório", nameof(idEstabelecimento));

            const string sqlSel = @"SELECT id FROM clientes
                                     WHERE id_estabelecimento = @IdEstabelecimento
                                       AND telefone_e164      = @Telefone
                                     LIMIT 1;";

            await using var cx = new NpgsqlConnection(_connectionString);
            var existente = await cx.ExecuteScalarAsync<Guid?>(sqlSel, new { IdEstabelecimento = idEstabelecimento, Telefone = telefoneE164 });
            if (existente.HasValue && existente.Value != Guid.Empty)
                return existente.Value;

            var novoId = Guid.NewGuid();
            var agora = DateTime.UtcNow;

            const string sqlIns = @"INSERT INTO clientes (id, id_estabelecimento, telefone_e164, data_criacao, data_atualizacao)
                                     VALUES (@Id, @IdEstabelecimento, @Telefone, @CriadoEm, @AtualizadoEm);";

            await cx.ExecuteAsync(sqlIns, new
            {
                Id = novoId,
                IdEstabelecimento = idEstabelecimento,
                Telefone = telefoneE164,
                CriadoEm = agora,
                AtualizadoEm = agora
            });

            return novoId;
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================

