using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Automation.Dtos;
using APIBack.Automation.Interfaces;
using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace APIBack.Automation.Infra
{
    public class SqlGaragemVeiculoRepository : IGaragemVeiculoRepository
    {
        private const string UniqueViolation = "23505";
        private readonly string _connectionString;
        private static bool _schemaEnsured;
        private static readonly SemaphoreSlim SchemaLock = new(1, 1);

        public SqlGaragemVeiculoRepository(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection")
                ?? configuration["ConnectionStrings:DefaultConnection"]
                ?? throw new InvalidOperationException("Connection string 'DefaultConnection' nao encontrada.");
        }

        public async Task<(IReadOnlyList<GarageVehicleAdminDto> Items, int Total)> ListarAsync(
            Guid idEstabelecimento,
            string? status,
            string? categoria,
            string? search,
            int page,
            int pageSize)
        {
            await EnsureSchemaAsync();

            var pageNormalizado = page < 1 ? 1 : page;
            var pageSizeNormalizado = Math.Clamp(pageSize <= 0 ? 20 : pageSize, 1, 200);
            var offset = (pageNormalizado - 1) * pageSizeNormalizado;

            const string sql = @"
SELECT COUNT(*)::int
  FROM garagem_veiculo v
 WHERE v.id_estabelecimento = @IdEstabelecimento
   AND (@Status IS NULL OR v.status = @Status)
   AND (@Categoria IS NULL OR v.categoria = @Categoria)
    AND (
         @Search IS NULL OR @Search = '' OR
         COALESCE(v.titulo, '') ILIKE '%' || @Search || '%' OR
         COALESCE(v.marca, '') ILIKE '%' || @Search || '%' OR
         COALESCE(v.modelo, '') ILIKE '%' || @Search || '%' OR
         COALESCE(v.cidade, '') ILIKE '%' || @Search || '%' OR
         COALESCE(v.codigo_estoque, '') ILIKE '%' || @Search || '%' OR
         COALESCE(v.placa, '') ILIKE '%' || @Search || '%' OR
         COALESCE(v.slug, '') ILIKE '%' || @Search || '%'
    );

SELECT v.id,
       v.id_estabelecimento AS IdEstabelecimento,
       v.slug,
       v.titulo,
       v.marca,
       v.modelo,
       v.categoria,
       v.ano_fabricacao AS AnoFabricacao,
       v.ano_modelo AS AnoModelo,
       v.preco,
       v.preco_anterior AS PrecoAnterior,
       v.km,
       v.combustivel,
       v.cambio,
       v.cor,
       v.placa AS Placa,
       v.cidade,
       v.carroceria,
       v.portas,
       v.assentos,
       v.status,
       v.tipo_veiculo AS TipoVeiculo,
       v.destaque,
       v.label_destaque AS LabelDestaque,
       v.codigo_estoque AS CodigoEstoque,
       v.tracao,
       v.descricao,
       v.opcionais::text AS OpcionaisJson,
       v.condicoes::text AS CondicoesJson
  FROM garagem_veiculo v
 WHERE v.id_estabelecimento = @IdEstabelecimento
   AND (@Status IS NULL OR v.status = @Status)
   AND (@Categoria IS NULL OR v.categoria = @Categoria)
    AND (
         @Search IS NULL OR @Search = '' OR
         COALESCE(v.titulo, '') ILIKE '%' || @Search || '%' OR
         COALESCE(v.marca, '') ILIKE '%' || @Search || '%' OR
         COALESCE(v.modelo, '') ILIKE '%' || @Search || '%' OR
         COALESCE(v.cidade, '') ILIKE '%' || @Search || '%' OR
         COALESCE(v.codigo_estoque, '') ILIKE '%' || @Search || '%' OR
         COALESCE(v.placa, '') ILIKE '%' || @Search || '%' OR
         COALESCE(v.slug, '') ILIKE '%' || @Search || '%'
    )
  ORDER BY v.destaque DESC, COALESCE(v.data_atualizacao, v.data_criacao) DESC, v.id DESC
  LIMIT @Limit OFFSET @Offset;";

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            using var multi = await connection.QueryMultipleAsync(sql, new
            {
                IdEstabelecimento = idEstabelecimento,
                Status = LimparFiltro(status),
                Categoria = LimparFiltro(categoria),
                Search = LimparFiltro(search),
                Limit = pageSizeNormalizado,
                Offset = offset
            });

            var total = await multi.ReadFirstAsync<int>();
            var rows = (await multi.ReadAsync<GarageVehicleRow>()).ToList();
            var items = await MapAdminVehiclesAsync(connection, rows);
            return (items, total);
        }

        public async Task<GarageVehicleAdminDto?> ObterPorIdAsync(
            Guid id,
            Guid? idEstabelecimento = null,
            bool somenteDisponiveis = false)
        {
            await EnsureSchemaAsync();

            const string sql = @"
SELECT v.id,
       v.id_estabelecimento AS IdEstabelecimento,
       v.slug,
       v.titulo,
       v.marca,
       v.modelo,
       v.categoria,
       v.ano_fabricacao AS AnoFabricacao,
       v.ano_modelo AS AnoModelo,
       v.preco,
       v.preco_anterior AS PrecoAnterior,
       v.km,
       v.combustivel,
       v.cambio,
       v.cor,
       v.placa AS Placa,
       v.cidade,
       v.carroceria,
       v.portas,
       v.assentos,
       v.status,
       v.tipo_veiculo AS TipoVeiculo,
       v.destaque,
       v.label_destaque AS LabelDestaque,
       v.codigo_estoque AS CodigoEstoque,
       v.tracao,
       v.descricao,
       v.opcionais::text AS OpcionaisJson,
       v.condicoes::text AS CondicoesJson
  FROM garagem_veiculo v
 WHERE v.id = @Id
   AND (@IdEstabelecimento IS NULL OR v.id_estabelecimento = @IdEstabelecimento)
   AND (@SomenteDisponiveis = FALSE OR v.status = 'disponivel')
 LIMIT 1;";

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var row = await connection.QueryFirstOrDefaultAsync<GarageVehicleRow>(sql, new
            {
                Id = id,
                IdEstabelecimento = idEstabelecimento,
                SomenteDisponiveis = somenteDisponiveis
            });

            if (row == null)
            {
                return null;
            }

            var mapped = await MapAdminVehiclesAsync(connection, new[] { row });
            return mapped.FirstOrDefault();
        }

        public async Task<GarageVehicleAdminDto> CriarAsync(Guid idEstabelecimento, CreateGarageVehicleRequest request)
        {
            await EnsureSchemaAsync();

            var id = Guid.NewGuid();

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            try
            {
                var codigoEstoque = await ResolveCodigoEstoqueAsync(
                    connection,
                    transaction,
                    idEstabelecimento,
                    request.CodigoEstoque,
                    request.Marca,
                    request.Modelo,
                    request.AnoModelo,
                    null);

                var slug = await GerarSlugUnicoAsync(
                    connection,
                    transaction,
                    request.Marca ?? string.Empty,
                    request.Modelo ?? string.Empty,
                    request.AnoModelo ?? 0,
                    null);

                const string sql = @"
INSERT INTO garagem_veiculo (
    id, id_estabelecimento, slug, titulo, marca, modelo, categoria, ano_fabricacao,
    ano_modelo, preco, preco_anterior, km, combustivel, cambio, cor, placa, cidade,
    carroceria, portas, assentos, status, tipo_veiculo, destaque, label_destaque,
    codigo_estoque, tracao, descricao, opcionais, condicoes, data_criacao, data_atualizacao
)
VALUES (
    @Id, @IdEstabelecimento, @Slug, @Titulo, @Marca, @Modelo, @Categoria, @AnoFabricacao,
    @AnoModelo, @Preco, @PrecoAnterior, @Km, @Combustivel, @Cambio, @Cor, @Placa, @Cidade,
    @Carroceria, @Portas, @Assentos, @Status, @TipoVeiculo, @Destaque, @LabelDestaque,
    @CodigoEstoque, @Tracao, @Descricao, @OpcionaisJson::jsonb, @CondicoesJson::jsonb, NOW(), NOW()
);";

                await connection.ExecuteAsync(sql, BuildCreateVehicleParameters(id, idEstabelecimento, slug, codigoEstoque, request), transaction);
                await ReplacePhotosAsync(connection, transaction, id, request.Fotos);

                await transaction.CommitAsync();

                return await ObterPorIdAsync(id, idEstabelecimento)
                    ?? throw new InvalidOperationException("Nao foi possivel carregar o veiculo criado.");
            }
            catch (PostgresException ex) when (ex.SqlState == UniqueViolation)
            {
                await transaction.RollbackAsync();
                throw new InvalidOperationException(ResolveUniqueConstraintMessage(ex));
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }

        public async Task<GarageVehicleAdminDto?> AtualizarAsync(Guid id, Guid idEstabelecimento, UpsertGarageVehicleRequest request)
        {
            await EnsureSchemaAsync();

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            try
            {
                var existente = await connection.QueryFirstOrDefaultAsync<GarageVehicleIdentityRow>(@"
SELECT id, id_estabelecimento AS IdEstabelecimento
  FROM garagem_veiculo
 WHERE id = @Id
   AND id_estabelecimento = @IdEstabelecimento
 LIMIT 1
 FOR UPDATE;",
                    new { Id = id, IdEstabelecimento = idEstabelecimento },
                    transaction);

                if (existente == null)
                {
                    await transaction.RollbackAsync();
                    return null;
                }

                await GarantirCodigoEstoqueUnicoAsync(connection, transaction, idEstabelecimento, request.CodigoEstoque, id);
                var slug = await GerarSlugUnicoAsync(connection, transaction, request.Marca, request.Modelo, request.AnoModelo, id);

                const string sql = @"
UPDATE garagem_veiculo
   SET slug = @Slug,
       titulo = @Titulo,
       marca = @Marca,
       modelo = @Modelo,
       categoria = @Categoria,
       ano_fabricacao = @AnoFabricacao,
       ano_modelo = @AnoModelo,
       preco = @Preco,
       preco_anterior = @PrecoAnterior,
       km = @Km,
       combustivel = @Combustivel,
       cambio = @Cambio,
       cor = @Cor,
       placa = @Placa,
       cidade = @Cidade,
       carroceria = @Carroceria,
       portas = @Portas,
       assentos = @Assentos,
       status = @Status,
       tipo_veiculo = @TipoVeiculo,
       destaque = @Destaque,
       label_destaque = @LabelDestaque,
       codigo_estoque = @CodigoEstoque,
       tracao = @Tracao,
       descricao = @Descricao,
       opcionais = @OpcionaisJson::jsonb,
       condicoes = @CondicoesJson::jsonb,
       data_atualizacao = NOW()
 WHERE id = @Id
   AND id_estabelecimento = @IdEstabelecimento;";

                await connection.ExecuteAsync(sql, BuildVehicleParameters(id, idEstabelecimento, slug, request), transaction);
                await ReplacePhotosAsync(connection, transaction, id, request.Fotos);

                await transaction.CommitAsync();

                return await ObterPorIdAsync(id, idEstabelecimento);
            }
            catch (PostgresException ex) when (ex.SqlState == UniqueViolation)
            {
                await transaction.RollbackAsync();
                throw new InvalidOperationException(ResolveUniqueConstraintMessage(ex));
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }

        public async Task<bool> RemoverAsync(Guid id, Guid? idEstabelecimento = null)
        {
            await EnsureSchemaAsync();

            const string sql = @"
DELETE FROM garagem_veiculo
 WHERE id = @Id
   AND (@IdEstabelecimento IS NULL OR id_estabelecimento = @IdEstabelecimento);";

            await using var connection = new NpgsqlConnection(_connectionString);
            return await connection.ExecuteAsync(sql, new
            {
                Id = id,
                IdEstabelecimento = idEstabelecimento
            }) > 0;
        }

        public async Task<bool> AtualizarStatusAsync(Guid id, string status, Guid? idEstabelecimento = null)
        {
            await EnsureSchemaAsync();

            const string sql = @"
UPDATE garagem_veiculo
   SET status = @Status,
       data_atualizacao = NOW()
 WHERE id = @Id
   AND (@IdEstabelecimento IS NULL OR id_estabelecimento = @IdEstabelecimento);";

            await using var connection = new NpgsqlConnection(_connectionString);
            return await connection.ExecuteAsync(sql, new
            {
                Id = id,
                Status = status,
                IdEstabelecimento = idEstabelecimento
            }) > 0;
        }

        public async Task<bool> AtualizarDestaqueAsync(Guid id, bool destaque, string? label, Guid? idEstabelecimento = null)
        {
            await EnsureSchemaAsync();

            const string sql = @"
UPDATE garagem_veiculo
   SET destaque = @Destaque,
       label_destaque = CASE WHEN @Destaque THEN @Label ELSE NULL END,
       data_atualizacao = NOW()
 WHERE id = @Id
   AND (@IdEstabelecimento IS NULL OR id_estabelecimento = @IdEstabelecimento);";

            await using var connection = new NpgsqlConnection(_connectionString);
            return await connection.ExecuteAsync(sql, new
            {
                Id = id,
                Destaque = destaque,
                Label = LimparFiltro(label),
                IdEstabelecimento = idEstabelecimento
            }) > 0;
        }

        public async Task<GarageVehicleShowcaseResponseDto> ObterVitrineAsync(Guid idEstabelecimento, string? categoria, string? search)
        {
            await EnsureSchemaAsync();

            const string sql = @"
SELECT v.id,
       v.id_estabelecimento AS IdEstabelecimento,
       v.slug,
       v.titulo,
       v.marca,
       v.modelo,
       v.categoria,
       v.ano_fabricacao AS AnoFabricacao,
       v.ano_modelo AS AnoModelo,
       v.preco,
       v.preco_anterior AS PrecoAnterior,
       v.km,
       v.combustivel,
       v.cambio,
       v.cor,
       v.cidade,
       v.carroceria,
       v.portas,
       v.assentos,
       v.status,
       v.tipo_veiculo AS TipoVeiculo,
       v.destaque,
       v.label_destaque AS LabelDestaque,
       v.codigo_estoque AS CodigoEstoque,
       v.tracao,
       v.descricao,
       v.opcionais::text AS OpcionaisJson,
       v.condicoes::text AS CondicoesJson
  FROM garagem_veiculo v
 WHERE v.id_estabelecimento = @IdEstabelecimento
   AND v.status = 'disponivel'
   AND (@Categoria IS NULL OR v.categoria = @Categoria)
   AND (
        @Search IS NULL OR @Search = '' OR
        COALESCE(v.titulo, '') ILIKE '%' || @Search || '%' OR
        COALESCE(v.marca, '') ILIKE '%' || @Search || '%' OR
        COALESCE(v.modelo, '') ILIKE '%' || @Search || '%' OR
        COALESCE(v.cidade, '') ILIKE '%' || @Search || '%'
   )
 ORDER BY v.destaque DESC, COALESCE(v.data_atualizacao, v.data_criacao) DESC, v.id DESC;";

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var rows = (await connection.QueryAsync<GarageVehicleRow>(sql, new
            {
                IdEstabelecimento = idEstabelecimento,
                Categoria = LimparFiltro(categoria),
                Search = LimparFiltro(search)
            })).ToList();

            var items = await MapPublicVehiclesAsync(connection, rows);
            var destaques = items.Where(item => item.Featured).Take(8).ToList();

            return new GarageVehicleShowcaseResponseDto
            {
                Items = items,
                Destaques = destaques
            };
        }

        public async Task<GarageVehicleMetricsDto> ObterMetricasAsync(Guid idEstabelecimento)
        {
            await EnsureSchemaAsync();

            const string sql = @"
SELECT COUNT(*)::int AS Total,
       COUNT(*) FILTER (WHERE status = 'disponivel')::int AS Disponiveis,
       COUNT(*) FILTER (WHERE destaque = TRUE)::int AS EmDestaque,
       COALESCE(AVG(preco), 0)::numeric(14,2) AS TicketMedio
  FROM garagem_veiculo
 WHERE id_estabelecimento = @IdEstabelecimento;";

            await using var connection = new NpgsqlConnection(_connectionString);
            return await connection.QueryFirstAsync<GarageVehicleMetricsDto>(sql, new { IdEstabelecimento = idEstabelecimento });
        }

        public async Task<GarageVehicleDto?> ObterPorSlugPublicoAsync(Guid idEstabelecimento, string slug)
        {
            await EnsureSchemaAsync();

            const string sql = @"
SELECT v.id,
       v.id_estabelecimento AS IdEstabelecimento,
       v.slug,
       v.titulo,
       v.marca,
       v.modelo,
       v.categoria,
       v.ano_fabricacao AS AnoFabricacao,
       v.ano_modelo AS AnoModelo,
       v.preco,
       v.preco_anterior AS PrecoAnterior,
       v.km,
       v.combustivel,
       v.cambio,
       v.cor,
       v.cidade,
       v.carroceria,
       v.portas,
       v.assentos,
       v.status,
       v.tipo_veiculo AS TipoVeiculo,
       v.destaque,
       v.label_destaque AS LabelDestaque,
       v.codigo_estoque AS CodigoEstoque,
       v.tracao,
       v.descricao,
       v.opcionais::text AS OpcionaisJson,
       v.condicoes::text AS CondicoesJson
  FROM garagem_veiculo v
 WHERE v.id_estabelecimento = @IdEstabelecimento
   AND v.slug = @Slug
   AND v.status = 'disponivel'
 LIMIT 1;";

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var row = await connection.QueryFirstOrDefaultAsync<GarageVehicleRow>(sql, new
            {
                IdEstabelecimento = idEstabelecimento,
                Slug = slug.Trim().ToLowerInvariant()
            });

            if (row == null)
            {
                return null;
            }

            var items = await MapPublicVehiclesAsync(connection, new[] { row });
            return items.FirstOrDefault();
        }

        public async Task<string?> ObterVitrineBaseUrlAsync(Guid idEstabelecimento)
        {
            await EnsureSchemaAsync();

            const string sql = @"
SELECT vitrine_url_base
  FROM garagem_config
 WHERE id_estabelecimento = @IdEstabelecimento;";

            await using var connection = new NpgsqlConnection(_connectionString);
            var url = await connection.QueryFirstOrDefaultAsync<string?>(sql, new { IdEstabelecimento = idEstabelecimento });
            return string.IsNullOrWhiteSpace(url) ? null : url.TrimEnd('/');
        }

        public async Task AtualizarVitrineBaseUrlAsync(Guid idEstabelecimento, string url)
        {
            await EnsureSchemaAsync();

            const string sql = @"
INSERT INTO garagem_config (id_estabelecimento, vitrine_url_base, data_criacao, data_atualizacao)
VALUES (@IdEstabelecimento, @Url, NOW(), NOW())
ON CONFLICT (id_estabelecimento) DO UPDATE
   SET vitrine_url_base = EXCLUDED.vitrine_url_base,
       data_atualizacao = NOW();";

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.ExecuteAsync(sql, new
            {
                IdEstabelecimento = idEstabelecimento,
                Url = url.Trim().TrimEnd('/')
            });
        }

        private async Task<IReadOnlyList<GarageVehicleAdminDto>> MapAdminVehiclesAsync(
            NpgsqlConnection connection,
            IReadOnlyCollection<GarageVehicleRow> rows)
        {
            if (rows.Count == 0)
            {
                return Array.Empty<GarageVehicleAdminDto>();
            }

            var galleryByVehicle = await LoadGalleryByVehicleAsync(connection, rows);
            return rows.Select(row =>
            {
                var dto = new GarageVehicleAdminDto
                {
                    Plate = LimparFiltro(row.Placa)
                };

                PopulateVehicleDto(dto, row, galleryByVehicle);
                return dto;
            }).ToList();
        }

        private async Task<IReadOnlyList<GarageVehicleDto>> MapPublicVehiclesAsync(
            NpgsqlConnection connection,
            IReadOnlyCollection<GarageVehicleRow> rows)
        {
            if (rows.Count == 0)
            {
                return Array.Empty<GarageVehicleDto>();
            }

            var galleryByVehicle = await LoadGalleryByVehicleAsync(connection, rows);
            return rows.Select(row =>
            {
                var dto = new GarageVehicleDto();
                PopulateVehicleDto(dto, row, galleryByVehicle);
                return dto;
            }).ToList();
        }

        private async Task<IReadOnlyDictionary<Guid, List<string>>> LoadGalleryByVehicleAsync(
            NpgsqlConnection connection,
            IReadOnlyCollection<GarageVehicleRow> rows)
        {
            var ids = rows.Select(row => row.Id).ToArray();
            var photos = (await connection.QueryAsync<GarageVehiclePhotoRow>(@"
SELECT id_veiculo AS VehicleId,
       url,
       ordem
  FROM garagem_veiculo_foto
  WHERE id_veiculo = ANY(@Ids)
 ORDER BY id_veiculo ASC, ordem ASC;",
                new { Ids = ids }))
                .ToList();

            var galleryByVehicle = photos
                .GroupBy(photo => photo.VehicleId)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(photo => photo.Url ?? string.Empty).Where(url => !string.IsNullOrWhiteSpace(url)).ToList());

            return galleryByVehicle;
        }

        private static void PopulateVehicleDto(
            GarageVehicleDto dto,
            GarageVehicleRow row,
            IReadOnlyDictionary<Guid, List<string>> galleryByVehicle)
        {
            dto.Id = row.Id;
            dto.Slug = row.Slug ?? string.Empty;
            dto.Title = row.Titulo ?? string.Empty;
            dto.Brand = row.Marca ?? string.Empty;
            dto.Model = row.Modelo ?? string.Empty;
            dto.Category = NormalizeCategory(row.Categoria) ?? string.Empty;
            dto.Year = row.AnoFabricacao;
            dto.ModelYear = row.AnoModelo;
            dto.Price = row.Preco;
            dto.OldPrice = row.PrecoAnterior;
            dto.Km = row.Km ?? 0;
            dto.Fuel = row.Combustivel ?? string.Empty;
            dto.Transmission = row.Cambio ?? string.Empty;
            dto.Color = row.Cor ?? string.Empty;
            dto.City = row.Cidade ?? string.Empty;
            dto.Body = row.Carroceria ?? string.Empty;
            dto.Doors = row.Portas ?? 0;
            dto.Seats = row.Assentos ?? 0;
            dto.Status = NormalizeStatus(row.Status) ?? string.Empty;
            dto.Condition = NormalizeVehicleType(row.TipoVeiculo);
            dto.Featured = row.Destaque;
            dto.SpotlightLabel = LimparFiltro(row.LabelDestaque);
            dto.StockCode = row.CodigoEstoque ?? string.Empty;
            dto.DriveType = LimparFiltro(row.Tracao);
            dto.Description = row.Descricao ?? string.Empty;
            dto.Optionals = DeserializeStringList(row.OpcionaisJson);
            dto.Gallery = galleryByVehicle.TryGetValue(row.Id, out var gallery) ? gallery : new List<string>();
            dto.ConditionItems = DeserializeConditionItems(row.CondicoesJson);
        }

        private async Task ReplacePhotosAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            Guid idVeiculo,
            IReadOnlyCollection<string>? fotos)
        {
            await connection.ExecuteAsync("DELETE FROM garagem_veiculo_foto WHERE id_veiculo = @IdVeiculo;", new { IdVeiculo = idVeiculo }, transaction);

            var fotosNormalizadas = (fotos ?? Array.Empty<string>())
                .Select(LimparFiltro)
                .Where(url => !string.IsNullOrWhiteSpace(url))
                .Select(url => url!)
                .ToList();

            if (fotosNormalizadas.Count == 0)
            {
                return;
            }

            const string insertSql = @"
INSERT INTO garagem_veiculo_foto (id, id_veiculo, url, ordem, data_criacao)
VALUES (@Id, @IdVeiculo, @Url, @Ordem, NOW());";

            for (var i = 0; i < fotosNormalizadas.Count; i++)
            {
                await connection.ExecuteAsync(insertSql, new
                {
                    Id = Guid.NewGuid(),
                    IdVeiculo = idVeiculo,
                    Url = fotosNormalizadas[i],
                    Ordem = i
                }, transaction);
            }
        }

        private async Task<string> ResolveCodigoEstoqueAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            Guid idEstabelecimento,
            string? codigoEstoque,
            string? marca,
            string? modelo,
            int? anoModelo,
            Guid? ignorarId)
        {
            var normalized = LimparFiltro(codigoEstoque);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                await GarantirCodigoEstoqueUnicoAsync(connection, transaction, idEstabelecimento, normalized, ignorarId);
                return normalized;
            }

            return await GerarCodigoEstoqueUnicoAsync(
                connection,
                transaction,
                idEstabelecimento,
                marca ?? string.Empty,
                modelo ?? string.Empty,
                anoModelo ?? 0,
                ignorarId);
        }

        private async Task GarantirCodigoEstoqueUnicoAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            Guid idEstabelecimento,
            string codigoEstoque,
            Guid? ignorarId)
        {
            const string sql = @"
SELECT 1
  FROM garagem_veiculo
 WHERE id_estabelecimento = @IdEstabelecimento
   AND lower(codigo_estoque) = lower(@CodigoEstoque)
   AND (@IgnorarId IS NULL OR id <> @IgnorarId)
 LIMIT 1;";

            var existe = await connection.ExecuteScalarAsync<int?>(sql, new
            {
                IdEstabelecimento = idEstabelecimento,
                CodigoEstoque = codigoEstoque,
                IgnorarId = ignorarId
            }, transaction);

            if (existe.HasValue)
            {
                throw new InvalidOperationException("Codigo de estoque ja existe para este estabelecimento.");
            }
        }

        private async Task<string> GerarCodigoEstoqueUnicoAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            Guid idEstabelecimento,
            string marca,
            string modelo,
            int anoModelo,
            Guid? ignorarId)
        {
            var baseCode = BuildStockCodeBase(marca, modelo, anoModelo);

            const string sql = @"
SELECT codigo_estoque AS CodigoEstoque
  FROM garagem_veiculo
 WHERE id_estabelecimento = @IdEstabelecimento
   AND (lower(codigo_estoque) = lower(@CodigoBase) OR lower(codigo_estoque) LIKE lower(@CodigoLike))
   AND (@IgnorarId IS NULL OR id <> @IgnorarId);";

            var existentes = (await connection.QueryAsync<GarageVehicleStockCodeRow>(sql, new
            {
                IdEstabelecimento = idEstabelecimento,
                CodigoBase = baseCode,
                CodigoLike = $"{baseCode}-%",
                IgnorarId = ignorarId
            }, transaction)).ToList();

            if (existentes.Count == 0 || existentes.All(item => !string.Equals(item.CodigoEstoque, baseCode, StringComparison.OrdinalIgnoreCase)))
            {
                return baseCode;
            }

            var maiorSufixo = 1;
            foreach (var item in existentes)
            {
                if (string.IsNullOrWhiteSpace(item.CodigoEstoque))
                {
                    continue;
                }

                if (string.Equals(item.CodigoEstoque, baseCode, StringComparison.OrdinalIgnoreCase))
                {
                    maiorSufixo = Math.Max(maiorSufixo, 1);
                    continue;
                }

                if (!item.CodigoEstoque.StartsWith(baseCode + "-", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var sufixoTexto = item.CodigoEstoque[(baseCode.Length + 1)..];
                if (int.TryParse(sufixoTexto, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sufixoNumero))
                {
                    maiorSufixo = Math.Max(maiorSufixo, sufixoNumero);
                }
            }

            return $"{baseCode}-{maiorSufixo + 1}";
        }

        private async Task<string> GerarSlugUnicoAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string marca,
            string modelo,
            int anoModelo,
            Guid? ignorarId)
        {
            var baseSlug = Slugify($"{marca} {modelo} {anoModelo.ToString(CultureInfo.InvariantCulture)}");

            const string sql = @"
SELECT id, slug
  FROM garagem_veiculo
 WHERE (slug = @SlugBase OR slug LIKE @SlugLike)
   AND (@IgnorarId IS NULL OR id <> @IgnorarId);";

            var existentes = (await connection.QueryAsync<GarageVehicleSlugRow>(sql, new
            {
                SlugBase = baseSlug,
                SlugLike = $"{baseSlug}-%",
                IgnorarId = ignorarId
            }, transaction)).ToList();

            if (existentes.Count == 0 || existentes.All(item => !string.Equals(item.Slug, baseSlug, StringComparison.Ordinal)))
            {
                return baseSlug;
            }

            var maiorSufixo = 1;
            foreach (var item in existentes)
            {
                if (string.IsNullOrWhiteSpace(item.Slug))
                {
                    continue;
                }

                if (string.Equals(item.Slug, baseSlug, StringComparison.Ordinal))
                {
                    maiorSufixo = Math.Max(maiorSufixo, 1);
                    continue;
                }

                if (!item.Slug.StartsWith(baseSlug + "-", StringComparison.Ordinal))
                {
                    continue;
                }

                var sufixoTexto = item.Slug[(baseSlug.Length + 1)..];
                if (int.TryParse(sufixoTexto, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sufixoNumero))
                {
                    maiorSufixo = Math.Max(maiorSufixo, sufixoNumero);
                }
            }

            return $"{baseSlug}-{maiorSufixo + 1}";
        }

        private static object BuildCreateVehicleParameters(
            Guid id,
            Guid idEstabelecimento,
            string slug,
            string codigoEstoque,
            CreateGarageVehicleRequest request)
            => new
            {
                Id = id,
                IdEstabelecimento = idEstabelecimento,
                Slug = slug,
                Titulo = request.Titulo!,
                Marca = request.Marca!,
                Modelo = request.Modelo!,
                Categoria = request.Categoria!,
                AnoFabricacao = request.AnoFabricacao!.Value,
                AnoModelo = request.AnoModelo!.Value,
                Preco = request.Preco!.Value,
                PrecoAnterior = request.PrecoAnterior,
                Km = request.Km,
                Combustivel = LimparFiltro(request.Combustivel),
                Cambio = LimparFiltro(request.Cambio),
                Cor = LimparFiltro(request.Cor),
                Placa = LimparFiltro(request.Placa),
                Cidade = LimparFiltro(request.Cidade),
                Carroceria = LimparFiltro(request.Carroceria),
                Portas = request.Portas,
                Assentos = request.Assentos,
                Status = request.Status!,
                TipoVeiculo = request.TipoVeiculo!,
                Destaque = request.Destaque,
                LabelDestaque = request.Destaque ? LimparFiltro(request.LabelDestaque) : null,
                CodigoEstoque = codigoEstoque,
                Tracao = LimparFiltro(request.Tracao),
                Descricao = LimparFiltro(request.Descricao),
                OpcionaisJson = SerializeStringList(request.Opcionais),
                CondicoesJson = SerializeConditionItems(request.Condicoes)
            };

        private static object BuildVehicleParameters(Guid id, Guid idEstabelecimento, string slug, UpsertGarageVehicleRequest request)
            => new
            {
                Id = id,
                IdEstabelecimento = idEstabelecimento,
                Slug = slug,
                Titulo = request.Titulo.Trim(),
                Marca = request.Marca.Trim(),
                Modelo = request.Modelo.Trim(),
                Categoria = request.Categoria.Trim(),
                AnoFabricacao = request.AnoFabricacao,
                AnoModelo = request.AnoModelo,
                Preco = request.Preco,
                PrecoAnterior = request.PrecoAnterior,
                Km = request.Km,
                Combustivel = request.Combustivel.Trim(),
                Cambio = request.Cambio.Trim(),
                Cor = request.Cor.Trim(),
                Placa = LimparFiltro(request.Placa),
                Cidade = request.Cidade.Trim(),
                Carroceria = request.Carroceria.Trim(),
                Portas = request.Portas,
                Assentos = request.Assentos,
                Status = request.Status.Trim(),
                TipoVeiculo = request.TipoVeiculo.Trim(),
                Destaque = request.Destaque,
                LabelDestaque = request.Destaque ? LimparFiltro(request.LabelDestaque) : null,
                CodigoEstoque = request.CodigoEstoque.Trim(),
                Tracao = LimparFiltro(request.Tracao),
                Descricao = request.Descricao.Trim(),
                OpcionaisJson = SerializeStringList(request.Opcionais),
                CondicoesJson = SerializeConditionItems(request.Condicoes)
            };

        private async Task EnsureSchemaAsync()
        {
            if (_schemaEnsured)
            {
                return;
            }

            await SchemaLock.WaitAsync();
            try
            {
                if (_schemaEnsured)
                {
                    return;
                }

                const string sql = @"
CREATE TABLE IF NOT EXISTS garagem_veiculo (
    id UUID PRIMARY KEY,
    id_estabelecimento UUID NOT NULL,
    slug VARCHAR(220) NOT NULL,
    titulo VARCHAR(220) NOT NULL,
    marca VARCHAR(120) NOT NULL,
    modelo VARCHAR(160) NOT NULL,
    categoria VARCHAR(80) NOT NULL,
    ano_fabricacao INTEGER NOT NULL,
    ano_modelo INTEGER NOT NULL,
    preco NUMERIC(14,2) NOT NULL,
    preco_anterior NUMERIC(14,2) NULL,
    km INTEGER NULL,
    combustivel VARCHAR(80) NULL,
    cambio VARCHAR(80) NULL,
    cor VARCHAR(80) NULL,
    placa VARCHAR(20) NULL,
    cidade VARCHAR(180) NULL,
    carroceria VARCHAR(120) NULL,
    portas INTEGER NULL,
    assentos INTEGER NULL,
    status VARCHAR(40) NOT NULL DEFAULT 'disponivel',
    tipo_veiculo VARCHAR(40) NOT NULL,
    destaque BOOLEAN NOT NULL DEFAULT FALSE,
    label_destaque VARCHAR(120) NULL,
    codigo_estoque VARCHAR(80) NOT NULL,
    tracao VARCHAR(40) NULL,
    descricao TEXT NULL,
    opcionais JSONB NOT NULL DEFAULT '[]'::jsonb,
    condicoes JSONB NOT NULL DEFAULT '[]'::jsonb,
    data_criacao TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    data_atualizacao TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_garagem_veiculo_slug
    ON garagem_veiculo (slug);

CREATE UNIQUE INDEX IF NOT EXISTS ux_garagem_veiculo_estabelecimento_codigo_estoque
    ON garagem_veiculo (id_estabelecimento, lower(codigo_estoque));

CREATE INDEX IF NOT EXISTS ix_garagem_veiculo_estabelecimento_status
    ON garagem_veiculo (id_estabelecimento, status);

CREATE INDEX IF NOT EXISTS ix_garagem_veiculo_estabelecimento_categoria
    ON garagem_veiculo (id_estabelecimento, categoria);

CREATE INDEX IF NOT EXISTS ix_garagem_veiculo_estabelecimento_destaque
    ON garagem_veiculo (id_estabelecimento, destaque);

CREATE TABLE IF NOT EXISTS garagem_veiculo_foto (
    id UUID PRIMARY KEY,
    id_veiculo UUID NOT NULL REFERENCES garagem_veiculo (id) ON DELETE CASCADE,
    url TEXT NOT NULL,
    ordem INTEGER NOT NULL,
    data_criacao TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_garagem_veiculo_foto_ordem
    ON garagem_veiculo_foto (id_veiculo, ordem);

CREATE INDEX IF NOT EXISTS ix_garagem_veiculo_foto_veiculo
    ON garagem_veiculo_foto (id_veiculo, ordem);

CREATE TABLE IF NOT EXISTS garagem_config (
    id_estabelecimento UUID PRIMARY KEY,
    vitrine_url_base TEXT,
    data_criacao TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    data_atualizacao TIMESTAMPTZ NOT NULL DEFAULT NOW()
);";

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.ExecuteAsync(sql);
                _schemaEnsured = true;
            }
            finally
            {
                SchemaLock.Release();
            }
        }

        private static string SerializeStringList(IReadOnlyCollection<string>? values)
        {
            var normalized = (values ?? Array.Empty<string>())
                .Select(LimparFiltro)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!)
                .ToList();

            return JsonSerializer.Serialize(normalized);
        }

        private static string SerializeConditionItems(IReadOnlyCollection<GarageVehicleConditionRequestDto>? values)
        {
            var normalized = (values ?? Array.Empty<GarageVehicleConditionRequestDto>())
                .Where(item => !string.IsNullOrWhiteSpace(item.Item))
                .Select(item => new GarageVehicleConditionStorageItem
                {
                    Item = item.Item.Trim(),
                    Status = item.Status.Trim(),
                    Nota = LimparFiltro(item.Nota) ?? string.Empty
                })
                .ToList();

            return JsonSerializer.Serialize(normalized);
        }

        private static List<string> DeserializeStringList(string? json)
        {
            try
            {
                return string.IsNullOrWhiteSpace(json)
                    ? new List<string>()
                    : JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
            }
            catch
            {
                return new List<string>();
            }
        }

        private static List<GarageVehicleConditionItemDto> DeserializeConditionItems(string? json)
        {
            try
            {
                var items = string.IsNullOrWhiteSpace(json)
                    ? new List<GarageVehicleConditionStorageItem>()
                    : JsonSerializer.Deserialize<List<GarageVehicleConditionStorageItem>>(json) ?? new List<GarageVehicleConditionStorageItem>();

                return items.Select(item => new GarageVehicleConditionItemDto
                {
                    Label = item.Item ?? string.Empty,
                    Status = NormalizeConditionItemStatus(item.Status) ?? string.Empty,
                    Note = item.Nota ?? string.Empty
                }).ToList();
            }
            catch
            {
                return new List<GarageVehicleConditionItemDto>();
            }
        }

        private static string ResolveUniqueConstraintMessage(PostgresException ex)
        {
            if (string.Equals(ex.ConstraintName, "ux_garagem_veiculo_estabelecimento_codigo_estoque", StringComparison.Ordinal))
            {
                return "Codigo de estoque ja existe para este estabelecimento.";
            }

            if (string.Equals(ex.ConstraintName, "ux_garagem_veiculo_slug", StringComparison.Ordinal))
            {
                return "Nao foi possivel gerar um slug unico para o veiculo.";
            }

            return "Nao foi possivel salvar o veiculo devido a um conflito de unicidade.";
        }

        private static string Slugify(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "veiculo";
            }

            var normalized = value
                .Trim()
                .ToLowerInvariant()
                .Normalize(NormalizationForm.FormD);

            var builder = new StringBuilder(normalized.Length);
            var lastWasSeparator = false;

            foreach (var ch in normalized)
            {
                var category = CharUnicodeInfo.GetUnicodeCategory(ch);
                if (category == UnicodeCategory.NonSpacingMark)
                {
                    continue;
                }

                if (char.IsLetterOrDigit(ch))
                {
                    builder.Append(ch);
                    lastWasSeparator = false;
                    continue;
                }

                if (lastWasSeparator)
                {
                    continue;
                }

                builder.Append('-');
                lastWasSeparator = true;
            }

            var slug = builder.ToString().Trim('-');
            return string.IsNullOrWhiteSpace(slug) ? "veiculo" : slug;
        }

        private static string? NormalizeStatus(string? value)
        {
            var normalized = NormalizeToken(value);
            return normalized switch
            {
                "disponivel" => "disponivel",
                "indisponivel" => "indisponivel",
                "vendido" => "vendido",
                _ => LimparFiltro(value)
            };
        }

        private static string NormalizeVehicleType(string? value)
        {
            var normalized = NormalizeToken(value);
            return normalized switch
            {
                "novo" => "novo",
                "seminovo" => "seminovo",
                _ => string.Empty
            };
        }

        private static string? NormalizeCategory(string? value)
        {
            var normalized = NormalizeToken(value);
            return normalized switch
            {
                "suv" => "SUV",
                "sedan" => "Sedan",
                "pickup" => "Pickup",
                "eletrico" => "Eletrico",
                "hatch" => "Hatch",
                _ => LimparFiltro(value)
            };
        }

        private static string? NormalizeConditionItemStatus(string? value)
        {
            var normalized = NormalizeToken(value);
            return normalized switch
            {
                "excelente" => "Excelente",
                "bom" => "Bom",
                "regular" => "Regular",
                _ => LimparFiltro(value)
            };
        }

        private static string NormalizeToken(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var normalized = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(normalized.Length);

            foreach (var ch in normalized)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                {
                    builder.Append(ch);
                }
            }

            return builder.ToString().Normalize(NormalizationForm.FormC);
        }

        private static string? LimparFiltro(string? value)
            => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        private sealed class GarageVehicleRow
        {
            public Guid Id { get; set; }
            public Guid IdEstabelecimento { get; set; }
            public string? Slug { get; set; }
            public string? Titulo { get; set; }
            public string? Marca { get; set; }
            public string? Modelo { get; set; }
            public string? Categoria { get; set; }
            public int AnoFabricacao { get; set; }
            public int AnoModelo { get; set; }
            public decimal Preco { get; set; }
            public decimal? PrecoAnterior { get; set; }
            public int? Km { get; set; }
            public string? Combustivel { get; set; }
            public string? Cambio { get; set; }
            public string? Cor { get; set; }
            public string? Placa { get; set; }
            public string? Cidade { get; set; }
            public string? Carroceria { get; set; }
            public int? Portas { get; set; }
            public int? Assentos { get; set; }
            public string? Status { get; set; }
            public string? TipoVeiculo { get; set; }
            public bool Destaque { get; set; }
            public string? LabelDestaque { get; set; }
            public string? CodigoEstoque { get; set; }
            public string? Tracao { get; set; }
            public string? Descricao { get; set; }
            public string? OpcionaisJson { get; set; }
            public string? CondicoesJson { get; set; }
        }

        private sealed class GarageVehiclePhotoRow
        {
            public Guid VehicleId { get; set; }
            public string? Url { get; set; }
            public int Ordem { get; set; }
        }

        private sealed class GarageVehicleIdentityRow
        {
            public Guid Id { get; set; }
            public Guid IdEstabelecimento { get; set; }
        }

        private sealed class GarageVehicleSlugRow
        {
            public Guid Id { get; set; }
            public string? Slug { get; set; }
        }

        private sealed class GarageVehicleStockCodeRow
        {
            public string? CodigoEstoque { get; set; }
        }

        private sealed class GarageVehicleConditionStorageItem
        {
            public string? Item { get; set; }
            public string? Status { get; set; }
            public string? Nota { get; set; }
        }

        private static string BuildStockCodeBase(string marca, string modelo, int anoModelo)
        {
            var marcaToken = BuildStockCodeToken(marca, 3, "VEI");
            var modeloToken = BuildStockCodeToken(modelo, 4, "ITEM");
            return $"{marcaToken}-{modeloToken}-{anoModelo.ToString(CultureInfo.InvariantCulture)}";
        }

        private static string BuildStockCodeToken(string? value, int maxLength, string fallback)
        {
            var normalized = NormalizeToken(value);
            var token = new string(normalized.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(token))
            {
                return fallback;
            }

            return token.Length <= maxLength ? token : token[..maxLength];
        }
    }
}
