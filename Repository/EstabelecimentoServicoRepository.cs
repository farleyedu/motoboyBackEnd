using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using APIBack.Model;
using APIBack.Repository.Interface;
using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace APIBack.Repository
{
    public class EstabelecimentoServicoRepository : IEstabelecimentoServicoRepository
    {
        private readonly string _connectionString;
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        public EstabelecimentoServicoRepository(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection")
                ?? throw new InvalidOperationException("Connection string 'DefaultConnection' nao encontrada.");
        }

        public async Task<(IReadOnlyCollection<EstabelecimentoServico> Itens, int Total)> ListarAsync(
            Guid idEstabelecimento,
            string? busca,
            bool? ativo,
            bool? agendavel,
            string? tipo,
            int page,
            int pageSize)
        {
            const string sqlCount = @"
SELECT COUNT(1)
  FROM estabelecimento_servicos
 WHERE id_estabelecimento = @IdEstabelecimento
   AND deleted_at IS NULL
   AND (@Ativo IS NULL OR ativo = @Ativo)
   AND (@Agendavel IS NULL OR permite_agendamento = @Agendavel)
   AND (@Tipo IS NULL OR tipo = @Tipo)
   AND (
       @Busca IS NULL
       OR nome ILIKE @BuscaLike
       OR COALESCE(descricao, '') ILIKE @BuscaLike
       OR tipo ILIKE @BuscaLike
   );";

            const string sqlItens = @"
SELECT id,
       id_estabelecimento AS IdEstabelecimento,
       nome,
       descricao,
       tipo,
       duracao_minutos AS DuracaoMinutos,
       valor_centavos AS ValorCentavos,
       ativo,
       exibir_no_bot AS ExibirNoBot,
       permite_agendamento AS PermiteAgendamento,
       palavras_chave::text AS PalavrasChaveJson,
       created_at AS CreatedAt,
       updated_at AS UpdatedAt,
       deleted_at AS DeletedAt
  FROM estabelecimento_servicos
 WHERE id_estabelecimento = @IdEstabelecimento
   AND deleted_at IS NULL
   AND (@Ativo IS NULL OR ativo = @Ativo)
   AND (@Agendavel IS NULL OR permite_agendamento = @Agendavel)
   AND (@Tipo IS NULL OR tipo = @Tipo)
   AND (
       @Busca IS NULL
       OR nome ILIKE @BuscaLike
       OR COALESCE(descricao, '') ILIKE @BuscaLike
       OR tipo ILIKE @BuscaLike
   )
 ORDER BY nome ASC
 LIMIT @PageSize OFFSET @Offset;";

            var parameters = new
            {
                IdEstabelecimento = idEstabelecimento,
                Ativo = ativo,
                Agendavel = agendavel,
                Tipo = string.IsNullOrWhiteSpace(tipo) ? null : tipo.Trim(),
                Busca = string.IsNullOrWhiteSpace(busca) ? null : busca.Trim(),
                BuscaLike = string.IsNullOrWhiteSpace(busca) ? null : $"%{busca.Trim()}%",
                PageSize = pageSize,
                Offset = (page - 1) * pageSize
            };

            await using var connection = new NpgsqlConnection(_connectionString);
            var total = await connection.ExecuteScalarAsync<int>(sqlCount, parameters);
            var rows = await connection.QueryAsync<Row>(sqlItens, parameters);
            return (rows.Select(Map).ToArray(), total);
        }

        public async Task<IReadOnlyCollection<EstabelecimentoServico>> ListarTodosAsync(Guid idEstabelecimento)
        {
            const string sql = @"
SELECT id,
       id_estabelecimento AS IdEstabelecimento,
       nome,
       descricao,
       tipo,
       duracao_minutos AS DuracaoMinutos,
       valor_centavos AS ValorCentavos,
       ativo,
       exibir_no_bot AS ExibirNoBot,
       permite_agendamento AS PermiteAgendamento,
       palavras_chave::text AS PalavrasChaveJson,
       created_at AS CreatedAt,
       updated_at AS UpdatedAt,
       deleted_at AS DeletedAt
  FROM estabelecimento_servicos
 WHERE id_estabelecimento = @IdEstabelecimento
   AND deleted_at IS NULL
 ORDER BY nome ASC;";

            await using var connection = new NpgsqlConnection(_connectionString);
            var rows = await connection.QueryAsync<Row>(sql, new
            {
                IdEstabelecimento = idEstabelecimento
            });

            return rows.Select(Map).ToArray();
        }

        public async Task<EstabelecimentoServico?> ObterPorIdAsync(Guid idEstabelecimento, Guid id)
        {
            const string sql = @"
SELECT id,
       id_estabelecimento AS IdEstabelecimento,
       nome,
       descricao,
       tipo,
       duracao_minutos AS DuracaoMinutos,
       valor_centavos AS ValorCentavos,
       ativo,
       exibir_no_bot AS ExibirNoBot,
       permite_agendamento AS PermiteAgendamento,
       palavras_chave::text AS PalavrasChaveJson,
       created_at AS CreatedAt,
       updated_at AS UpdatedAt,
       deleted_at AS DeletedAt
  FROM estabelecimento_servicos
 WHERE id_estabelecimento = @IdEstabelecimento
   AND id = @Id
   AND deleted_at IS NULL;";

            await using var connection = new NpgsqlConnection(_connectionString);
            var row = await connection.QueryFirstOrDefaultAsync<Row>(sql, new
            {
                IdEstabelecimento = idEstabelecimento,
                Id = id
            });

            return row == null ? null : Map(row);
        }

        public async Task<Guid> CriarAsync(EstabelecimentoServico entity)
        {
            const string sql = @"
INSERT INTO estabelecimento_servicos (
    id,
    id_estabelecimento,
    nome,
    descricao,
    tipo,
    duracao_minutos,
    valor_centavos,
    ativo,
    exibir_no_bot,
    permite_agendamento,
    palavras_chave,
    created_at,
    updated_at
) VALUES (
    @Id,
    @IdEstabelecimento,
    @Nome,
    @Descricao,
    @Tipo,
    @DuracaoMinutos,
    @ValorCentavos,
    @Ativo,
    @ExibirNoBot,
    @PermiteAgendamento,
    CAST(@PalavrasChave AS jsonb),
    @CreatedAt,
    @UpdatedAt
);";

            if (entity.Id == Guid.Empty)
            {
                entity.Id = Guid.NewGuid();
            }

            var now = DateTime.UtcNow;
            entity.CreatedAt = now;
            entity.UpdatedAt = now;

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.ExecuteAsync(sql, ToParameters(entity));
            return entity.Id;
        }

        public async Task<bool> AtualizarAsync(EstabelecimentoServico entity)
        {
            const string sql = @"
UPDATE estabelecimento_servicos
   SET nome = @Nome,
       descricao = @Descricao,
       tipo = @Tipo,
       duracao_minutos = @DuracaoMinutos,
       valor_centavos = @ValorCentavos,
       ativo = @Ativo,
       exibir_no_bot = @ExibirNoBot,
       permite_agendamento = @PermiteAgendamento,
       palavras_chave = CAST(@PalavrasChave AS jsonb),
       updated_at = @UpdatedAt
 WHERE id_estabelecimento = @IdEstabelecimento
   AND id = @Id
   AND deleted_at IS NULL;";

            entity.UpdatedAt = DateTime.UtcNow;

            await using var connection = new NpgsqlConnection(_connectionString);
            var affected = await connection.ExecuteAsync(sql, ToParameters(entity));
            return affected > 0;
        }

        public async Task<bool> AtualizarStatusAsync(Guid idEstabelecimento, Guid id, bool ativo)
        {
            const string sql = @"
UPDATE estabelecimento_servicos
   SET ativo = @Ativo,
       updated_at = NOW()
 WHERE id_estabelecimento = @IdEstabelecimento
   AND id = @Id
   AND deleted_at IS NULL;";

            await using var connection = new NpgsqlConnection(_connectionString);
            var affected = await connection.ExecuteAsync(sql, new
            {
                IdEstabelecimento = idEstabelecimento,
                Id = id,
                Ativo = ativo
            });

            return affected > 0;
        }

        public async Task<bool> ExcluirAsync(Guid idEstabelecimento, Guid id)
        {
            const string sql = @"
UPDATE estabelecimento_servicos
   SET deleted_at = NOW(),
       updated_at = NOW()
 WHERE id_estabelecimento = @IdEstabelecimento
   AND id = @Id
   AND deleted_at IS NULL;";

            await using var connection = new NpgsqlConnection(_connectionString);
            var affected = await connection.ExecuteAsync(sql, new { IdEstabelecimento = idEstabelecimento, Id = id });
            return affected > 0;
        }

        private static object ToParameters(EstabelecimentoServico entity)
        {
            return new
            {
                entity.Id,
                entity.IdEstabelecimento,
                entity.Nome,
                entity.Descricao,
                entity.Tipo,
                entity.DuracaoMinutos,
                entity.ValorCentavos,
                entity.Ativo,
                entity.ExibirNoBot,
                entity.PermiteAgendamento,
                PalavrasChave = JsonSerializer.Serialize(entity.PalavrasChave ?? new List<string>(), JsonOptions),
                entity.CreatedAt,
                entity.UpdatedAt
            };
        }

        private static EstabelecimentoServico Map(Row row)
        {
            return new EstabelecimentoServico
            {
                Id = row.Id,
                IdEstabelecimento = row.IdEstabelecimento,
                Nome = row.Nome ?? string.Empty,
                Descricao = row.Descricao,
                Tipo = row.Tipo ?? string.Empty,
                DuracaoMinutos = row.DuracaoMinutos,
                ValorCentavos = row.ValorCentavos,
                Ativo = row.Ativo,
                ExibirNoBot = row.ExibirNoBot,
                PermiteAgendamento = row.PermiteAgendamento,
                PalavrasChave = DeserializeList(row.PalavrasChaveJson),
                CreatedAt = row.CreatedAt,
                UpdatedAt = row.UpdatedAt,
                DeletedAt = row.DeletedAt
            };
        }

        private static List<string> DeserializeList(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return new List<string>();
            }

            try
            {
                return JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? new List<string>();
            }
            catch
            {
                return new List<string>();
            }
        }

        private sealed class Row
        {
            public Guid Id { get; set; }
            public Guid IdEstabelecimento { get; set; }
            public string? Nome { get; set; }
            public string? Descricao { get; set; }
            public string? Tipo { get; set; }
            public int DuracaoMinutos { get; set; }
            public long? ValorCentavos { get; set; }
            public bool Ativo { get; set; }
            public bool ExibirNoBot { get; set; }
            public bool PermiteAgendamento { get; set; }
            public string? PalavrasChaveJson { get; set; }
            public DateTime CreatedAt { get; set; }
            public DateTime UpdatedAt { get; set; }
            public DateTime? DeletedAt { get; set; }
        }
    }
}
