using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using APIBack.DTOs.Cardapio;
using APIBack.Repository.Interface;
using APIBack.Service;
using Dapper;
using Npgsql;

namespace APIBack.Repository
{
    public sealed class ProdutoAtendimentoRepository : IProdutoAtendimentoRepository
    {
        private const string UndefinedTable = "42P01";
        private readonly NpgsqlDataSource _dataSource;

        public ProdutoAtendimentoRepository(NpgsqlDataSource dataSource)
        {
            _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        }

        public async Task<IReadOnlyDictionary<Guid, ProdutoAtendimentoDto>> ListAsync(Guid estabelecimentoId)
        {
            try
            {
                await using var connection = await _dataSource.OpenConnectionAsync();
                var rows = await connection.QueryAsync<Row>(@"
SELECT produto_id AS ProdutoId, apelidos AS Apelidos, instrucoes AS Instrucoes,
       restricoes AS Restricoes, tempo_extra_preparo_min AS TempoExtraPreparoMin
  FROM cardapio_produto_atendimento
 WHERE id_estabelecimento = @EstabelecimentoId;", new { EstabelecimentoId = estabelecimentoId });
                return rows.ToDictionary(row => row.ProdutoId, ToDto);
            }
            catch (PostgresException ex) when (ex.SqlState == UndefinedTable)
            {
                // Migration 20260926_01 ainda nao aplicada: a ficha sai sem os campos de atendimento.
                return new Dictionary<Guid, ProdutoAtendimentoDto>();
            }
        }

        public async Task<ProdutoAtendimentoDto?> GetAsync(Guid estabelecimentoId, Guid produtoId)
        {
            var all = await ListAsync(estabelecimentoId);
            return all.TryGetValue(produtoId, out var dto) ? dto : null;
        }

        public async Task<ProdutoAtendimentoDto?> UpsertAsync(Guid estabelecimentoId, Guid produtoId, ProdutoAtendimentoDto data)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            var belongs = await connection.ExecuteScalarAsync<bool>(
                "SELECT EXISTS (SELECT 1 FROM cardapio_produto WHERE id = @ProdutoId AND id_estabelecimento = @EstabelecimentoId AND deleted_at IS NULL);",
                new { ProdutoId = produtoId, EstabelecimentoId = estabelecimentoId });
            if (!belongs) return null;

            try
            {
                await connection.ExecuteAsync(@"
INSERT INTO cardapio_produto_atendimento (produto_id, id_estabelecimento, apelidos, instrucoes, restricoes, tempo_extra_preparo_min, updated_at)
VALUES (@ProdutoId, @EstabelecimentoId, @Apelidos, @Instrucoes, @Restricoes, @TempoExtraPreparoMin, NOW())
ON CONFLICT (produto_id) DO UPDATE
   SET apelidos = EXCLUDED.apelidos,
       instrucoes = EXCLUDED.instrucoes,
       restricoes = EXCLUDED.restricoes,
       tempo_extra_preparo_min = EXCLUDED.tempo_extra_preparo_min,
       updated_at = NOW();",
                    new
                    {
                        ProdutoId = produtoId,
                        EstabelecimentoId = estabelecimentoId,
                        Apelidos = data.Apelidos.ToArray(),
                        data.Instrucoes,
                        data.Restricoes,
                        data.TempoExtraPreparoMin
                    });
            }
            catch (PostgresException ex) when (ex.SqlState == UndefinedTable)
            {
                throw new DeliveryDomainException(503, "MIGRATION_PENDING",
                    "Os campos de atendimento exigem a migration 20260926_01 do delivery, ainda nao aplicada neste banco.");
            }

            return data;
        }

        private static ProdutoAtendimentoDto ToDto(Row row) => new()
        {
            Apelidos = row.Apelidos?.ToList() ?? new List<string>(),
            Instrucoes = row.Instrucoes,
            Restricoes = row.Restricoes,
            TempoExtraPreparoMin = row.TempoExtraPreparoMin
        };

        private sealed class Row
        {
            public Guid ProdutoId { get; set; }
            public string[]? Apelidos { get; set; }
            public string? Instrucoes { get; set; }
            public string? Restricoes { get; set; }
            public int? TempoExtraPreparoMin { get; set; }
        }
    }
}
