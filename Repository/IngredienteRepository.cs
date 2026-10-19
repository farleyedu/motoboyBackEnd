using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using APIBack.DTOs.Inteligencia;
using APIBack.Repository.Interface;
using APIBack.Service;
using Dapper;
using Npgsql;

namespace APIBack.Repository
{
    public sealed class IngredienteRepository : IIngredienteRepository
    {
        // Linhas de insumo com o custo vigente (rn = 1) e o anterior (rn = 2). Custo desconhecido = nenhuma linha em insumo_custo.
        private const string BaseCte = @"
WITH ranked AS (
    SELECT c.id, c.insumo_id, c.quantidade_compra, c.unidade_compra, c.valor_compra, c.custo_base, c.gratuito, c.origem,
           c.motivo, c.documento_id, c.vigencia_inicio, c.criado_por,
           ROW_NUMBER() OVER (PARTITION BY c.insumo_id ORDER BY c.vigencia_inicio DESC, c.criado_em DESC) AS rn
      FROM insumo_custo c
     WHERE c.estabelecimento_id = @Est),
base AS (
    SELECT i.id AS Id, i.nome AS Nome, i.unidade AS Unidade, i.categoria AS Categoria, i.observacao AS Observacao,
           i.foto_url AS FotoUrl, i.foto_credito AS FotoCredito, i.ativo AS Ativo, i.versao AS Versao,
           i.criado_em AS CriadoEm, i.atualizado_em AS AtualizadoEm,
           a.id AS CustoId, a.quantidade_compra AS CustoQuantidade, a.unidade_compra AS CustoUnidade, a.valor_compra AS CustoValor,
           a.custo_base AS CustoBase, a.gratuito AS CustoGratuito, a.origem AS CustoOrigem, a.motivo AS CustoMotivo,
           a.documento_id AS CustoDocumento, a.vigencia_inicio AS CustoVigencia, u.nome AS CustoAutor,
           p.custo_base AS AnteriorCusto, p.vigencia_inicio AS AnteriorDesde,
           (a.id IS NOT NULL AND p.id IS NOT NULL AND p.custo_base <> a.custo_base
              AND a.vigencia_inicio >= now() - make_interval(days => @Dias)) AS PrecoAlterado
      FROM insumo i
      LEFT JOIN ranked a ON a.insumo_id = i.id AND a.rn = 1
      LEFT JOIN ranked p ON p.insumo_id = i.id AND p.rn = 2
      LEFT JOIN usuario u ON u.id = a.criado_por
     WHERE i.estabelecimento_id = @Est)";

        private readonly NpgsqlDataSource _dataSource;

        public IngredienteRepository(NpgsqlDataSource dataSource)
        {
            _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        }

        public async Task<IngredienteListaDto> ListAsync(Guid estabelecimentoId, string filtro, string? busca, int page, int pageSize)
        {
            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, 100);
            var term = string.IsNullOrWhiteSpace(busca) ? null : $"%{busca.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%";
            var parameters = new
            {
                Est = estabelecimentoId, Dias = IngredienteRules.PrecoAlteradoDias, Term = term,
                Limit = pageSize, Offset = (page - 1) * pageSize,
            };
            var where = filtro switch
            {
                "sem_custo" => "Ativo AND CustoId IS NULL",
                "preco_alterado" => "Ativo AND PrecoAlterado",
                "inativos" => "NOT Ativo",
                _ => "Ativo",
            };
            await using var connection = await _dataSource.OpenConnectionAsync();
            var counts = await connection.QuerySingleAsync<Counts>(BaseCte + @"
SELECT COUNT(*) FILTER (WHERE Ativo) AS Todos,
       COUNT(*) FILTER (WHERE Ativo AND CustoId IS NULL) AS SemCusto,
       COUNT(*) FILTER (WHERE Ativo AND PrecoAlterado) AS PrecoAlterado,
       COUNT(*) FILTER (WHERE NOT Ativo) AS Inativos
  FROM base;", parameters);
            var filter = $"{where} AND (@Term::text IS NULL OR Nome ILIKE @Term ESCAPE '\\' OR COALESCE(Categoria, '') ILIKE @Term ESCAPE '\\')";
            var total = await connection.ExecuteScalarAsync<int>(BaseCte + $" SELECT COUNT(*) FROM base WHERE {filter};", parameters);
            var rows = await connection.QueryAsync<Row>(BaseCte + $@"
SELECT * FROM base WHERE {filter} ORDER BY lower(Nome), Id LIMIT @Limit OFFSET @Offset;", parameters);
            return new IngredienteListaDto
            {
                Itens = rows.Select(ToDto).ToList(), Total = total, Page = page, PageSize = pageSize,
                Contagens = new IngredienteContagensDto { Todos = counts.Todos, SemCusto = counts.SemCusto, PrecoAlterado = counts.PrecoAlterado, Inativos = counts.Inativos },
            };
        }

        public async Task<IngredienteDto?> GetAsync(Guid estabelecimentoId, Guid id)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            return await GetAsync(connection, estabelecimentoId, id);
        }

        private static async Task<IngredienteDto?> GetAsync(NpgsqlConnection connection, Guid estabelecimentoId, Guid id)
        {
            var row = await connection.QuerySingleOrDefaultAsync<Row>(BaseCte + " SELECT * FROM base WHERE Id = @Id;",
                new { Est = estabelecimentoId, Id = id, Dias = IngredienteRules.PrecoAlteradoDias });
            return row == null ? null : ToDto(row);
        }

        public async Task<IReadOnlyList<string>> NamesAsync(Guid estabelecimentoId, Guid? exceto)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            var names = await connection.QueryAsync<string>(
                "SELECT nome FROM insumo WHERE estabelecimento_id = @Est AND (@Exceto::uuid IS NULL OR id <> @Exceto);",
                new { Est = estabelecimentoId, Exceto = exceto });
            return names.ToList();
        }

        public async Task<Guid> CreateAsync(Guid estabelecimentoId, int userId, IngredienteInput input, CustoCalculado? custo)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            if (input.ChaveIdempotencia != null)
            {
                var existing = await connection.ExecuteScalarAsync<Guid?>(
                    "SELECT id FROM insumo WHERE estabelecimento_id = @Est AND chave_idempotencia = @Chave;",
                    new { Est = estabelecimentoId, Chave = input.ChaveIdempotencia });
                if (existing.HasValue) return existing.Value;
            }
            await using var transaction = await connection.BeginTransactionAsync();
            var id = Guid.NewGuid();
            await connection.ExecuteAsync(@"
INSERT INTO insumo (id, estabelecimento_id, nome, unidade, categoria, observacao, foto_url, foto_credito, chave_idempotencia, criado_por)
VALUES (@Id, @Est, @Nome, @Unidade, @Categoria, @Observacao, @FotoUrl, @FotoCredito, @Chave, @User);",
                new { Id = id, Est = estabelecimentoId, input.Nome, input.Unidade, input.Categoria, input.Observacao, input.FotoUrl, input.FotoCredito, Chave = input.ChaveIdempotencia, User = userId },
                transaction);
            if (custo != null) await InsertCustoAsync(connection, transaction, estabelecimentoId, userId, id, custo);
            await transaction.CommitAsync();
            return id;
        }

        public async Task<bool> UpdateAsync(Guid estabelecimentoId, Guid id, IngredienteInput input)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            var affected = await connection.ExecuteAsync(@"
UPDATE insumo SET nome = @Nome, categoria = @Categoria, observacao = @Observacao, foto_url = @FotoUrl,
       foto_credito = @FotoCredito, versao = versao + 1, atualizado_em = now()
 WHERE id = @Id AND estabelecimento_id = @Est AND versao = @Versao;",
                new { Id = id, Est = estabelecimentoId, input.Nome, input.Categoria, input.Observacao, input.FotoUrl, input.FotoCredito, Versao = input.Versao });
            if (affected > 0) return true;
            var exists = await connection.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM insumo WHERE id = @Id AND estabelecimento_id = @Est);", new { Id = id, Est = estabelecimentoId });
            if (!exists) return false;
            throw new DeliveryDomainException(409, "INGREDIENTE_CONFLITO", "Este ingrediente foi alterado por outra pessoa. Recarregue antes de salvar.");
        }

        public async Task<Guid?> AddCustoAsync(Guid estabelecimentoId, int userId, Guid id, CustoCalculado custo)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var exists = await connection.ExecuteScalarAsync<bool>(
                "SELECT EXISTS (SELECT 1 FROM insumo WHERE id = @Id AND estabelecimento_id = @Est) ;", new { Id = id, Est = estabelecimentoId }, transaction);
            if (!exists) return null;
            if (custo.ChaveIdempotencia != null)
            {
                var existing = await connection.ExecuteScalarAsync<Guid?>(
                    "SELECT id FROM insumo_custo WHERE estabelecimento_id = @Est AND chave_idempotencia = @Chave;",
                    new { Est = estabelecimentoId, Chave = custo.ChaveIdempotencia }, transaction);
                if (existing.HasValue) return existing.Value;
            }
            var custoId = await InsertCustoAsync(connection, transaction, estabelecimentoId, userId, id, custo);
            await connection.ExecuteAsync("UPDATE insumo SET atualizado_em = now() WHERE id = @Id AND estabelecimento_id = @Est;", new { Id = id, Est = estabelecimentoId }, transaction);
            await transaction.CommitAsync();
            return custoId;
        }

        private static async Task<Guid> InsertCustoAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid estabelecimentoId, int userId, Guid insumoId, CustoCalculado custo)
        {
            var id = Guid.NewGuid();
            await connection.ExecuteAsync(@"
INSERT INTO insumo_custo (id, estabelecimento_id, insumo_id, quantidade_compra, unidade_compra, valor_compra, custo_base, gratuito, origem, motivo, criado_por, chave_idempotencia)
VALUES (@Id, @Est, @Insumo, @Quantidade, @Unidade, @Valor, @CustoBase, @Gratuito, 'informado', @Motivo, @User, @Chave);",
                new
                {
                    Id = id, Est = estabelecimentoId, Insumo = insumoId, Quantidade = custo.QuantidadeCompra, Unidade = custo.UnidadeCompra, Valor = custo.ValorCompra,
                    custo.CustoBase, custo.Gratuito, custo.Motivo, User = userId, Chave = custo.ChaveIdempotencia,
                }, transaction);
            return id;
        }

        public async Task<IngredienteCustosDto?> ListCustosAsync(Guid estabelecimentoId, Guid id, int page, int pageSize)
        {
            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, 100);
            await using var connection = await _dataSource.OpenConnectionAsync();
            var exists = await connection.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM insumo WHERE id = @Id AND estabelecimento_id = @Est);", new { Id = id, Est = estabelecimentoId });
            if (!exists) return null;
            var total = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM insumo_custo WHERE insumo_id = @Id AND estabelecimento_id = @Est;", new { Id = id, Est = estabelecimentoId });
            var rows = await connection.QueryAsync<CustoRow>(@"
SELECT c.id AS Id, c.quantidade_compra AS Quantidade, c.unidade_compra AS Unidade, c.valor_compra AS Valor, c.custo_base AS CustoBase,
       c.gratuito AS Gratuito, c.origem AS Origem, c.motivo AS Motivo, c.documento_id AS Documento, c.vigencia_inicio AS Vigencia, u.nome AS Autor
  FROM insumo_custo c LEFT JOIN usuario u ON u.id = c.criado_por
 WHERE c.insumo_id = @Id AND c.estabelecimento_id = @Est
 ORDER BY c.vigencia_inicio DESC, c.criado_em DESC LIMIT @Limit OFFSET @Offset;",
                new { Id = id, Est = estabelecimentoId, Limit = pageSize, Offset = (page - 1) * pageSize });
            return new IngredienteCustosDto
            {
                Itens = rows.Select(row => new IngredienteCustoDto
                {
                    Id = row.Id, QuantidadeCompra = row.Quantidade, UnidadeCompra = row.Unidade, ValorCompra = row.Valor, CustoBase = row.CustoBase,
                    Gratuito = row.Gratuito, Origem = row.Origem, Motivo = row.Motivo, DocumentoId = row.Documento, VigenciaInicio = row.Vigencia, CriadoPorNome = row.Autor,
                }).ToList(),
                Total = total, Page = page, PageSize = pageSize,
            };
        }

        public async Task<bool> SetAtivoAsync(Guid estabelecimentoId, Guid id, bool ativo)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            return await connection.ExecuteAsync(
                "UPDATE insumo SET ativo = @Ativo, versao = versao + 1, atualizado_em = now() WHERE id = @Id AND estabelecimento_id = @Est;",
                new { Id = id, Est = estabelecimentoId, Ativo = ativo }) > 0;
        }

        private static IngredienteDto ToDto(Row row)
        {
            IngredienteCustoDto? atual = null;
            VariacaoDto? variacao = null;
            if (row.CustoId.HasValue)
            {
                atual = new IngredienteCustoDto
                {
                    Id = row.CustoId.Value, QuantidadeCompra = row.CustoQuantidade ?? 0, UnidadeCompra = row.CustoUnidade ?? row.Unidade, ValorCompra = row.CustoValor ?? 0,
                    CustoBase = row.CustoBase ?? 0, Gratuito = row.CustoGratuito ?? false, Origem = row.CustoOrigem ?? "informado", Motivo = row.CustoMotivo,
                    DocumentoId = row.CustoDocumento, VigenciaInicio = row.CustoVigencia ?? row.CriadoEm, CriadoPorNome = row.CustoAutor,
                };
                variacao = IngredienteRules.Variation(atual.CustoBase, row.AnteriorCusto, row.AnteriorDesde);
            }
            return new IngredienteDto
            {
                Id = row.Id, Nome = row.Nome, Unidade = row.Unidade, Categoria = row.Categoria, Observacao = row.Observacao, FotoUrl = row.FotoUrl,
                FotoCredito = row.FotoCredito, Ativo = row.Ativo, Versao = row.Versao, CriadoEm = row.CriadoEm, AtualizadoEm = row.AtualizadoEm,
                CustoAtual = atual, Variacao = variacao, PrecoAlterado = row.PrecoAlterado,
            };
        }

        private sealed class Counts
        {
            public int Todos { get; set; }
            public int SemCusto { get; set; }
            public int PrecoAlterado { get; set; }
            public int Inativos { get; set; }
        }

        private sealed class Row
        {
            public Guid Id { get; set; }
            public string Nome { get; set; } = string.Empty;
            public string Unidade { get; set; } = "g";
            public string? Categoria { get; set; }
            public string? Observacao { get; set; }
            public string? FotoUrl { get; set; }
            public string? FotoCredito { get; set; }
            public bool Ativo { get; set; }
            public int Versao { get; set; }
            public DateTimeOffset CriadoEm { get; set; }
            public DateTimeOffset AtualizadoEm { get; set; }
            public Guid? CustoId { get; set; }
            public decimal? CustoQuantidade { get; set; }
            public string? CustoUnidade { get; set; }
            public decimal? CustoValor { get; set; }
            public decimal? CustoBase { get; set; }
            public bool? CustoGratuito { get; set; }
            public string? CustoOrigem { get; set; }
            public string? CustoMotivo { get; set; }
            public Guid? CustoDocumento { get; set; }
            public DateTimeOffset? CustoVigencia { get; set; }
            public string? CustoAutor { get; set; }
            public decimal? AnteriorCusto { get; set; }
            public DateTimeOffset? AnteriorDesde { get; set; }
            public bool PrecoAlterado { get; set; }
        }

        private sealed class CustoRow
        {
            public Guid Id { get; set; }
            public decimal Quantidade { get; set; }
            public string Unidade { get; set; } = string.Empty;
            public decimal Valor { get; set; }
            public decimal CustoBase { get; set; }
            public bool Gratuito { get; set; }
            public string Origem { get; set; } = "informado";
            public string? Motivo { get; set; }
            public Guid? Documento { get; set; }
            public DateTimeOffset Vigencia { get; set; }
            public string? Autor { get; set; }
        }
    }
}
