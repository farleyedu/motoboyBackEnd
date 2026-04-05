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
        private static (bool PhoneNumberId, bool DisplayPhoneNumber, bool AccessToken)? _cachedColumns;

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
            if (wabaPhone == null)
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

                var targetColumn =
                    columns.PhoneNumberId && !string.IsNullOrWhiteSpace(wabaPhone.PhoneNumberId) ? "phone_number_id" :
                    columns.DisplayPhoneNumber && !string.IsNullOrWhiteSpace(wabaPhone.DisplayPhoneNumber) ? "display_phone_number" :
                    null;

                if (string.IsNullOrWhiteSpace(targetColumn))
                {
                    return false;
                }

                var insertColumns = new System.Collections.Generic.List<string>();
                var insertValues = new System.Collections.Generic.List<string>();
                var updateSet = new System.Collections.Generic.List<string>();

                if (columns.PhoneNumberId && !string.IsNullOrWhiteSpace(wabaPhone.PhoneNumberId))
                {
                    insertColumns.Add("phone_number_id");
                    insertValues.Add("@PhoneNumberId");
                    if (!string.Equals(targetColumn, "phone_number_id", StringComparison.Ordinal))
                    {
                        updateSet.Add("phone_number_id = COALESCE(EXCLUDED.phone_number_id, waba_phone.phone_number_id)");
                    }
                }

                if (columns.DisplayPhoneNumber && !string.IsNullOrWhiteSpace(wabaPhone.DisplayPhoneNumber))
                {
                    insertColumns.Add("display_phone_number");
                    insertValues.Add("@DisplayPhoneNumber");
                    if (!string.Equals(targetColumn, "display_phone_number", StringComparison.Ordinal))
                    {
                        updateSet.Add("display_phone_number = COALESCE(EXCLUDED.display_phone_number, waba_phone.display_phone_number)");
                    }
                }

                insertColumns.Add("id_estabelecimento");
                insertColumns.Add("ativo");
                insertColumns.Add("descricao");
                insertColumns.Add("data_criacao");
                insertColumns.Add("data_atualizacao");

                insertValues.Add("@IdEstabelecimento");
                insertValues.Add("@Ativo");
                insertValues.Add("@Descricao");
                insertValues.Add("@DataCriacao");
                insertValues.Add("@DataAtualizacao");

                updateSet.Add("id_estabelecimento = EXCLUDED.id_estabelecimento");
                updateSet.Add("ativo = EXCLUDED.ativo");
                updateSet.Add("descricao = EXCLUDED.descricao");
                updateSet.Add("data_atualizacao = EXCLUDED.data_atualizacao");

                var sql = $@"INSERT INTO waba_phone ({string.Join(", ", insertColumns)})
                             VALUES ({string.Join(", ", insertValues)})
                             ON CONFLICT ({targetColumn})
                             DO UPDATE SET
                               {string.Join(", ", updateSet)};";
                var rows = await connection.ExecuteAsync(sql, new
                {
                    PhoneNumberId = wabaPhone.PhoneNumberId,
                    DisplayPhoneNumber = wabaPhone.DisplayPhoneNumber,
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

        public async Task<string?> ObterAccessTokenPorPhoneNumberIdAsync(string phoneNumberId)
        {
            if (string.IsNullOrWhiteSpace(phoneNumberId))
                return null;

            var digitsOnly = new string(phoneNumberId.Where(char.IsDigit).ToArray());

            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                var columns = await ObterColunasAsync(connection);
                if (!columns.AccessToken)
                    return null;

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
                    return null;

                var sql = $@"SELECT access_token
                               FROM waba_phone
                              WHERE ativo = TRUE
                                AND ({string.Join(" OR ", comparisons)})
                           ORDER BY data_atualizacao DESC
                              LIMIT 1;";

                return await connection.ExecuteScalarAsync<string?>(sql, new { Raw = phoneNumberId, Digits = digitsOnly });
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Erro ao buscar access_token por phone_number_id {PhoneNumberId}", phoneNumberId);
                return null;
            }
        }

        private async Task<(bool PhoneNumberId, bool DisplayPhoneNumber, bool AccessToken)> ObterColunasAsync(NpgsqlConnection connection)
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
                hash.Contains("display_phone_number"),
                hash.Contains("access_token"));
            return _cachedColumns.Value;
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
