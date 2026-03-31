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
    public class EstabelecimentoFaqRepository : IEstabelecimentoFaqRepository
    {
        private readonly string _connectionString;
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        public EstabelecimentoFaqRepository(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection")
                ?? throw new InvalidOperationException("Connection string 'DefaultConnection' nao encontrada.");
        }

        public async Task<(IReadOnlyCollection<EstabelecimentoFaq> Itens, int Total)> ListarAsync(
            Guid idEstabelecimento,
            string? busca,
            bool? ativo,
            string? categoria,
            string? acao,
            int page,
            int pageSize)
        {
            const string sqlCount = @"
SELECT COUNT(1)
  FROM estabelecimento_faq
 WHERE id_estabelecimento = @IdEstabelecimento
   AND deleted_at IS NULL
   AND (@Ativo IS NULL OR ativo = @Ativo)
   AND (@Categoria IS NULL OR categoria = @Categoria)
   AND (@Acao IS NULL OR acao = @Acao)
   AND (
       @Busca IS NULL
       OR pergunta ILIKE @BuscaLike
       OR resposta ILIKE @BuscaLike
       OR categoria ILIKE @BuscaLike
   );";

            const string sqlItens = @"
SELECT id,
       id_estabelecimento AS IdEstabelecimento,
       pergunta,
       resposta,
       categoria,
       acao AS Acao,
       ordem AS Ordem,
       ativo,
       palavras_chave::text AS PalavrasChaveJson,
       created_at AS CreatedAt,
       updated_at AS UpdatedAt,
       deleted_at AS DeletedAt
  FROM estabelecimento_faq
 WHERE id_estabelecimento = @IdEstabelecimento
   AND deleted_at IS NULL
   AND (@Ativo IS NULL OR ativo = @Ativo)
   AND (@Categoria IS NULL OR categoria = @Categoria)
   AND (@Acao IS NULL OR acao = @Acao)
   AND (
       @Busca IS NULL
       OR pergunta ILIKE @BuscaLike
       OR resposta ILIKE @BuscaLike
       OR categoria ILIKE @BuscaLike
   )
 ORDER BY categoria ASC, ordem ASC, pergunta ASC
 LIMIT @PageSize OFFSET @Offset;";

            var parameters = new
            {
                IdEstabelecimento = idEstabelecimento,
                Ativo = ativo,
                Categoria = string.IsNullOrWhiteSpace(categoria) ? null : categoria.Trim(),
                Acao = string.IsNullOrWhiteSpace(acao) ? null : acao.Trim(),
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

        public async Task<EstabelecimentoFaq?> ObterPorIdAsync(Guid idEstabelecimento, Guid id)
        {
            const string sql = @"
SELECT id,
       id_estabelecimento AS IdEstabelecimento,
       pergunta,
       resposta,
       categoria,
       acao AS Acao,
       ordem AS Ordem,
       ativo,
       palavras_chave::text AS PalavrasChaveJson,
       created_at AS CreatedAt,
       updated_at AS UpdatedAt,
       deleted_at AS DeletedAt
  FROM estabelecimento_faq
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

        public async Task<Guid> CriarAsync(EstabelecimentoFaq entity)
        {
            const string sql = @"
INSERT INTO estabelecimento_faq (
    id,
    id_estabelecimento,
    pergunta,
    resposta,
    categoria,
    acao,
    ordem,
    ativo,
    palavras_chave,
    created_at,
    updated_at
) VALUES (
    @Id,
    @IdEstabelecimento,
    @Pergunta,
    @Resposta,
    @Categoria,
    @Acao,
    @Ordem,
    @Ativo,
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

        public async Task<bool> AtualizarAsync(EstabelecimentoFaq entity)
        {
            const string sql = @"
UPDATE estabelecimento_faq
   SET pergunta = @Pergunta,
       resposta = @Resposta,
       categoria = @Categoria,
       acao = @Acao,
       ordem = @Ordem,
       ativo = @Ativo,
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
UPDATE estabelecimento_faq
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
UPDATE estabelecimento_faq
   SET deleted_at = NOW(),
       updated_at = NOW()
 WHERE id_estabelecimento = @IdEstabelecimento
   AND id = @Id
   AND deleted_at IS NULL;";

            await using var connection = new NpgsqlConnection(_connectionString);
            var affected = await connection.ExecuteAsync(sql, new { IdEstabelecimento = idEstabelecimento, Id = id });
            return affected > 0;
        }

        private static object ToParameters(EstabelecimentoFaq entity)
        {
            return new
            {
                entity.Id,
                entity.IdEstabelecimento,
                entity.Pergunta,
                entity.Resposta,
                entity.Categoria,
                entity.Acao,
                entity.Ordem,
                entity.Ativo,
                PalavrasChave = JsonSerializer.Serialize(entity.PalavrasChave ?? new List<string>(), JsonOptions),
                entity.CreatedAt,
                entity.UpdatedAt
            };
        }

        private static EstabelecimentoFaq Map(Row row)
        {
            return new EstabelecimentoFaq
            {
                Id = row.Id,
                IdEstabelecimento = row.IdEstabelecimento,
                Pergunta = row.Pergunta ?? string.Empty,
                Resposta = row.Resposta ?? string.Empty,
                Categoria = row.Categoria ?? string.Empty,
                Acao = row.Acao ?? "responder",
                Ordem = row.Ordem,
                Ativo = row.Ativo,
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
            public string? Pergunta { get; set; }
            public string? Resposta { get; set; }
            public string? Categoria { get; set; }
            public string? Acao { get; set; }
            public int Ordem { get; set; }
            public bool Ativo { get; set; }
            public string? PalavrasChaveJson { get; set; }
            public DateTime CreatedAt { get; set; }
            public DateTime UpdatedAt { get; set; }
            public DateTime? DeletedAt { get; set; }
        }
    }
}
