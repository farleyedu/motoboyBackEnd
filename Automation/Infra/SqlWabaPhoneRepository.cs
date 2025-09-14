// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Threading.Tasks;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Models;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace APIBack.Automation.Infra
{
    /// <summary>
    /// Implementacao SQL do repositorio para mapeamento de WhatsApp Business API phone numbers
    /// </summary>
    public class SqlWabaPhoneRepository : IWabaPhoneRepository
    {
        private readonly string _connectionString;
        private readonly ILogger<SqlWabaPhoneRepository>? _logger;

        // Mantem o construtor existente para compatibilidade com testes
        public SqlWabaPhoneRepository(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection")
                ?? configuration["ConnectionStrings:DefaultConnection"]
                ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
        }

        // Construtor opcional com ILogger para logs estruturados
        public SqlWabaPhoneRepository(IConfiguration configuration, ILogger<SqlWabaPhoneRepository> logger) : this(configuration)
        {
            _logger = logger;
        }

        public async Task<Guid?> ObterIdEstabelecimentoPorPhoneNumberIdAsync(string phoneNumberId)
        {
            if (string.IsNullOrWhiteSpace(phoneNumberId))
                return null;

            const string sql = @"SELECT id_estabelecimento
                                  FROM waba_phone
                                  WHERE phone_number_id = @phoneNumberId
                                    AND ativo = TRUE
                                  LIMIT 1;";

            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                var result = await connection.ExecuteScalarAsync<Guid?>(sql, new { phoneNumberId });
                return result;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Erro ao buscar estabelecimento por phone_number_id {PhoneNumberId}", phoneNumberId);
                return null;
            }
        }

        public async Task<WabaPhone?> ObterPorPhoneNumberIdAsync(string phoneNumberId)
        {
            if (string.IsNullOrWhiteSpace(phoneNumberId))
                return null;

            const string sql = @"SELECT
                                     id                 AS ""Id"",
                                     phone_number_id    AS ""PhoneNumberId"",
                                     id_estabelecimento AS ""IdEstabelecimento"",
                                     ativo              AS ""Ativo"",
                                     descricao          AS ""Descricao"",
                                     data_criacao       AS ""DataCriacao"",
                                     data_atualizacao   AS ""DataAtualizacao""
                                   FROM waba_phone
                                   WHERE phone_number_id = @phoneNumberId
                                   LIMIT 1;";

            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                var entidade = await connection.QueryFirstOrDefaultAsync<WabaPhone>(sql, new { phoneNumberId });
                return entidade;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Erro ao buscar WabaPhone por phone_number_id {PhoneNumberId}", phoneNumberId);
                return null;
            }
        }

        public async Task<bool> InserirOuAtualizarAsync(WabaPhone wabaPhone)
        {
            if (wabaPhone == null || string.IsNullOrWhiteSpace(wabaPhone.PhoneNumberId))
                return false;

            var id = wabaPhone.Id == Guid.Empty ? Guid.NewGuid() : wabaPhone.Id;
            var criado = wabaPhone.DataCriacao != default ? DateTime.SpecifyKind(wabaPhone.DataCriacao, DateTimeKind.Utc) : DateTime.UtcNow;
            var atualizado = DateTime.UtcNow;

            const string sql = @"INSERT INTO waba_phone (id, phone_number_id, id_estabelecimento, ativo, descricao, data_criacao, data_atualizacao)
                                 VALUES (@Id, @PhoneNumberId, @IdEstabelecimento, @Ativo, @Descricao, @DataCriacao, @DataAtualizacao)
                                 ON CONFLICT (phone_number_id)
                                 DO UPDATE SET
                                   id_estabelecimento = EXCLUDED.id_estabelecimento,
                                   ativo              = EXCLUDED.ativo,
                                   descricao          = EXCLUDED.descricao,
                                   data_atualizacao   = EXCLUDED.data_atualizacao;";

            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                var rows = await connection.ExecuteAsync(sql, new
                {
                    Id = id,
                    PhoneNumberId = wabaPhone.PhoneNumberId,
                    IdEstabelecimento = wabaPhone.IdEstabelecimento,
                    Ativo = wabaPhone.Ativo,
                    Descricao = (object?)wabaPhone.Descricao,
                    DataCriacao = criado,
                    DataAtualizacao = atualizado
                });
                return rows > 0;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Erro ao inserir/atualizar WabaPhone {PhoneNumberId}", wabaPhone.PhoneNumberId);
                return false;
            }
        }

        public async Task<bool> ExisteAtivoAsync(string phoneNumberId)
        {
            if (string.IsNullOrWhiteSpace(phoneNumberId))
                return false;

            const string sql = @"SELECT 1
                                  FROM waba_phone
                                  WHERE phone_number_id = @phoneNumberId
                                    AND ativo = TRUE
                                  LIMIT 1;";

            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                var existe = await connection.ExecuteScalarAsync<int?>(sql, new { phoneNumberId });
                return existe.HasValue;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Erro ao verificar se WabaPhone esta ativo {PhoneNumberId}", phoneNumberId);
                return false;
            }
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================

