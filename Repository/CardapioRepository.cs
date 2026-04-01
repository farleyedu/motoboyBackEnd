using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using APIBack.Model.Cardapio;
using APIBack.Repository.Interface;
using APIBack.Service;
using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace APIBack.Repository
{
    public class CardapioRepository : ICardapioRepository
    {
        private const string UniqueViolation = "23505";
        private static readonly Regex SlugRegex = new("[^a-z0-9]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private readonly string _connectionString;

        public CardapioRepository(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection")
                ?? configuration["ConnectionStrings:DefaultConnection"]
                ?? throw new InvalidOperationException("Connection string 'DefaultConnection' nao encontrada.");
        }

        public async Task<bool> EstabelecimentoTemModuloAtivoAsync(Guid idEstabelecimento, string modulo)
        {
            const string sql = @"
SELECT EXISTS (
    SELECT 1
      FROM estabelecimentos e
     CROSS JOIN LATERAL unnest(e.modulos_ativos)::text AS modulo_ativo
     WHERE e.id = @IdEstabelecimento
       AND COALESCE(e.ativo, TRUE) = TRUE
       AND lower(modulo_ativo) = lower(@Modulo)
);";

            await using var connection = new NpgsqlConnection(_connectionString);
            return await connection.ExecuteScalarAsync<bool>(sql, new
            {
                IdEstabelecimento = idEstabelecimento,
                Modulo = ValidationUtils.NormalizeToken(modulo)
            });
        }

        public async Task<CardapioEstabelecimentoPublico?> ObterEstabelecimentoPublicoAsync(Guid? idEstabelecimento, string? estabelecimentoSlug)
        {
            const string sql = @"
SELECT e.id,
       e.nome_fantasia AS NomeFantasia,
       e.slug,
       COALESCE(cwc.logo_url, e.url_logo) AS UrlLogo,
       COALESCE(cwc.nome_publico, e.nome_fantasia) AS NomePublico,
       cwc.emoji,
       cwc.banner_url AS BannerUrl,
       cwc.rating,
       cwc.review_count AS ReviewCount,
       cwc.delivery_time_label AS DeliveryTimeLabel,
       COALESCE(cwc.delivery_fee_value, e.taxa_entrega_fixa, 0) AS DeliveryFeeValue,
       cwc.delivery_fee_label AS DeliveryFeeLabel,
       COALESCE(cwc.service_fee_value, 0) AS ServiceFeeValue,
       COALESCE(cwc.aceita_entrega, TRUE) AS AceitaEntrega,
       COALESCE(cwc.aceita_retirada, TRUE) AS AceitaRetirada,
       COALESCE(cwc.publicado, FALSE) AS Publicado,
       COALESCE(e.aceita_pedidos, FALSE) AS AceitaPedidos,
       COALESCE(e.pedido_minimo, 0) AS PedidoMinimo,
       COALESCE(e.taxa_entrega_fixa, 0) AS TaxaEntregaFixa,
       COALESCE(e.tempo_preparo_min, 0) AS TempoPreparoMin,
       e.modulos_ativos::text[] AS ModulosAtivosRaw
  FROM estabelecimentos e
  LEFT JOIN cardapio_web_config cwc
    ON cwc.id_estabelecimento = e.id
 WHERE (@IdEstabelecimento IS NULL OR e.id = @IdEstabelecimento)
   AND (@Slug IS NULL OR lower(e.slug) = lower(@Slug))
   AND COALESCE(e.ativo, TRUE) = TRUE
   AND COALESCE(e.status, 'ativo') IN ('ativo', 'trial')
 LIMIT 1;";

            await using var connection = new NpgsqlConnection(_connectionString);
            return await connection.QueryFirstOrDefaultAsync<CardapioEstabelecimentoPublico>(sql, new
            {
                IdEstabelecimento = idEstabelecimento,
                Slug = ValidationUtils.TrimToNull(estabelecimentoSlug)
            });
        }

        public async Task<CardapioWebConfig?> ObterWebConfigAsync(Guid idEstabelecimento)
        {
            const string sql = @"
SELECT id_estabelecimento AS IdEstabelecimento,
       nome_publico AS NomePublico,
       emoji,
       logo_url AS LogoUrl,
       banner_url AS BannerUrl,
       rating,
       review_count AS ReviewCount,
       delivery_time_label AS DeliveryTimeLabel,
       COALESCE(delivery_fee_value, 0) AS DeliveryFeeValue,
       delivery_fee_label AS DeliveryFeeLabel,
       COALESCE(service_fee_value, 0) AS ServiceFeeValue,
       COALESCE(aceita_entrega, TRUE) AS AceitaEntrega,
       COALESCE(aceita_retirada, TRUE) AS AceitaRetirada,
       COALESCE(publicado, FALSE) AS Publicado,
       created_at AS CreatedAt,
       updated_at AS UpdatedAt
  FROM cardapio_web_config
 WHERE id_estabelecimento = @IdEstabelecimento
 LIMIT 1;";

            await using var connection = new NpgsqlConnection(_connectionString);
            return await connection.QueryFirstOrDefaultAsync<CardapioWebConfig>(sql, new { IdEstabelecimento = idEstabelecimento });
        }

        public async Task<CardapioWebConfig> UpsertWebConfigAsync(CardapioWebConfig entity)
        {
            const string sql = @"
INSERT INTO cardapio_web_config (
    id_estabelecimento,
    nome_publico,
    emoji,
    logo_url,
    banner_url,
    rating,
    review_count,
    delivery_time_label,
    delivery_fee_value,
    delivery_fee_label,
    service_fee_value,
    aceita_entrega,
    aceita_retirada,
    publicado,
    created_at,
    updated_at
) VALUES (
    @IdEstabelecimento,
    @NomePublico,
    @Emoji,
    @LogoUrl,
    @BannerUrl,
    @Rating,
    @ReviewCount,
    @DeliveryTimeLabel,
    @DeliveryFeeValue,
    @DeliveryFeeLabel,
    @ServiceFeeValue,
    @AceitaEntrega,
    @AceitaRetirada,
    @Publicado,
    NOW(),
    NOW()
)
ON CONFLICT (id_estabelecimento) DO UPDATE
   SET nome_publico = EXCLUDED.nome_publico,
       emoji = EXCLUDED.emoji,
       logo_url = EXCLUDED.logo_url,
       banner_url = EXCLUDED.banner_url,
       rating = EXCLUDED.rating,
       review_count = EXCLUDED.review_count,
       delivery_time_label = EXCLUDED.delivery_time_label,
       delivery_fee_value = EXCLUDED.delivery_fee_value,
       delivery_fee_label = EXCLUDED.delivery_fee_label,
       service_fee_value = EXCLUDED.service_fee_value,
       aceita_entrega = EXCLUDED.aceita_entrega,
       aceita_retirada = EXCLUDED.aceita_retirada,
       publicado = EXCLUDED.publicado,
       updated_at = NOW();";

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.ExecuteAsync(sql, entity);
            return await ObterWebConfigAsync(entity.IdEstabelecimento)
                ?? throw new InvalidOperationException("Nao foi possivel carregar a configuracao web do cardapio.");
        }

        public async Task<(IReadOnlyCollection<CardapioCategoria> Itens, int Total)> ListarCategoriasAsync(
            Guid idEstabelecimento,
            string? busca,
            bool? ativo,
            int page,
            int pageSize)
        {
            const string sql = @"
SELECT COUNT(1)
  FROM cardapio_categoria
 WHERE id_estabelecimento = @IdEstabelecimento
   AND deleted_at IS NULL
   AND (@Ativo IS NULL OR ativo = @Ativo)
   AND (
       @Busca IS NULL
       OR nome ILIKE @BuscaLike
       OR COALESCE(slug, '') ILIKE @BuscaLike
       OR COALESCE(descricao, '') ILIKE @BuscaLike
   );

SELECT id,
       id_estabelecimento AS IdEstabelecimento,
       nome,
       slug,
       emoji,
       descricao,
       imagem_url AS ImagemUrl,
       ordem,
       ativo,
       created_at AS CreatedAt,
       updated_at AS UpdatedAt,
       deleted_at AS DeletedAt
  FROM cardapio_categoria
 WHERE id_estabelecimento = @IdEstabelecimento
   AND deleted_at IS NULL
   AND (@Ativo IS NULL OR ativo = @Ativo)
   AND (
       @Busca IS NULL
       OR nome ILIKE @BuscaLike
       OR COALESCE(slug, '') ILIKE @BuscaLike
       OR COALESCE(descricao, '') ILIKE @BuscaLike
   )
 ORDER BY ordem ASC, nome ASC
 LIMIT @PageSize OFFSET @Offset;";

            var parameters = BuildBuscaParameters(idEstabelecimento, busca, page, pageSize, ativo);

            await using var connection = new NpgsqlConnection(_connectionString);
            using var multi = await connection.QueryMultipleAsync(sql, parameters);
            var total = await multi.ReadFirstAsync<int>();
            var itens = (await multi.ReadAsync<CardapioCategoria>()).ToArray();
            return (itens, total);
        }

        public async Task<bool> CategoriaTemProdutosAsync(Guid idEstabelecimento, Guid categoriaId)
        {
            const string sql = @"
SELECT EXISTS (
    SELECT 1
      FROM cardapio_produto
     WHERE id_estabelecimento = @IdEstabelecimento
       AND categoria_id = @CategoriaId
       AND deleted_at IS NULL
);";

            await using var connection = new NpgsqlConnection(_connectionString);
            return await connection.ExecuteScalarAsync<bool>(sql, new
            {
                IdEstabelecimento = idEstabelecimento,
                CategoriaId = categoriaId
            });
        }

        public async Task<CardapioCategoria?> ObterCategoriaPorIdAsync(Guid idEstabelecimento, Guid id)
        {
            const string sql = @"
SELECT id,
       id_estabelecimento AS IdEstabelecimento,
       nome,
       slug,
       emoji,
       descricao,
       imagem_url AS ImagemUrl,
       ordem,
       ativo,
       created_at AS CreatedAt,
       updated_at AS UpdatedAt,
       deleted_at AS DeletedAt
  FROM cardapio_categoria
 WHERE id_estabelecimento = @IdEstabelecimento
   AND id = @Id
   AND deleted_at IS NULL
 LIMIT 1;";

            await using var connection = new NpgsqlConnection(_connectionString);
            return await connection.QueryFirstOrDefaultAsync<CardapioCategoria>(sql, new
            {
                IdEstabelecimento = idEstabelecimento,
                Id = id
            });
        }

        public async Task<Guid> CriarCategoriaAsync(CardapioCategoria entity)
        {
            const string sql = @"
INSERT INTO cardapio_categoria (
    id,
    id_estabelecimento,
    nome,
    slug,
    emoji,
    descricao,
    imagem_url,
    ordem,
    ativo,
    created_at,
    updated_at
) VALUES (
    @Id,
    @IdEstabelecimento,
    @Nome,
    @Slug,
    @Emoji,
    @Descricao,
    @ImagemUrl,
    @Ordem,
    @Ativo,
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
                entity.Slug = await GerarSlugCategoriaAsync(connection, transaction, entity.IdEstabelecimento, entity.Slug, entity.Nome, null);
                await connection.ExecuteAsync(sql, entity, transaction);
                await transaction.CommitAsync();
                return entity.Id;
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

        public async Task<bool> AtualizarCategoriaAsync(CardapioCategoria entity)
        {
            const string sql = @"
UPDATE cardapio_categoria
   SET nome = @Nome,
       slug = @Slug,
       emoji = @Emoji,
       descricao = @Descricao,
       imagem_url = @ImagemUrl,
       ordem = @Ordem,
       ativo = @Ativo,
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
                entity.Slug = await GerarSlugCategoriaAsync(connection, transaction, entity.IdEstabelecimento, entity.Slug, entity.Nome, entity.Id);
                var affected = await connection.ExecuteAsync(sql, entity, transaction);
                if (affected <= 0)
                {
                    await transaction.RollbackAsync();
                    return false;
                }

                await transaction.CommitAsync();
                return true;
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

        public async Task<bool> AtualizarCategoriaStatusAsync(Guid idEstabelecimento, Guid id, bool ativo)
        {
            const string sql = @"
UPDATE cardapio_categoria
   SET ativo = @Ativo,
       updated_at = NOW()
 WHERE id_estabelecimento = @IdEstabelecimento
   AND id = @Id
   AND deleted_at IS NULL;";

            await using var connection = new NpgsqlConnection(_connectionString);
            return await connection.ExecuteAsync(sql, new
            {
                IdEstabelecimento = idEstabelecimento,
                Id = id,
                Ativo = ativo
            }) > 0;
        }

        public async Task<bool> ExcluirCategoriaAsync(Guid idEstabelecimento, Guid id)
        {
            const string sql = @"
UPDATE cardapio_categoria
   SET deleted_at = NOW(),
       updated_at = NOW()
 WHERE id_estabelecimento = @IdEstabelecimento
   AND id = @Id
   AND deleted_at IS NULL;";

            await using var connection = new NpgsqlConnection(_connectionString);
            return await connection.ExecuteAsync(sql, new
            {
                IdEstabelecimento = idEstabelecimento,
                Id = id
            }) > 0;
        }

        public async Task<(IReadOnlyCollection<CardapioGrupoAdicional> Itens, int Total)> ListarGruposAsync(
            Guid idEstabelecimento,
            string? busca,
            bool? ativo,
            int page,
            int pageSize,
            string? tipo = null)
        {
            var normalizedBusca = ValidationUtils.TrimToNull(busca);
            var normalizedTipo = ValidationUtils.TrimToNull(tipo);
            var normalizedPage = page <= 0 ? 1 : page;
            var normalizedPageSize = pageSize <= 0 ? 20 : pageSize;

            const string sql = @"
SELECT COUNT(1)
  FROM cardapio_grupo_adicional
 WHERE id_estabelecimento = @IdEstabelecimento
   AND deleted_at IS NULL
   AND (@Ativo IS NULL OR ativo = @Ativo)
   AND (@Tipo IS NULL OR lower(COALESCE(tipo, 'adicional_global')) = lower(@Tipo))
   AND (
       @Busca IS NULL
       OR nome ILIKE @BuscaLike
       OR COALESCE(descricao, '') ILIKE @BuscaLike
   );

SELECT id,
       id_estabelecimento AS IdEstabelecimento,
       nome,
       COALESCE(tipo, 'adicional_global') AS Tipo,
       descricao,
       min_selecionados AS MinSelecionados,
       max_selecionados AS MaxSelecionados,
       ordem,
       ativo,
       created_at AS CreatedAt,
       updated_at AS UpdatedAt,
       deleted_at AS DeletedAt
  FROM cardapio_grupo_adicional
 WHERE id_estabelecimento = @IdEstabelecimento
   AND deleted_at IS NULL
   AND (@Ativo IS NULL OR ativo = @Ativo)
   AND (@Tipo IS NULL OR lower(COALESCE(tipo, 'adicional_global')) = lower(@Tipo))
   AND (
       @Busca IS NULL
       OR nome ILIKE @BuscaLike
       OR COALESCE(descricao, '') ILIKE @BuscaLike
   )
 ORDER BY ordem ASC, nome ASC
 LIMIT @PageSize OFFSET @Offset;";

            var parameters = new
            {
                IdEstabelecimento = idEstabelecimento,
                Busca = normalizedBusca,
                BuscaLike = normalizedBusca == null ? null : $"%{normalizedBusca}%",
                Ativo = ativo,
                Tipo = normalizedTipo,
                PageSize = normalizedPageSize,
                Offset = (normalizedPage - 1) * normalizedPageSize
            };

            await using var connection = new NpgsqlConnection(_connectionString);
            using var multi = await connection.QueryMultipleAsync(sql, parameters);
            var total = await multi.ReadFirstAsync<int>();
            var itens = (await multi.ReadAsync<CardapioGrupoAdicional>()).ToList();
            await PreencherItensDeGrupoAsync(connection, itens);
            return (itens.ToArray(), total);
        }

        public async Task<IReadOnlyCollection<CardapioGrupoAdicional>> ListarGruposPorIdsAsync(Guid idEstabelecimento, IReadOnlyCollection<Guid> ids, string? tipo = null)
        {
            if (ids == null || ids.Count == 0)
            {
                return Array.Empty<CardapioGrupoAdicional>();
            }

            const string sql = @"
SELECT id,
       id_estabelecimento AS IdEstabelecimento,
       nome,
       COALESCE(tipo, 'adicional_global') AS Tipo,
       descricao,
       min_selecionados AS MinSelecionados,
       max_selecionados AS MaxSelecionados,
       ordem,
       ativo,
       created_at AS CreatedAt,
       updated_at AS UpdatedAt,
       deleted_at AS DeletedAt
  FROM cardapio_grupo_adicional
 WHERE id_estabelecimento = @IdEstabelecimento
   AND deleted_at IS NULL
   AND (@Tipo IS NULL OR lower(COALESCE(tipo, 'adicional_global')) = lower(@Tipo))
   AND id = ANY(@Ids)
 ORDER BY ordem ASC, nome ASC;";

            await using var connection = new NpgsqlConnection(_connectionString);
            var itens = (await connection.QueryAsync<CardapioGrupoAdicional>(sql, new
            {
                IdEstabelecimento = idEstabelecimento,
                Ids = ids.Distinct().ToArray(),
                Tipo = ValidationUtils.TrimToNull(tipo)
            })).ToList();

            await PreencherItensDeGrupoAsync(connection, itens);
            return itens.ToArray();
        }

        public async Task<bool> GrupoTemProdutosAsync(Guid idEstabelecimento, Guid grupoId)
        {
            const string sql = @"
SELECT EXISTS (
    SELECT 1
      FROM cardapio_produto_grupo pg
      JOIN cardapio_produto p
        ON p.id = pg.id_produto
       AND p.deleted_at IS NULL
     WHERE p.id_estabelecimento = @IdEstabelecimento
       AND pg.id_grupo = @GrupoId
);";

            await using var connection = new NpgsqlConnection(_connectionString);
            return await connection.ExecuteScalarAsync<bool>(sql, new
            {
                IdEstabelecimento = idEstabelecimento,
                GrupoId = grupoId
            });
        }

        public async Task<CardapioGrupoAdicional?> ObterGrupoPorIdAsync(Guid idEstabelecimento, Guid id, string? tipo = null)
        {
            const string sql = @"
SELECT id,
       id_estabelecimento AS IdEstabelecimento,
       nome,
       COALESCE(tipo, 'adicional_global') AS Tipo,
       descricao,
       min_selecionados AS MinSelecionados,
       max_selecionados AS MaxSelecionados,
       ordem,
       ativo,
       created_at AS CreatedAt,
       updated_at AS UpdatedAt,
       deleted_at AS DeletedAt
  FROM cardapio_grupo_adicional
 WHERE id_estabelecimento = @IdEstabelecimento
   AND id = @Id
   AND (@Tipo IS NULL OR lower(COALESCE(tipo, 'adicional_global')) = lower(@Tipo))
   AND deleted_at IS NULL
 LIMIT 1;";

            await using var connection = new NpgsqlConnection(_connectionString);
            var grupo = await connection.QueryFirstOrDefaultAsync<CardapioGrupoAdicional>(sql, new
            {
                IdEstabelecimento = idEstabelecimento,
                Id = id,
                Tipo = ValidationUtils.TrimToNull(tipo)
            });

            if (grupo == null)
            {
                return null;
            }

            await PreencherItensDeGrupoAsync(connection, new[] { grupo });
            return grupo;
        }

        public async Task<Guid> CriarGrupoAsync(CardapioGrupoAdicional entity)
        {
            const string sql = @"
INSERT INTO cardapio_grupo_adicional (
    id,
    id_estabelecimento,
    nome,
    tipo,
    descricao,
    min_selecionados,
    max_selecionados,
    ordem,
    ativo,
    created_at,
    updated_at
) VALUES (
    @Id,
    @IdEstabelecimento,
    @Nome,
    @Tipo,
    @Descricao,
    @MinSelecionados,
    @MaxSelecionados,
    @Ordem,
    @Ativo,
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
                await connection.ExecuteAsync(sql, entity, transaction);
                await SincronizarItensDeGrupoAsync(connection, transaction, entity);
                await transaction.CommitAsync();
                return entity.Id;
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }

        public async Task<bool> AtualizarGrupoAsync(CardapioGrupoAdicional entity)
        {
            const string sql = @"
UPDATE cardapio_grupo_adicional
   SET nome = @Nome,
       tipo = @Tipo,
       descricao = @Descricao,
       min_selecionados = @MinSelecionados,
       max_selecionados = @MaxSelecionados,
       ordem = @Ordem,
       ativo = @Ativo,
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
                var affected = await connection.ExecuteAsync(sql, entity, transaction);
                if (affected <= 0)
                {
                    await transaction.RollbackAsync();
                    return false;
                }

                await SincronizarItensDeGrupoAsync(connection, transaction, entity);
                await transaction.CommitAsync();
                return true;
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }

        public async Task<bool> AtualizarGrupoStatusAsync(Guid idEstabelecimento, Guid id, bool ativo)
        {
            const string sql = @"
UPDATE cardapio_grupo_adicional
   SET ativo = @Ativo,
       updated_at = NOW()
 WHERE id_estabelecimento = @IdEstabelecimento
   AND id = @Id
   AND deleted_at IS NULL;";

            await using var connection = new NpgsqlConnection(_connectionString);
            return await connection.ExecuteAsync(sql, new
            {
                IdEstabelecimento = idEstabelecimento,
                Id = id,
                Ativo = ativo
            }) > 0;
        }

        public async Task<bool> ExcluirGrupoAsync(Guid idEstabelecimento, Guid id)
        {
            const string sql = @"
UPDATE cardapio_grupo_adicional
   SET deleted_at = NOW(),
       updated_at = NOW()
 WHERE id_estabelecimento = @IdEstabelecimento
   AND id = @Id
   AND deleted_at IS NULL;";

            await using var connection = new NpgsqlConnection(_connectionString);
            return await connection.ExecuteAsync(sql, new
            {
                IdEstabelecimento = idEstabelecimento,
                Id = id
            }) > 0;
        }

        public async Task<(IReadOnlyCollection<CardapioProduto> Itens, int Total)> ListarProdutosAsync(
            Guid idEstabelecimento,
            string? busca,
            bool? ativo,
            bool? destaque,
            bool? disponivel,
            Guid? categoriaId,
            int page,
            int pageSize)
        {
            const string sql = @"
SELECT COUNT(1)
  FROM cardapio_produto p
  JOIN cardapio_categoria c
    ON c.id = p.categoria_id
   AND c.deleted_at IS NULL
 WHERE p.id_estabelecimento = @IdEstabelecimento
   AND p.deleted_at IS NULL
   AND (@Ativo IS NULL OR p.ativo = @Ativo)
   AND (@Destaque IS NULL OR p.destaque = @Destaque)
   AND (@Disponivel IS NULL OR p.disponivel = @Disponivel)
   AND (@CategoriaId IS NULL OR p.categoria_id = @CategoriaId)
   AND (
       @Busca IS NULL
       OR p.nome ILIKE @BuscaLike
       OR COALESCE(p.slug, '') ILIKE @BuscaLike
       OR COALESCE(p.descricao, '') ILIKE @BuscaLike
       OR COALESCE(p.descricao_curta, '') ILIKE @BuscaLike
       OR c.nome ILIKE @BuscaLike
   );

SELECT p.id,
       p.id_estabelecimento AS IdEstabelecimento,
       p.categoria_id AS CategoriaId,
       c.nome AS CategoriaNome,
       p.nome,
       p.slug,
       p.emoji,
       p.descricao,
       p.descricao_curta AS DescricaoCurta,
       p.preco_base AS PrecoBase,
       p.preco_de AS PrecoDe,
       p.badge_desconto AS BadgeDesconto,
       p.is_club AS IsClub,
       p.imagem_url AS ImagemUrl,
       p.eco_friendly AS EcoFriendly,
       p.extras_titulo AS ExtrasTitulo,
       p.extras_subtitulo AS ExtrasSubtitulo,
       p.ordem,
       p.ativo,
       p.destaque,
       p.disponivel,
       p.publico_web AS PublicoWeb,
       p.created_at AS CreatedAt,
       p.updated_at AS UpdatedAt,
       p.deleted_at AS DeletedAt
  FROM cardapio_produto p
  JOIN cardapio_categoria c
    ON c.id = p.categoria_id
   AND c.deleted_at IS NULL
 WHERE p.id_estabelecimento = @IdEstabelecimento
   AND p.deleted_at IS NULL
   AND (@Ativo IS NULL OR p.ativo = @Ativo)
   AND (@Destaque IS NULL OR p.destaque = @Destaque)
   AND (@Disponivel IS NULL OR p.disponivel = @Disponivel)
   AND (@CategoriaId IS NULL OR p.categoria_id = @CategoriaId)
   AND (
       @Busca IS NULL
       OR p.nome ILIKE @BuscaLike
       OR COALESCE(p.slug, '') ILIKE @BuscaLike
       OR COALESCE(p.descricao, '') ILIKE @BuscaLike
       OR COALESCE(p.descricao_curta, '') ILIKE @BuscaLike
       OR c.nome ILIKE @BuscaLike
   )
 ORDER BY c.ordem ASC, c.nome ASC, p.destaque DESC, p.ordem ASC, p.nome ASC
 LIMIT @PageSize OFFSET @Offset;";

            var normalizedBusca = ValidationUtils.TrimToNull(busca);
            var normalizedPage = page <= 0 ? 1 : page;
            var normalizedPageSize = pageSize <= 0 ? 20 : pageSize;
            var parameters = new
            {
                IdEstabelecimento = idEstabelecimento,
                Busca = normalizedBusca,
                BuscaLike = normalizedBusca == null ? null : $"%{normalizedBusca}%",
                Ativo = ativo,
                Destaque = destaque,
                Disponivel = disponivel,
                CategoriaId = categoriaId,
                PageSize = normalizedPageSize,
                Offset = (normalizedPage - 1) * normalizedPageSize
            };

            await using var connection = new NpgsqlConnection(_connectionString);
            using var multi = await connection.QueryMultipleAsync(sql, parameters);
            var total = await multi.ReadFirstAsync<int>();
            var itens = (await multi.ReadAsync<CardapioProduto>()).ToList();
            await PreencherGrupoIdsDeProdutosAsync(connection, itens);
            return (itens.ToArray(), total);
        }

        public async Task<IReadOnlyCollection<CardapioProduto>> ListarProdutosPublicosAsync(Guid idEstabelecimento, string? busca)
        {
            const string sql = @"
SELECT p.id,
       p.id_estabelecimento AS IdEstabelecimento,
       p.categoria_id AS CategoriaId,
       c.nome AS CategoriaNome,
       p.nome,
       p.slug,
       p.emoji,
       p.descricao,
       p.descricao_curta AS DescricaoCurta,
       p.preco_base AS PrecoBase,
       p.preco_de AS PrecoDe,
       p.badge_desconto AS BadgeDesconto,
       p.is_club AS IsClub,
       p.imagem_url AS ImagemUrl,
       p.eco_friendly AS EcoFriendly,
       p.extras_titulo AS ExtrasTitulo,
       p.extras_subtitulo AS ExtrasSubtitulo,
       p.ordem,
       p.ativo,
       p.destaque,
       p.disponivel,
       p.publico_web AS PublicoWeb,
       p.created_at AS CreatedAt,
       p.updated_at AS UpdatedAt,
       p.deleted_at AS DeletedAt
  FROM cardapio_produto p
  JOIN cardapio_categoria c
    ON c.id = p.categoria_id
   AND c.deleted_at IS NULL
   AND c.ativo = TRUE
 WHERE p.id_estabelecimento = @IdEstabelecimento
   AND p.deleted_at IS NULL
   AND p.ativo = TRUE
   AND p.disponivel = TRUE
   AND p.publico_web = TRUE
   AND (
       @Busca IS NULL
       OR p.nome ILIKE @BuscaLike
       OR COALESCE(p.descricao, '') ILIKE @BuscaLike
       OR COALESCE(p.descricao_curta, '') ILIKE @BuscaLike
       OR c.nome ILIKE @BuscaLike
   )
 ORDER BY c.ordem ASC, c.nome ASC, p.destaque DESC, p.ordem ASC, p.nome ASC;";

            var normalizedBusca = ValidationUtils.TrimToNull(busca);

            await using var connection = new NpgsqlConnection(_connectionString);
            var itens = (await connection.QueryAsync<CardapioProduto>(sql, new
            {
                IdEstabelecimento = idEstabelecimento,
                Busca = normalizedBusca,
                BuscaLike = normalizedBusca == null ? null : $"%{normalizedBusca}%"
            })).ToList();

            await PreencherGruposPublicosDeProdutosAsync(connection, itens);
            return itens.ToArray();
        }

        public async Task<IReadOnlyCollection<CardapioProduto>> ListarProdutosPublicosPorIdsAsync(Guid idEstabelecimento, IReadOnlyCollection<Guid> ids)
        {
            if (ids == null || ids.Count == 0)
            {
                return Array.Empty<CardapioProduto>();
            }

            const string sql = @"
SELECT p.id,
       p.id_estabelecimento AS IdEstabelecimento,
       p.categoria_id AS CategoriaId,
       c.nome AS CategoriaNome,
       p.nome,
       p.slug,
       p.emoji,
       p.descricao,
       p.descricao_curta AS DescricaoCurta,
       p.preco_base AS PrecoBase,
       p.preco_de AS PrecoDe,
       p.badge_desconto AS BadgeDesconto,
       p.is_club AS IsClub,
       p.imagem_url AS ImagemUrl,
       p.eco_friendly AS EcoFriendly,
       p.extras_titulo AS ExtrasTitulo,
       p.extras_subtitulo AS ExtrasSubtitulo,
       p.ordem,
       p.ativo,
       p.destaque,
       p.disponivel,
       p.publico_web AS PublicoWeb,
       p.created_at AS CreatedAt,
       p.updated_at AS UpdatedAt,
       p.deleted_at AS DeletedAt
  FROM cardapio_produto p
  JOIN cardapio_categoria c
    ON c.id = p.categoria_id
   AND c.deleted_at IS NULL
   AND c.ativo = TRUE
 WHERE p.id_estabelecimento = @IdEstabelecimento
   AND p.deleted_at IS NULL
   AND p.ativo = TRUE
   AND p.disponivel = TRUE
   AND p.publico_web = TRUE
   AND p.id = ANY(@Ids);";

            await using var connection = new NpgsqlConnection(_connectionString);
            var itens = (await connection.QueryAsync<CardapioProduto>(sql, new
            {
                IdEstabelecimento = idEstabelecimento,
                Ids = ids.Distinct().ToArray()
            })).ToList();

            await PreencherGruposPublicosDeProdutosAsync(connection, itens);
            return itens.ToArray();
        }

        public async Task<CardapioProduto?> ObterProdutoPorIdAsync(Guid idEstabelecimento, Guid id)
        {
            const string sql = @"
SELECT p.id,
       p.id_estabelecimento AS IdEstabelecimento,
       p.categoria_id AS CategoriaId,
       c.nome AS CategoriaNome,
       p.nome,
       p.slug,
       p.emoji,
       p.descricao,
       p.descricao_curta AS DescricaoCurta,
       p.preco_base AS PrecoBase,
       p.preco_de AS PrecoDe,
       p.badge_desconto AS BadgeDesconto,
       p.is_club AS IsClub,
       p.imagem_url AS ImagemUrl,
       p.eco_friendly AS EcoFriendly,
       p.extras_titulo AS ExtrasTitulo,
       p.extras_subtitulo AS ExtrasSubtitulo,
       p.ordem,
       p.ativo,
       p.destaque,
       p.disponivel,
       p.publico_web AS PublicoWeb,
       p.created_at AS CreatedAt,
       p.updated_at AS UpdatedAt,
       p.deleted_at AS DeletedAt
  FROM cardapio_produto p
  JOIN cardapio_categoria c
    ON c.id = p.categoria_id
   AND c.deleted_at IS NULL
 WHERE p.id_estabelecimento = @IdEstabelecimento
   AND p.id = @Id
   AND p.deleted_at IS NULL
 LIMIT 1;";

            await using var connection = new NpgsqlConnection(_connectionString);
            var produto = await connection.QueryFirstOrDefaultAsync<CardapioProduto>(sql, new
            {
                IdEstabelecimento = idEstabelecimento,
                Id = id
            });

            if (produto == null)
            {
                return null;
            }

            await PreencherGrupoIdsDeProdutosAsync(connection, new[] { produto });
            return produto;
        }

        public async Task<CardapioProduto?> ObterProdutoPublicoPorSlugAsync(Guid idEstabelecimento, string slug)
        {
            const string sql = @"
SELECT p.id,
       p.id_estabelecimento AS IdEstabelecimento,
       p.categoria_id AS CategoriaId,
       c.nome AS CategoriaNome,
       p.nome,
       p.slug,
       p.emoji,
       p.descricao,
       p.descricao_curta AS DescricaoCurta,
       p.preco_base AS PrecoBase,
       p.preco_de AS PrecoDe,
       p.badge_desconto AS BadgeDesconto,
       p.is_club AS IsClub,
       p.imagem_url AS ImagemUrl,
       p.eco_friendly AS EcoFriendly,
       p.extras_titulo AS ExtrasTitulo,
       p.extras_subtitulo AS ExtrasSubtitulo,
       p.ordem,
       p.ativo,
       p.destaque,
       p.disponivel,
       p.publico_web AS PublicoWeb,
       p.created_at AS CreatedAt,
       p.updated_at AS UpdatedAt,
       p.deleted_at AS DeletedAt
  FROM cardapio_produto p
  JOIN cardapio_categoria c
    ON c.id = p.categoria_id
   AND c.deleted_at IS NULL
   AND c.ativo = TRUE
 WHERE p.id_estabelecimento = @IdEstabelecimento
   AND p.deleted_at IS NULL
   AND p.ativo = TRUE
   AND p.disponivel = TRUE
   AND p.publico_web = TRUE
   AND lower(p.slug) = lower(@Slug)
 LIMIT 1;";

            await using var connection = new NpgsqlConnection(_connectionString);
            var produto = await connection.QueryFirstOrDefaultAsync<CardapioProduto>(sql, new
            {
                IdEstabelecimento = idEstabelecimento,
                Slug = ValidationUtils.TrimToNull(slug)
            });

            if (produto == null)
            {
                return null;
            }

            await PreencherGruposPublicosDeProdutosAsync(connection, new[] { produto });
            return produto;
        }

        public async Task<Guid> CriarProdutoAsync(CardapioProduto entity)
        {
            const string sql = @"
INSERT INTO cardapio_produto (
    id,
    id_estabelecimento,
    categoria_id,
    nome,
    slug,
    emoji,
    descricao,
    descricao_curta,
    preco_base,
    preco_de,
    badge_desconto,
    is_club,
    imagem_url,
    eco_friendly,
    extras_titulo,
    extras_subtitulo,
    ordem,
    ativo,
    destaque,
    disponivel,
    publico_web,
    created_at,
    updated_at
) VALUES (
    @Id,
    @IdEstabelecimento,
    @CategoriaId,
    @Nome,
    @Slug,
    @Emoji,
    @Descricao,
    @DescricaoCurta,
    @PrecoBase,
    @PrecoDe,
    @BadgeDesconto,
    @IsClub,
    @ImagemUrl,
    @EcoFriendly,
    @ExtrasTitulo,
    @ExtrasSubtitulo,
    @Ordem,
    @Ativo,
    @Destaque,
    @Disponivel,
    @PublicoWeb,
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
                entity.Slug = await GerarSlugProdutoAsync(connection, transaction, entity.IdEstabelecimento, entity.Slug, entity.Nome, null);
                await connection.ExecuteAsync(sql, entity, transaction);
                await SubstituirProdutoGruposAsync(connection, transaction, entity.Id, entity.GrupoIds);
                await transaction.CommitAsync();
                return entity.Id;
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

        public async Task<bool> AtualizarProdutoAsync(CardapioProduto entity)
        {
            const string sql = @"
UPDATE cardapio_produto
   SET categoria_id = @CategoriaId,
       nome = @Nome,
       slug = @Slug,
       emoji = @Emoji,
       descricao = @Descricao,
       descricao_curta = @DescricaoCurta,
       preco_base = @PrecoBase,
       preco_de = @PrecoDe,
       badge_desconto = @BadgeDesconto,
       is_club = @IsClub,
       imagem_url = @ImagemUrl,
       eco_friendly = @EcoFriendly,
       extras_titulo = @ExtrasTitulo,
       extras_subtitulo = @ExtrasSubtitulo,
       ordem = @Ordem,
       ativo = @Ativo,
       destaque = @Destaque,
       disponivel = @Disponivel,
       publico_web = @PublicoWeb,
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
                entity.Slug = await GerarSlugProdutoAsync(connection, transaction, entity.IdEstabelecimento, entity.Slug, entity.Nome, entity.Id);
                var affected = await connection.ExecuteAsync(sql, entity, transaction);
                if (affected <= 0)
                {
                    await transaction.RollbackAsync();
                    return false;
                }

                await SubstituirProdutoGruposAsync(connection, transaction, entity.Id, entity.GrupoIds);
                await transaction.CommitAsync();
                return true;
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

        public async Task<bool> AtualizarProdutoStatusAsync(Guid idEstabelecimento, Guid id, bool ativo)
        {
            const string sql = @"
UPDATE cardapio_produto
   SET ativo = @Ativo,
       updated_at = NOW()
 WHERE id_estabelecimento = @IdEstabelecimento
   AND id = @Id
   AND deleted_at IS NULL;";

            await using var connection = new NpgsqlConnection(_connectionString);
            return await connection.ExecuteAsync(sql, new
            {
                IdEstabelecimento = idEstabelecimento,
                Id = id,
                Ativo = ativo
            }) > 0;
        }

        public async Task<bool> AtualizarProdutoDisponibilidadeAsync(Guid idEstabelecimento, Guid id, bool disponivel)
        {
            const string sql = @"
UPDATE cardapio_produto
   SET disponivel = @Disponivel,
       updated_at = NOW()
 WHERE id_estabelecimento = @IdEstabelecimento
   AND id = @Id
   AND deleted_at IS NULL;";

            await using var connection = new NpgsqlConnection(_connectionString);
            return await connection.ExecuteAsync(sql, new
            {
                IdEstabelecimento = idEstabelecimento,
                Id = id,
                Disponivel = disponivel
            }) > 0;
        }

        public async Task<bool> ExcluirProdutoAsync(Guid idEstabelecimento, Guid id)
        {
            const string sql = @"
UPDATE cardapio_produto
   SET deleted_at = NOW(),
       updated_at = NOW()
 WHERE id_estabelecimento = @IdEstabelecimento
   AND id = @Id
   AND deleted_at IS NULL;";

            await using var connection = new NpgsqlConnection(_connectionString);
            return await connection.ExecuteAsync(sql, new
            {
                IdEstabelecimento = idEstabelecimento,
                Id = id
            }) > 0;
        }

        public async Task<Guid> CriarPedidoPublicoAsync(CardapioPedidoPublico entity)
        {
            const string sql = @"
INSERT INTO cardapio_pedido_publico (
    id,
    id_estabelecimento,
    codigo,
    status,
    tipo_entrega,
    nome_cliente,
    telefone_cliente,
    email_cliente,
    forma_pagamento,
    observacoes,
    subtotal_produtos,
    subtotal_adicionais,
    taxa_entrega,
    total,
    itens_json,
    endereco_entrega_json,
    status_pagamento,
    created_at,
    updated_at
) VALUES (
    @Id,
    @IdEstabelecimento,
    @Codigo,
    @Status,
    @TipoEntrega,
    @NomeCliente,
    @TelefoneCliente,
    @EmailCliente,
    @FormaPagamento,
    @Observacoes,
    @SubtotalProdutos,
    @SubtotalAdicionais,
    @TaxaEntrega,
    @Total,
    CAST(@ItensJson AS jsonb),
    CAST(@EnderecoEntregaJson AS jsonb),
    @StatusPagamento,
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
            try
            {
                await connection.ExecuteAsync(sql, entity);
                return entity.Id;
            }
            catch (PostgresException ex) when (ex.SqlState == UniqueViolation)
            {
                throw new InvalidOperationException(ResolveUniqueConstraintMessage(ex));
            }
        }

        private static object BuildBuscaParameters(Guid idEstabelecimento, string? busca, int page, int pageSize, bool? ativo)
        {
            var normalizedBusca = ValidationUtils.TrimToNull(busca);
            var normalizedPage = page <= 0 ? 1 : page;
            var normalizedPageSize = pageSize <= 0 ? 20 : pageSize;

            return new
            {
                IdEstabelecimento = idEstabelecimento,
                Busca = normalizedBusca,
                BuscaLike = normalizedBusca == null ? null : $"%{normalizedBusca}%",
                Ativo = ativo,
                PageSize = normalizedPageSize,
                Offset = (normalizedPage - 1) * normalizedPageSize
            };
        }

        private static async Task PreencherItensDeGrupoAsync(NpgsqlConnection connection, IEnumerable<CardapioGrupoAdicional> grupos)
        {
            var list = grupos?.ToList() ?? new List<CardapioGrupoAdicional>();
            if (list.Count == 0)
            {
                return;
            }

            const string sql = @"
SELECT id,
       id_grupo AS IdGrupo,
       nome,
       descricao,
       preco,
       ordem,
       ativo,
       created_at AS CreatedAt,
       updated_at AS UpdatedAt
  FROM cardapio_grupo_adicional_item
 WHERE id_grupo = ANY(@Ids)
 ORDER BY ordem ASC, nome ASC;";

            var items = await connection.QueryAsync<CardapioGrupoAdicionalItem>(sql, new
            {
                Ids = list.Select(x => x.Id).Distinct().ToArray()
            });

            var lookup = items
                .GroupBy(x => x.IdGrupo)
                .ToDictionary(group => group.Key, group => group.ToList());

            foreach (var grupo in list)
            {
                grupo.Itens = lookup.TryGetValue(grupo.Id, out var grupoItens)
                    ? grupoItens
                    : new List<CardapioGrupoAdicionalItem>();
            }
        }

        private static async Task PreencherGrupoIdsDeProdutosAsync(NpgsqlConnection connection, IEnumerable<CardapioProduto> produtos)
        {
            var list = produtos?.ToList() ?? new List<CardapioProduto>();
            if (list.Count == 0)
            {
                return;
            }

            const string sql = @"
SELECT id_produto AS ProdutoId,
       id_grupo AS GrupoId
  FROM cardapio_produto_grupo
 WHERE id_produto = ANY(@Ids)
 ORDER BY ordem ASC, id_grupo ASC;";

            var links = await connection.QueryAsync<ProdutoGrupoLinkRow>(sql, new
            {
                Ids = list.Select(x => x.Id).Distinct().ToArray()
            });

            var lookup = links
                .GroupBy(x => x.ProdutoId)
                .ToDictionary(group => group.Key, group => group.Select(item => item.GrupoId).ToList());

            foreach (var produto in list)
            {
                produto.GrupoIds = lookup.TryGetValue(produto.Id, out var grupoIds)
                    ? grupoIds
                    : new List<Guid>();
            }
        }

        private static async Task PreencherGruposPublicosDeProdutosAsync(NpgsqlConnection connection, IEnumerable<CardapioProduto> produtos)
        {
            var list = produtos?.ToList() ?? new List<CardapioProduto>();
            if (list.Count == 0)
            {
                return;
            }

            const string sql = @"
SELECT pg.id_produto AS ProdutoId,
       g.id AS GrupoId,
       g.id_estabelecimento AS IdEstabelecimento,
       g.nome,
       COALESCE(g.tipo, 'adicional_global') AS Tipo,
       g.descricao,
       g.min_selecionados AS MinSelecionados,
       g.max_selecionados AS MaxSelecionados,
       COALESCE(pg.ordem, g.ordem) AS Ordem,
       g.ativo,
       g.created_at AS CreatedAt,
       g.updated_at AS UpdatedAt,
       g.deleted_at AS DeletedAt,
       i.id AS ItemId,
       i.id_grupo AS IdGrupo,
       i.nome AS ItemNome,
       i.descricao AS ItemDescricao,
       i.preco AS ItemPreco,
       i.ordem AS ItemOrdem,
       i.ativo AS ItemAtivo,
       i.created_at AS ItemCreatedAt,
       i.updated_at AS ItemUpdatedAt
  FROM cardapio_produto_grupo pg
  JOIN cardapio_grupo_adicional g
    ON g.id = pg.id_grupo
   AND g.deleted_at IS NULL
   AND g.ativo = TRUE
  LEFT JOIN cardapio_grupo_adicional_item i
    ON i.id_grupo = g.id
   AND i.ativo = TRUE
 WHERE pg.id_produto = ANY(@Ids)
 ORDER BY pg.id_produto ASC, COALESCE(pg.ordem, g.ordem) ASC, g.nome ASC, i.ordem ASC, i.nome ASC;";

            var rows = (await connection.QueryAsync<ProdutoGrupoPublicoRow>(sql, new
            {
                Ids = list.Select(x => x.Id).Distinct().ToArray()
            })).ToList();

            var produtosPorId = list.ToDictionary(x => x.Id);

            foreach (var produto in list)
            {
                produto.GrupoIds = new List<Guid>();
                produto.Grupos = new List<CardapioGrupoAdicional>();
            }

            foreach (var row in rows)
            {
                if (!produtosPorId.TryGetValue(row.ProdutoId, out var produto))
                {
                    continue;
                }

                var grupo = produto.Grupos.FirstOrDefault(x => x.Id == row.GrupoId);
                if (grupo == null)
                {
                    grupo = new CardapioGrupoAdicional
                    {
                        Id = row.GrupoId,
                        IdEstabelecimento = row.IdEstabelecimento,
                        Nome = row.Nome ?? string.Empty,
                        Tipo = row.Tipo ?? "adicional_global",
                        Descricao = row.Descricao,
                        MinSelecionados = row.MinSelecionados,
                        MaxSelecionados = row.MaxSelecionados,
                        Ordem = row.Ordem,
                        Ativo = row.Ativo,
                        CreatedAt = row.CreatedAt,
                        UpdatedAt = row.UpdatedAt,
                        DeletedAt = row.DeletedAt,
                        Itens = new List<CardapioGrupoAdicionalItem>()
                    };

                    produto.Grupos.Add(grupo);
                    produto.GrupoIds.Add(row.GrupoId);
                }

                if (row.ItemId.HasValue)
                {
                    grupo.Itens.Add(new CardapioGrupoAdicionalItem
                    {
                        Id = row.ItemId.Value,
                        IdGrupo = row.IdGrupo ?? row.GrupoId,
                        Nome = row.ItemNome ?? string.Empty,
                        Descricao = row.ItemDescricao,
                        Preco = row.ItemPreco ?? 0,
                        Ordem = row.ItemOrdem ?? 0,
                        Ativo = row.ItemAtivo ?? true,
                        CreatedAt = row.ItemCreatedAt ?? DateTime.UtcNow,
                        UpdatedAt = row.ItemUpdatedAt ?? DateTime.UtcNow
                    });
                }
            }

            foreach (var produto in list)
            {
                produto.Grupos = produto.Grupos
                    .OrderBy(x => x.Ordem)
                    .ThenBy(x => x.Nome)
                    .ToList();
            }
        }

        private static async Task SincronizarItensDeGrupoAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CardapioGrupoAdicional entity)
        {
            var itens = (entity.Itens ?? new List<CardapioGrupoAdicionalItem>())
                .Select(item =>
                {
                    item.Id = item.Id == Guid.Empty ? Guid.NewGuid() : item.Id;
                    item.IdGrupo = entity.Id;
                    return item;
                })
                .ToList();

            var requestedIds = itens.Select(item => item.Id).Distinct().ToArray();

            await connection.ExecuteAsync(@"
DELETE FROM cardapio_grupo_adicional_item
 WHERE id_grupo = @IdGrupo
   AND (@KeepAll = TRUE OR id <> ALL(@Ids));",
                new
                {
                    IdGrupo = entity.Id,
                    KeepAll = requestedIds.Length == 0,
                    Ids = requestedIds
                },
                transaction);

            const string sqlUpdate = @"
UPDATE cardapio_grupo_adicional_item
   SET nome = @Nome,
       descricao = @Descricao,
       preco = @Preco,
       ordem = @Ordem,
       ativo = @Ativo,
       updated_at = NOW()
 WHERE id = @Id
   AND id_grupo = @IdGrupo;";

            const string sqlInsert = @"
INSERT INTO cardapio_grupo_adicional_item (
    id,
    id_grupo,
    nome,
    descricao,
    preco,
    ordem,
    ativo,
    created_at,
    updated_at
) VALUES (
    @Id,
    @IdGrupo,
    @Nome,
    @Descricao,
    @Preco,
    @Ordem,
    @Ativo,
    NOW(),
    NOW()
);";

            foreach (var item in itens)
            {
                var updated = await connection.ExecuteAsync(sqlUpdate, item, transaction);
                if (updated <= 0)
                {
                    await connection.ExecuteAsync(sqlInsert, item, transaction);
                }
            }
        }

        private static async Task SubstituirProdutoGruposAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            Guid produtoId,
            IReadOnlyCollection<Guid>? grupoIds)
        {
            await connection.ExecuteAsync(
                "DELETE FROM cardapio_produto_grupo WHERE id_produto = @IdProduto;",
                new { IdProduto = produtoId },
                transaction);

            var distinctIds = (grupoIds ?? Array.Empty<Guid>())
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToList();

            if (distinctIds.Count == 0)
            {
                return;
            }

            const string sql = @"
INSERT INTO cardapio_produto_grupo (
    id_produto,
    id_grupo,
    ordem,
    created_at
) VALUES (
    @IdProduto,
    @IdGrupo,
    @Ordem,
    NOW()
);";

            for (var index = 0; index < distinctIds.Count; index++)
            {
                await connection.ExecuteAsync(sql, new
                {
                    IdProduto = produtoId,
                    IdGrupo = distinctIds[index],
                    Ordem = index + 1
                }, transaction);
            }
        }

        private async Task<string> GerarSlugCategoriaAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            Guid idEstabelecimento,
            string? preferredSlug,
            string nome,
            Guid? ignoreId)
        {
            return await GerarSlugUnicoAsync(connection, transaction, "cardapio_categoria", idEstabelecimento, preferredSlug, nome, ignoreId, "categoria");
        }

        private async Task<string> GerarSlugProdutoAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            Guid idEstabelecimento,
            string? preferredSlug,
            string nome,
            Guid? ignoreId)
        {
            return await GerarSlugUnicoAsync(connection, transaction, "cardapio_produto", idEstabelecimento, preferredSlug, nome, ignoreId, "produto");
        }

        private static async Task<string> GerarSlugUnicoAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string tableName,
            Guid idEstabelecimento,
            string? preferredSlug,
            string nome,
            Guid? ignoreId,
            string fallbackPrefix)
        {
            var baseSlug = Slugify(string.IsNullOrWhiteSpace(preferredSlug) ? nome : preferredSlug);
            if (string.IsNullOrWhiteSpace(baseSlug))
            {
                baseSlug = fallbackPrefix;
            }

            var sql = $@"
SELECT slug
  FROM {tableName}
 WHERE id_estabelecimento = @IdEstabelecimento
   AND deleted_at IS NULL
   AND (slug = @SlugBase OR slug LIKE @SlugLike)
   AND (@IgnoreId IS NULL OR id <> @IgnoreId);";

            var existentes = (await connection.QueryAsync<string>(sql, new
            {
                IdEstabelecimento = idEstabelecimento,
                SlugBase = baseSlug,
                SlugLike = $"{baseSlug}-%",
                IgnoreId = ignoreId
            }, transaction))
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item.Trim())
                .ToList();

            if (existentes.Count == 0 || existentes.All(item => !string.Equals(item, baseSlug, StringComparison.Ordinal)))
            {
                return baseSlug;
            }

            var maiorSufixo = 1;
            foreach (var existente in existentes)
            {
                if (!existente.StartsWith(baseSlug + "-", StringComparison.Ordinal))
                {
                    continue;
                }

                var sufixoTexto = existente[(baseSlug.Length + 1)..];
                if (int.TryParse(sufixoTexto, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sufixoNumero))
                {
                    maiorSufixo = Math.Max(maiorSufixo, sufixoNumero);
                }
            }

            return $"{baseSlug}-{maiorSufixo + 1}";
        }

        private static string ResolveUniqueConstraintMessage(PostgresException ex)
        {
            return ex.ConstraintName switch
            {
                "ux_cardapio_categoria_estab_slug" => "Ja existe uma categoria com este slug neste estabelecimento.",
                "ux_cardapio_produto_estab_slug" => "Ja existe um produto com este slug neste estabelecimento.",
                "ux_cardapio_pedido_publico_codigo" => "Nao foi possivel gerar um codigo unico para o pedido.",
                _ => "Nao foi possivel salvar o registro devido a um conflito de unicidade."
            };
        }

        private static string Slugify(string value)
        {
            var normalized = ValidationUtils.NormalizeToken(value);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return string.Empty;
            }

            normalized = SlugRegex.Replace(normalized, "-");
            return normalized.Trim('-');
        }

        private sealed class ProdutoGrupoLinkRow
        {
            public Guid ProdutoId { get; set; }
            public Guid GrupoId { get; set; }
        }

        private sealed class ProdutoGrupoPublicoRow
        {
            public Guid ProdutoId { get; set; }
            public Guid GrupoId { get; set; }
            public Guid IdEstabelecimento { get; set; }
            public string? Nome { get; set; }
            public string? Tipo { get; set; }
            public string? Descricao { get; set; }
            public int MinSelecionados { get; set; }
            public int MaxSelecionados { get; set; }
            public int Ordem { get; set; }
            public bool Ativo { get; set; }
            public DateTime CreatedAt { get; set; }
            public DateTime UpdatedAt { get; set; }
            public DateTime? DeletedAt { get; set; }
            public Guid? ItemId { get; set; }
            public Guid? IdGrupo { get; set; }
            public string? ItemNome { get; set; }
            public string? ItemDescricao { get; set; }
            public decimal? ItemPreco { get; set; }
            public int? ItemOrdem { get; set; }
            public bool? ItemAtivo { get; set; }
            public DateTime? ItemCreatedAt { get; set; }
            public DateTime? ItemUpdatedAt { get; set; }
        }
    }
}
