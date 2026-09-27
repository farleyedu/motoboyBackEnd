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
        private static DateTime _cachedColumnsAt = DateTime.MinValue;
        private static readonly TimeSpan _columnsCacheTtl = TimeSpan.FromMinutes(5);

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

            if (string.IsNullOrWhiteSpace(wabaPhone.PhoneNumberId) && string.IsNullOrWhiteSpace(wabaPhone.DisplayPhoneNumber))
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

                var setColumns = new System.Collections.Generic.List<string>
                {
                    "id_estabelecimento = @IdEstabelecimento", "ativo = @Ativo", "descricao = @Descricao", "data_atualizacao = @DataAtualizacao"
                };
                var insertColumns = new System.Collections.Generic.List<string> { "id_estabelecimento", "ativo", "descricao", "data_criacao", "data_atualizacao" };
                var insertValues = new System.Collections.Generic.List<string> { "@IdEstabelecimento", "@Ativo", "@Descricao", "@DataCriacao", "@DataAtualizacao" };

                // As duas colunas, quando existem nesta instalacao, entram sempre no INSERT (com
                // COALESCE para string vazia): pelo menos uma delas tem NOT NULL na tabela legada
                // (achado em producao - "null value in column display_phone_number violates not-null
                // constraint" - so incluir a coluna quando o valor vinha preenchido deixava a outra
                // de fora do INSERT quando so uma era informada, e o Postgres recusava a linha
                // inteira). O UPDATE continua so tocando a coluna que realmente veio preenchida, pra
                // nao apagar um valor ja salvo antes.
                if (columns.PhoneNumberId)
                {
                    insertColumns.Add("phone_number_id");
                    insertValues.Add("COALESCE(@PhoneNumberId, '')");
                    if (!string.IsNullOrWhiteSpace(wabaPhone.PhoneNumberId))
                    {
                        setColumns.Add("phone_number_id = @PhoneNumberId");
                    }
                }

                if (columns.DisplayPhoneNumber)
                {
                    insertColumns.Add("display_phone_number");
                    insertValues.Add("COALESCE(@DisplayPhoneNumber, '')");
                    if (!string.IsNullOrWhiteSpace(wabaPhone.DisplayPhoneNumber))
                    {
                        setColumns.Add("display_phone_number = @DisplayPhoneNumber");
                    }
                }

                if (insertColumns.Count == 5)
                {
                    // Nenhuma das colunas de identificacao do telefone existe nesta instalacao.
                    return false;
                }

                var parameters = new
                {
                    wabaPhone.PhoneNumberId,
                    wabaPhone.DisplayPhoneNumber,
                    IdEstabelecimento = wabaPhone.IdEstabelecimento,
                    Ativo = wabaPhone.Ativo,
                    Descricao = (object?)wabaPhone.Descricao,
                    DataCriacao = criado,
                    DataAtualizacao = atualizado
                };

                // Uma linha por estabelecimento: atualiza se ja existir, senao insere. Antes usava
                // "ON CONFLICT (phone_number_id) DO UPDATE", que exige uma constraint UNIQUE nessa
                // coluna - a tabela legada nao tem, entao o INSERT sempre lancava 42P10 (nenhuma
                // constraint de exclusao ou unicidade corresponde a especificacao ON CONFLICT), o
                // catch abaixo engolia o erro e o formulario "salvava" sem gravar nada.
                var updated = await connection.ExecuteAsync(
                    $"UPDATE waba_phone SET {string.Join(", ", setColumns)} WHERE id_estabelecimento = @IdEstabelecimento;",
                    parameters);

                if (updated == 0)
                {
                    await connection.ExecuteAsync(
                        $"INSERT INTO waba_phone ({string.Join(", ", insertColumns)}) VALUES ({string.Join(", ", insertValues)});",
                        parameters);
                }

                return true;
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

        public async Task<string?> ObterPhoneNumberIdPorDisplayPhoneAsync(string displayPhoneNumber)
        {
            if (string.IsNullOrWhiteSpace(displayPhoneNumber))
                return null;

            var digitsOnly = new string(displayPhoneNumber.Where(char.IsDigit).ToArray());

            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                var columns = await ObterColunasAsync(connection);
                if (!columns.PhoneNumberId || !columns.DisplayPhoneNumber)
                    return null;

                // Busca o phone_number_id (Meta UID) filtrando pelo display_phone_number informado
                var sql = @"SELECT phone_number_id
                               FROM waba_phone
                              WHERE ativo = TRUE
                                AND (display_phone_number = @Raw OR regexp_replace(display_phone_number, '[^0-9]', '', 'g') = @Digits)
                           ORDER BY data_atualizacao DESC
                              LIMIT 1;";

                return await connection.ExecuteScalarAsync<string?>(sql, new { Raw = displayPhoneNumber, Digits = digitsOnly });
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Erro ao buscar phone_number_id por displayPhoneNumber {DisplayPhone}", displayPhoneNumber);
                return null;
            }
        }

        private async Task<(bool PhoneNumberId, bool DisplayPhoneNumber, bool AccessToken)> ObterColunasAsync(NpgsqlConnection connection)
        {
            if (_cachedColumns.HasValue && DateTime.UtcNow - _cachedColumnsAt < _columnsCacheTtl)
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
            _cachedColumnsAt = DateTime.UtcNow;
            return _cachedColumns.Value;
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
