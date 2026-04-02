// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Linq;
using System.Threading.Tasks;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Models;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace APIBack.Automation.Infra
{
    public class SqlWabaPhoneRepository : IWabaPhoneRepository
    {
        private readonly string _connectionString;
        private readonly ILogger<SqlWabaPhoneRepository>? _logger;
        private static (bool PhoneNumberId, bool DisplayPhoneNumber)? _cachedColumns;

        public SqlWabaPhoneRepository(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection")
                ?? configuration["ConnectionStrings:DefaultConnection"]
                ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
        }

        public SqlWabaPhoneRepository(IConfiguration configuration, ILogger<SqlWabaPhoneRepository> logger) : this(configuration)
        {
            _logger = logger;
        }

        public async Task<Guid?> ObterIdEstabelecimentoPorPhoneNumberIdAsync(string phoneNumberId)
        {
            if (string.IsNullOrWhiteSpace(phoneNumberId))
                return null;

            var digitsOnly = new string(phoneNumberId.Where(char.IsDigit).ToArray());

            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                var columns = await ObterColunasAsync(connection);
                var comparisons = new System.Collections.Generic.List<string>();
                if (columns.PhoneNumberId)
                {
                    comparisons.Add("phone_number_id = @Raw");
                    comparisons.Add("regexp_replace(phone_number_id, '[^0-9]', '', 'g') = @Digits");
                }
                if (columns.DisplayPhoneNumber)
                {
                    comparisons.Add("display_phone_number = @Raw");
                    comparisons.Add("regexp_replace(display_phone_number, '[^0-9]', '', 'g') = @Digits");
                }

                if (comparisons.Count == 0)
                {
                    return null;
                }

                var sql = $@"SELECT id_estabelecimento
                                FROM waba_phone
                               WHERE ativo = TRUE
                                 AND ({string.Join(" OR ", comparisons)})
                               ORDER BY data_atualizacao DESC
                               LIMIT 1;";
                var result = await connection.ExecuteScalarAsync<Guid?>(sql, new { Raw = phoneNumberId, Digits = digitsOnly });
                return result;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Erro ao buscar estabelecimento por display_phone_number  {PhoneNumberId}", phoneNumberId);
                return null;
            }
        }

        public async Task<Guid?> ObterIdEstabelecimentoPorDisplayPhoneAsync(string displayPhoneNumber)
        {
            if (string.IsNullOrWhiteSpace(displayPhoneNumber))
                return null;

            try
            {
                var digitsOnly = new string(displayPhoneNumber.Where(char.IsDigit).ToArray());
                await using var connection = new NpgsqlConnection(_connectionString);
                var columns = await ObterColunasAsync(connection);
                if (!columns.DisplayPhoneNumber)
                {
                    return null;
                }

                var comparisons = new System.Collections.Generic.List<string>
                {
                    "display_phone_number = @Raw"
                };

                if (!string.IsNullOrWhiteSpace(digitsOnly))
                {
                    comparisons.Add("regexp_replace(display_phone_number, '[^0-9]', '', 'g') = @Digits");
                }

                var sql = $@"
SELECT id_estabelecimento
  FROM waba_phone
 WHERE ({string.Join(" OR ", comparisons)})
   AND ativo = true
 ORDER BY data_atualizacao DESC
 LIMIT 1;";
                var idEstabelecimento = await connection.QueryFirstOrDefaultAsync<Guid?>(
                    sql,
                    new { Raw = displayPhoneNumber, Digits = digitsOnly });
                return idEstabelecimento;
            }
            catch (Exception ex)
            {
                _logger?.LogError(
                    ex,
                    "Erro ao buscar estabelecimento por display_phone_number {DisplayPhone}",
                    displayPhoneNumber);
                return null;
            }
        }

        public async Task<bool> InserirOuAtualizarAsync(WabaPhone wabaPhone)
        {
            if (wabaPhone == null || string.IsNullOrWhiteSpace(wabaPhone.PhoneNumberId))
                return false;

            var criado = wabaPhone.DataCriacao != default ? DateTime.SpecifyKind(wabaPhone.DataCriacao, DateTimeKind.Utc) : DateTime.UtcNow;
            var atualizado = DateTime.UtcNow;

            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                var columns = await ObterColunasAsync(connection);
                if (!columns.PhoneNumberId && !columns.DisplayPhoneNumber)
                {
                    return false;
                }

                var targetColumn = columns.PhoneNumberId ? "phone_number_id" : "display_phone_number";
                var sql = $@"INSERT INTO waba_phone ({targetColumn}, id_estabelecimento, ativo, descricao, data_criacao, data_atualizacao)
                             VALUES (@PhoneNumberId, @IdEstabelecimento, @Ativo, @Descricao, @DataCriacao, @DataAtualizacao)
                             ON CONFLICT ({targetColumn})
                             DO UPDATE SET
                               id_estabelecimento = EXCLUDED.id_estabelecimento,
                               ativo              = EXCLUDED.ativo,
                               descricao          = EXCLUDED.descricao,
                               data_atualizacao   = EXCLUDED.data_atualizacao;";
                var rows = await connection.ExecuteAsync(sql, new
                {
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

            var digitsOnly = new string(phoneNumberId.Where(char.IsDigit).ToArray());

            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                var columns = await ObterColunasAsync(connection);
                var comparisons = new System.Collections.Generic.List<string>();
                if (columns.PhoneNumberId)
                {
                    comparisons.Add("phone_number_id = @Raw");
                    comparisons.Add("regexp_replace(phone_number_id, '[^0-9]', '', 'g') = @Digits");
                }
                if (columns.DisplayPhoneNumber)
                {
                    comparisons.Add("display_phone_number = @Raw");
                    comparisons.Add("regexp_replace(display_phone_number, '[^0-9]', '', 'g') = @Digits");
                }

                if (comparisons.Count == 0)
                {
                    return false;
                }

                var sql = $@"SELECT 1
                                FROM waba_phone
                               WHERE ativo = TRUE
                                 AND ({string.Join(" OR ", comparisons)})
                               LIMIT 1;";
                var existe = await connection.ExecuteScalarAsync<int?>(sql, new { Raw = phoneNumberId, Digits = digitsOnly });
                return existe.HasValue;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Erro ao verificar se WabaPhone esta ativo {PhoneNumberId}", phoneNumberId);
                return false;
            }
        }

        public async Task<string?> ObterPhoneNumberIdPorEstabelecimentoAsync(Guid idEstabelecimento)
        {
            if (idEstabelecimento == Guid.Empty)
            {
                return null;
            }

            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                var columns = await ObterColunasAsync(connection);
                if (!columns.PhoneNumberId)
                {
                    return null;
                }

                return await connection.ExecuteScalarAsync<string?>(
                    @"SELECT phone_number_id
                        FROM waba_phone
                       WHERE id_estabelecimento = @IdEstabelecimento
                         AND ativo = TRUE
                    ORDER BY data_atualizacao DESC
                       LIMIT 1;",
                    new { IdEstabelecimento = idEstabelecimento });
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Erro ao buscar phone_number_id por estabelecimento {IdEstabelecimento}", idEstabelecimento);
                return null;
            }
        }

        public async Task<string?> ObterDisplayPhonePorEstabelecimentoAsync(Guid idEstabelecimento)
        {
            if (idEstabelecimento == Guid.Empty)
            {
                return null;
            }

            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                var columns = await ObterColunasAsync(connection);
                if (!columns.DisplayPhoneNumber)
                {
                    return null;
                }

                return await connection.ExecuteScalarAsync<string?>(
                    @"SELECT display_phone_number
                        FROM waba_phone
                       WHERE id_estabelecimento = @IdEstabelecimento
                         AND ativo = TRUE
                    ORDER BY data_atualizacao DESC
                       LIMIT 1;",
                    new { IdEstabelecimento = idEstabelecimento });
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Erro ao buscar display_phone_number por estabelecimento {IdEstabelecimento}", idEstabelecimento);
                return null;
            }
        }

        private async Task<(bool PhoneNumberId, bool DisplayPhoneNumber)> ObterColunasAsync(NpgsqlConnection connection)
        {
            if (_cachedColumns.HasValue)
            {
                return _cachedColumns.Value;
            }

            var rows = await connection.QueryAsync<string>(
                @"SELECT column_name
                    FROM information_schema.columns
                   WHERE table_name = 'waba_phone';");

            var hash = rows.Select(r => r.Trim().ToLowerInvariant()).ToHashSet();
            _cachedColumns = (
                hash.Contains("phone_number_id"),
                hash.Contains("display_phone_number"));
            return _cachedColumns.Value;
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
