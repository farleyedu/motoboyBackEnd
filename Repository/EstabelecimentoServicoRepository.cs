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
       difere_por_veiculo AS DiferePorVeiculo,
       difere_por_marca_peca AS DiferePorMarcaPeca,
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
            var rows = (await connection.QueryAsync<Row>(sqlItens, parameters)).ToArray();
            var itens = rows.Select(Map).ToArray();
            await PopulateChildrenAsync(connection, itens);
            return (itens, total);
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
       difere_por_veiculo AS DiferePorVeiculo,
       difere_por_marca_peca AS DiferePorMarcaPeca,
       created_at AS CreatedAt,
       updated_at AS UpdatedAt,
       deleted_at AS DeletedAt
  FROM estabelecimento_servicos
 WHERE id_estabelecimento = @IdEstabelecimento
   AND deleted_at IS NULL
 ORDER BY nome ASC;";

            await using var connection = new NpgsqlConnection(_connectionString);
            var rows = (await connection.QueryAsync<Row>(sql, new
            {
                IdEstabelecimento = idEstabelecimento
            })).ToArray();

            var itens = rows.Select(Map).ToArray();
            await PopulateChildrenAsync(connection, itens);
            return itens;
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
       difere_por_veiculo AS DiferePorVeiculo,
       difere_por_marca_peca AS DiferePorMarcaPeca,
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

            if (row == null)
            {
                return null;
            }

            var item = Map(row);
            await PopulateChildrenAsync(connection, new[] { item });
            return item;
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
    difere_por_veiculo,
    difere_por_marca_peca,
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
    @DiferePorVeiculo,
    @DiferePorMarcaPeca,
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
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            try
            {
                await connection.ExecuteAsync(sql, ToParameters(entity), transaction);
                await InsertChildrenAsync(connection, transaction, entity, now);
                await transaction.CommitAsync();
                return entity.Id;
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
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
       difere_por_veiculo = @DiferePorVeiculo,
       difere_por_marca_peca = @DiferePorMarcaPeca,
       updated_at = @UpdatedAt
 WHERE id_estabelecimento = @IdEstabelecimento
   AND id = @Id
   AND deleted_at IS NULL;";

            entity.UpdatedAt = DateTime.UtcNow;

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            try
            {
                var affected = await connection.ExecuteAsync(sql, ToParameters(entity), transaction);
                if (affected <= 0)
                {
                    await transaction.RollbackAsync();
                    return false;
                }

                await DeleteChildrenAsync(connection, transaction, entity.Id);
                await InsertChildrenAsync(connection, transaction, entity, entity.UpdatedAt);
                await transaction.CommitAsync();
                return true;
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
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
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            try
            {
                var affected = await connection.ExecuteAsync(sql, new
                {
                    IdEstabelecimento = idEstabelecimento,
                    Id = id
                }, transaction);

                if (affected <= 0)
                {
                    await transaction.RollbackAsync();
                    return false;
                }

                await DeleteChildrenAsync(connection, transaction, id);
                await transaction.CommitAsync();
                return true;
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
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
                entity.DiferePorVeiculo,
                entity.DiferePorMarcaPeca,
                entity.CreatedAt,
                entity.UpdatedAt
            };
        }

        private async Task PopulateChildrenAsync(NpgsqlConnection connection, IReadOnlyCollection<EstabelecimentoServico> itens)
        {
            if (itens.Count == 0)
            {
                return;
            }

            const string sqlVeiculos = @"
SELECT servico_id AS ServicoId,
       modelo_id AS CarroId,
       compativel,
       valor_centavos AS ValorCentavos
  FROM estabelecimento_servico_veiculos
 WHERE servico_id = ANY(@ServicoIds)
 ORDER BY servico_id ASC, modelo_id ASC;";

            const string sqlPecas = @"
SELECT id,
       servico_id AS ServicoId,
       parent_id AS ParentId,
       nome,
       valor_centavos AS ValorCentavos,
       ordem,
       created_at AS CreatedAt
  FROM estabelecimento_servico_marcas_peca
 WHERE servico_id = ANY(@ServicoIds)
 ORDER BY servico_id ASC, parent_id NULLS FIRST, ordem ASC, created_at ASC;";

            var servicoIds = itens.Select(item => item.Id).Distinct().ToArray();
            var vehicleRows = (await connection.QueryAsync<VehicleConfigRow>(sqlVeiculos, new { ServicoIds = servicoIds })).ToArray();
            var pieceRows = (await connection.QueryAsync<PieceRow>(sqlPecas, new { ServicoIds = servicoIds })).ToArray();

            var vehiclesByService = vehicleRows
                .GroupBy(row => row.ServicoId)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyCollection<EstabelecimentoServicoVeiculoConfig>)group
                        .Select(row => new EstabelecimentoServicoVeiculoConfig
                        {
                            CarroId = row.CarroId,
                            Compativel = row.Compativel,
                            ValorCentavos = row.ValorCentavos
                        })
                        .ToArray());

            var piecesByService = pieceRows
                .GroupBy(row => row.ServicoId)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyCollection<EstabelecimentoServicoMarcaPecaNode>)BuildPieceNodes(group));

            foreach (var item in itens)
            {
                item.VeiculoConfigs = vehiclesByService.TryGetValue(item.Id, out var veiculos)
                    ? veiculos.ToList()
                    : new List<EstabelecimentoServicoVeiculoConfig>();

                item.MarcasPeca = piecesByService.TryGetValue(item.Id, out var pecas)
                    ? pecas.ToList()
                    : new List<EstabelecimentoServicoMarcaPecaNode>();
            }
        }

        private static IReadOnlyCollection<EstabelecimentoServicoMarcaPecaNode> BuildPieceNodes(IEnumerable<PieceRow> rows)
        {
            var orderedRows = rows.ToArray();
            var roots = orderedRows
                .Where(row => !row.ParentId.HasValue)
                .OrderBy(row => row.Ordem)
                .ThenBy(row => row.CreatedAt)
                .Select(root => new EstabelecimentoServicoMarcaPecaNode
                {
                    Id = root.Id,
                    Nome = root.Nome ?? string.Empty,
                    ValorCentavos = root.ValorCentavos,
                    Variantes = orderedRows
                        .Where(row => row.ParentId == root.Id)
                        .OrderBy(row => row.Ordem)
                        .ThenBy(row => row.CreatedAt)
                        .Select(child => new EstabelecimentoServicoMarcaPecaVariante
                        {
                            Id = child.Id,
                            Nome = child.Nome ?? string.Empty,
                            ValorCentavos = child.ValorCentavos
                        })
                        .ToList()
                })
                .ToArray();

            return roots;
        }

        private async Task InsertChildrenAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            EstabelecimentoServico entity,
            DateTime timestamp)
        {
            await InsertVehicleConfigsAsync(connection, transaction, entity, timestamp);
            await InsertMarcaPecaAsync(connection, transaction, entity, timestamp);
        }

        private static async Task DeleteChildrenAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            Guid servicoId)
        {
            const string sqlDeletePecas = @"
DELETE FROM estabelecimento_servico_marcas_peca
 WHERE servico_id = @ServicoId;";

            const string sqlDeleteVeiculos = @"
DELETE FROM estabelecimento_servico_veiculos
 WHERE servico_id = @ServicoId;";

            await connection.ExecuteAsync(sqlDeletePecas, new { ServicoId = servicoId }, transaction);
            await connection.ExecuteAsync(sqlDeleteVeiculos, new { ServicoId = servicoId }, transaction);
        }

        private static async Task InsertVehicleConfigsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            EstabelecimentoServico entity,
            DateTime timestamp)
        {
            if (!entity.DiferePorVeiculo || entity.VeiculoConfigs.Count == 0)
            {
                return;
            }

            const string sql = @"
INSERT INTO estabelecimento_servico_veiculos (
    servico_id,
    modelo_id,
    compativel,
    valor_centavos,
    created_at,
    updated_at
) VALUES (
    @ServicoId,
    @ModeloId,
    @Compativel,
    @ValorCentavos,
    @CreatedAt,
    @UpdatedAt
);";

            var rows = entity.VeiculoConfigs.Select(config => new
            {
                ServicoId = entity.Id,
                ModeloId = config.CarroId,
                config.Compativel,
                config.ValorCentavos,
                CreatedAt = timestamp,
                UpdatedAt = timestamp
            });

            await connection.ExecuteAsync(sql, rows, transaction);
        }

        private static async Task InsertMarcaPecaAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            EstabelecimentoServico entity,
            DateTime timestamp)
        {
            if (!entity.DiferePorMarcaPeca || entity.MarcasPeca.Count == 0)
            {
                return;
            }

            const string sql = @"
INSERT INTO estabelecimento_servico_marcas_peca (
    id,
    servico_id,
    parent_id,
    nome,
    valor_centavos,
    ordem,
    created_at,
    updated_at
) VALUES (
    @Id,
    @ServicoId,
    @ParentId,
    @Nome,
    @ValorCentavos,
    @Ordem,
    @CreatedAt,
    @UpdatedAt
);";

            var rows = new List<object>();

            for (var i = 0; i < entity.MarcasPeca.Count; i++)
            {
                var marca = entity.MarcasPeca[i];
                if (marca.Id == Guid.Empty)
                {
                    marca.Id = Guid.NewGuid();
                }

                rows.Add(new
                {
                    marca.Id,
                    ServicoId = entity.Id,
                    ParentId = (Guid?)null,
                    marca.Nome,
                    marca.ValorCentavos,
                    Ordem = i + 1,
                    CreatedAt = timestamp,
                    UpdatedAt = timestamp
                });

                for (var j = 0; j < marca.Variantes.Count; j++)
                {
                    var variante = marca.Variantes[j];
                    if (variante.Id == Guid.Empty)
                    {
                        variante.Id = Guid.NewGuid();
                    }

                    rows.Add(new
                    {
                        variante.Id,
                        ServicoId = entity.Id,
                        ParentId = (Guid?)marca.Id,
                        variante.Nome,
                        variante.ValorCentavos,
                        Ordem = j + 1,
                        CreatedAt = timestamp,
                        UpdatedAt = timestamp
                    });
                }
            }

            await connection.ExecuteAsync(sql, rows, transaction);
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
                DiferePorVeiculo = row.DiferePorVeiculo,
                DiferePorMarcaPeca = row.DiferePorMarcaPeca,
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
            public bool DiferePorVeiculo { get; set; }
            public bool DiferePorMarcaPeca { get; set; }
            public DateTime CreatedAt { get; set; }
            public DateTime UpdatedAt { get; set; }
            public DateTime? DeletedAt { get; set; }
        }

        private sealed class VehicleConfigRow
        {
            public Guid ServicoId { get; set; }
            public Guid CarroId { get; set; }
            public bool Compativel { get; set; }
            public long? ValorCentavos { get; set; }
        }

        private sealed class PieceRow
        {
            public Guid Id { get; set; }
            public Guid ServicoId { get; set; }
            public Guid? ParentId { get; set; }
            public string? Nome { get; set; }
            public long? ValorCentavos { get; set; }
            public int Ordem { get; set; }
            public DateTime CreatedAt { get; set; }
        }
    }
}
