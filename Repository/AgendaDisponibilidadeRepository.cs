using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using APIBack.Model;
using APIBack.Repository.Interface;
using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace APIBack.Repository
{
    public class AgendaDisponibilidadeRepository : IAgendaDisponibilidadeRepository
    {
        private readonly string _connectionString;

        public AgendaDisponibilidadeRepository(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection")
                ?? throw new InvalidOperationException("Connection string 'DefaultConnection' nao encontrada.");
        }

        public async Task<(IReadOnlyCollection<AgendaDisponibilidade> Itens, int Total)> ListarAsync(
            Guid idEstabelecimento,
            bool? ativo,
            string? tipo,
            string? escopo,
            long? profissionalId,
            int page,
            int pageSize)
        {
            const string sqlCount = @"
SELECT COUNT(1)
  FROM agenda_disponibilidade
 WHERE id_estabelecimento = @IdEstabelecimento
   AND deleted_at IS NULL
   AND (@Ativo IS NULL OR ativo = @Ativo)
   AND (@Tipo IS NULL OR tipo = @Tipo)
   AND (@Escopo IS NULL OR escopo = @Escopo)
   AND (@ProfissionalId IS NULL OR profissional_id = @ProfissionalId);";

            const string sqlItens = @"
SELECT id,
       id_estabelecimento AS IdEstabelecimento,
       profissional_id AS ProfissionalId,
       escopo,
       tipo,
       COALESCE(dias_semana, ARRAY[]::integer[]) AS DiasSemana,
       data_inicio AS DataInicio,
       data_fim AS DataFim,
       hora_inicio AS HoraInicio,
       hora_fim AS HoraFim,
       dia_inteiro AS DiaInteiro,
       ativo,
       observacao,
       created_at AS CreatedAt,
       updated_at AS UpdatedAt,
       deleted_at AS DeletedAt
  FROM agenda_disponibilidade
 WHERE id_estabelecimento = @IdEstabelecimento
   AND deleted_at IS NULL
   AND (@Ativo IS NULL OR ativo = @Ativo)
   AND (@Tipo IS NULL OR tipo = @Tipo)
   AND (@Escopo IS NULL OR escopo = @Escopo)
   AND (@ProfissionalId IS NULL OR profissional_id = @ProfissionalId)
 ORDER BY tipo ASC, escopo ASC, created_at DESC
 LIMIT @PageSize OFFSET @Offset;";

            var parameters = new
            {
                IdEstabelecimento = idEstabelecimento,
                Ativo = ativo,
                Tipo = string.IsNullOrWhiteSpace(tipo) ? null : tipo.Trim(),
                Escopo = string.IsNullOrWhiteSpace(escopo) ? null : escopo.Trim(),
                ProfissionalId = profissionalId,
                PageSize = pageSize,
                Offset = (page - 1) * pageSize
            };

            await using var connection = new NpgsqlConnection(_connectionString);
            var total = await connection.ExecuteScalarAsync<int>(sqlCount, parameters);
            var rows = await connection.QueryAsync<Row>(sqlItens, parameters);
            return (rows.Select(Map).ToArray(), total);
        }

        public async Task<IReadOnlyCollection<AgendaDisponibilidade>> ListarTodasAsync(Guid idEstabelecimento)
        {
            const string sql = @"
SELECT id,
       id_estabelecimento AS IdEstabelecimento,
       profissional_id AS ProfissionalId,
       escopo,
       tipo,
       COALESCE(dias_semana, ARRAY[]::integer[]) AS DiasSemana,
       data_inicio AS DataInicio,
       data_fim AS DataFim,
       hora_inicio AS HoraInicio,
       hora_fim AS HoraFim,
       dia_inteiro AS DiaInteiro,
       ativo,
       observacao,
       created_at AS CreatedAt,
       updated_at AS UpdatedAt,
       deleted_at AS DeletedAt
  FROM agenda_disponibilidade
 WHERE id_estabelecimento = @IdEstabelecimento
   AND deleted_at IS NULL
 ORDER BY tipo ASC, escopo ASC, created_at DESC;";

            await using var connection = new NpgsqlConnection(_connectionString);
            var rows = await connection.QueryAsync<Row>(sql, new
            {
                IdEstabelecimento = idEstabelecimento
            });

            return rows.Select(Map).ToArray();
        }

        public async Task<AgendaDisponibilidade?> ObterPorIdAsync(Guid idEstabelecimento, Guid id)
        {
            const string sql = @"
SELECT id,
       id_estabelecimento AS IdEstabelecimento,
       profissional_id AS ProfissionalId,
       escopo,
       tipo,
       COALESCE(dias_semana, ARRAY[]::integer[]) AS DiasSemana,
       data_inicio AS DataInicio,
       data_fim AS DataFim,
       hora_inicio AS HoraInicio,
       hora_fim AS HoraFim,
       dia_inteiro AS DiaInteiro,
       ativo,
       observacao,
       created_at AS CreatedAt,
       updated_at AS UpdatedAt,
       deleted_at AS DeletedAt
  FROM agenda_disponibilidade
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

        public async Task<Guid> CriarAsync(AgendaDisponibilidade entity)
        {
            const string sql = @"
INSERT INTO agenda_disponibilidade (
    id,
    id_estabelecimento,
    profissional_id,
    escopo,
    tipo,
    dias_semana,
    data_inicio,
    data_fim,
    hora_inicio,
    hora_fim,
    dia_inteiro,
    ativo,
    observacao,
    created_at,
    updated_at
) VALUES (
    @Id,
    @IdEstabelecimento,
    @ProfissionalId,
    @Escopo,
    @Tipo,
    @DiasSemana,
    @DataInicio,
    @DataFim,
    @HoraInicio,
    @HoraFim,
    @DiaInteiro,
    @Ativo,
    @Observacao,
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
            await connection.ExecuteAsync(sql, entity);
            return entity.Id;
        }

        public async Task<bool> AtualizarAsync(AgendaDisponibilidade entity)
        {
            const string sql = @"
UPDATE agenda_disponibilidade
   SET profissional_id = @ProfissionalId,
       escopo = @Escopo,
       tipo = @Tipo,
       dias_semana = @DiasSemana,
       data_inicio = @DataInicio,
       data_fim = @DataFim,
       hora_inicio = @HoraInicio,
       hora_fim = @HoraFim,
       dia_inteiro = @DiaInteiro,
       ativo = @Ativo,
       observacao = @Observacao,
       updated_at = @UpdatedAt
 WHERE id_estabelecimento = @IdEstabelecimento
   AND id = @Id
   AND deleted_at IS NULL;";

            entity.UpdatedAt = DateTime.UtcNow;

            await using var connection = new NpgsqlConnection(_connectionString);
            var affected = await connection.ExecuteAsync(sql, entity);
            return affected > 0;
        }

        public async Task<bool> AtualizarStatusAsync(Guid idEstabelecimento, Guid id, bool ativo)
        {
            const string sql = @"
UPDATE agenda_disponibilidade
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
UPDATE agenda_disponibilidade
   SET deleted_at = NOW(),
       updated_at = NOW()
 WHERE id_estabelecimento = @IdEstabelecimento
   AND id = @Id
   AND deleted_at IS NULL;";

            await using var connection = new NpgsqlConnection(_connectionString);
            var affected = await connection.ExecuteAsync(sql, new { IdEstabelecimento = idEstabelecimento, Id = id });
            return affected > 0;
        }

        public async Task<bool> ProfissionalExisteNoEstabelecimentoAsync(Guid idEstabelecimento, long profissionalId)
        {
            const string sql = @"
SELECT COUNT(1)
  FROM profissionais
 WHERE id = @ProfissionalId
   AND id_estabelecimento::text = @IdEstabelecimento
   AND ativo = TRUE;";

            await using var connection = new NpgsqlConnection(_connectionString);
            var count = await connection.ExecuteScalarAsync<long>(sql, new
            {
                ProfissionalId = profissionalId,
                IdEstabelecimento = idEstabelecimento.ToString()
            });

            return count > 0;
        }

        public async Task<IReadOnlyCollection<(long Id, string Nome, bool Ativo)>> ListarProfissionaisAsync(Guid idEstabelecimento)
        {
            const string sql = @"
SELECT p.id AS Id,
       COALESCE(NULLIF(btrim(u.nome), ''), CONCAT('Profissional ', p.id::text)) AS Nome,
       p.ativo AS Ativo
  FROM profissionais p
  LEFT JOIN usuario u ON u.id::bigint = p.id_usuario
 WHERE p.id_estabelecimento::text = @IdEstabelecimento
   AND p.ativo = TRUE
 ORDER BY Nome ASC;";

            await using var connection = new NpgsqlConnection(_connectionString);
            var rows = await connection.QueryAsync<ProfissionalRow>(sql, new
            {
                IdEstabelecimento = idEstabelecimento.ToString()
            });

            return rows.Select(r => (r.Id, r.Nome ?? string.Empty, r.Ativo)).ToArray();
        }

        public async Task<bool> ExisteConflitoAsync(Guid idEstabelecimento, AgendaDisponibilidade entity)
        {
            const string sqlSemanal = @"
SELECT EXISTS (
    SELECT 1
      FROM agenda_disponibilidade
     WHERE id_estabelecimento = @IdEstabelecimento
       AND deleted_at IS NULL
       AND ativo = TRUE
       AND id <> @Id
       AND escopo = @Escopo
       AND tipo = 'disponibilidade_semanal'
       AND ((@ProfissionalId IS NULL AND profissional_id IS NULL) OR profissional_id = @ProfissionalId)
       AND dias_semana && @DiasSemana
       AND @HoraFim > hora_inicio
       AND COALESCE(hora_fim, @HoraFim) > @HoraInicio
);";

            const string sqlData = @"
SELECT EXISTS (
    SELECT 1
      FROM agenda_disponibilidade
     WHERE id_estabelecimento = @IdEstabelecimento
       AND deleted_at IS NULL
       AND ativo = TRUE
       AND id <> @Id
       AND escopo = @Escopo
       AND tipo = @Tipo
       AND ((@ProfissionalId IS NULL AND profissional_id IS NULL) OR profissional_id = @ProfissionalId)
       AND daterange(data_inicio, data_fim, '[]') && daterange(@DataInicio, @DataFim, '[]')
       AND (
            @DiaInteiro = TRUE
            OR dia_inteiro = TRUE
            OR (
                @HoraFim IS NOT NULL
                AND hora_inicio IS NOT NULL
                AND @HoraFim > hora_inicio
                AND COALESCE(hora_fim, @HoraFim) > @HoraInicio
            )
       )
);";

            await using var connection = new NpgsqlConnection(_connectionString);
            if (string.Equals(entity.Tipo, "disponibilidade_semanal", StringComparison.OrdinalIgnoreCase))
            {
                return await connection.ExecuteScalarAsync<bool>(sqlSemanal, new
                {
                    entity.Id,
                    IdEstabelecimento = idEstabelecimento,
                    entity.Escopo,
                    entity.ProfissionalId,
                    DiasSemana = entity.DiasSemana.ToArray(),
                    entity.HoraInicio,
                    entity.HoraFim
                });
            }

            return await connection.ExecuteScalarAsync<bool>(sqlData, new
            {
                entity.Id,
                IdEstabelecimento = idEstabelecimento,
                entity.Escopo,
                entity.ProfissionalId,
                entity.Tipo,
                entity.DataInicio,
                entity.DataFim,
                entity.HoraInicio,
                entity.HoraFim,
                entity.DiaInteiro
            });
        }

        private static AgendaDisponibilidade Map(Row row)
        {
            return new AgendaDisponibilidade
            {
                Id = row.Id,
                IdEstabelecimento = row.IdEstabelecimento,
                ProfissionalId = row.ProfissionalId,
                Escopo = row.Escopo ?? "estabelecimento",
                Tipo = row.Tipo ?? "disponibilidade_semanal",
                DiasSemana = row.DiasSemana?.ToList() ?? new List<int>(),
                DataInicio = NormalizeDate(row.DataInicio),
                DataFim = NormalizeDate(row.DataFim),
                HoraInicio = NormalizeTime(row.HoraInicio),
                HoraFim = NormalizeTime(row.HoraFim),
                DiaInteiro = row.DiaInteiro,
                Ativo = row.Ativo,
                Observacao = row.Observacao,
                CreatedAt = row.CreatedAt,
                UpdatedAt = row.UpdatedAt,
                DeletedAt = row.DeletedAt
            };
        }

        private static DateOnly? NormalizeDate(object? value)
        {
            return value switch
            {
                null or DBNull => null,
                DateOnly dateOnly => dateOnly,
                DateTime dateTime => DateOnly.FromDateTime(dateTime),
                string text when DateOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) => parsed,
                _ => throw new DataException($"Tipo de data nao suportado para agenda_disponibilidade: {value.GetType().FullName}.")
            };
        }

        private static TimeOnly? NormalizeTime(object? value)
        {
            return value switch
            {
                null or DBNull => null,
                TimeOnly timeOnly => timeOnly,
                TimeSpan timeSpan => TimeOnly.FromTimeSpan(timeSpan),
                DateTime dateTime => TimeOnly.FromDateTime(dateTime),
                string text when TimeOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) => parsed,
                _ => throw new DataException($"Tipo de hora nao suportado para agenda_disponibilidade: {value.GetType().FullName}.")
            };
        }

        private sealed class Row
        {
            public Guid Id { get; set; }
            public Guid IdEstabelecimento { get; set; }
            public long? ProfissionalId { get; set; }
            public string? Escopo { get; set; }
            public string? Tipo { get; set; }
            public int[]? DiasSemana { get; set; }
            public object? DataInicio { get; set; }
            public object? DataFim { get; set; }
            public object? HoraInicio { get; set; }
            public object? HoraFim { get; set; }
            public bool DiaInteiro { get; set; }
            public bool Ativo { get; set; }
            public string? Observacao { get; set; }
            public DateTime CreatedAt { get; set; }
            public DateTime UpdatedAt { get; set; }
            public DateTime? DeletedAt { get; set; }
        }

        private sealed class ProfissionalRow
        {
            public long Id { get; set; }
            public string? Nome { get; set; }
            public bool Ativo { get; set; }
        }
    }
}
