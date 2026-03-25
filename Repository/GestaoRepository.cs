using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using APIBack.DTOs.Gestao;
using APIBack.Model.Gestao;
using APIBack.Repository.Interface;
using APIBack.Service;
using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace APIBack.Repository
{
    public class GestaoRepository : IGestaoRepository
    {
        private static readonly Regex SlugRegex = new("[^a-z0-9]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private readonly string _connectionString;

        public GestaoRepository(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection")
                ?? configuration["ConnectionStrings:DefaultConnection"]
                ?? throw new InvalidOperationException("Connection string 'DefaultConnection' nao configurada.");
        }

        public async Task<IReadOnlyCollection<GestaoEmpresaRow>> ListarEmpresasAsync(Guid? empresaId)
        {
            const string sql = @"
SELECT  emp.id                    AS Id,
        emp.id_int                AS IdInt,
        emp.nome_fantasia         AS NomeFantasia,
        emp.razao_social          AS RazaoSocial,
        emp.cnpj                  AS Cnpj,
        emp.email                 AS Email,
        emp.telefone              AS Telefone,
        emp.site                  AS Site,
        COALESCE(
            MAX(NULLIF(e.cidade, '')) FILTER (WHERE COALESCE(e.ativo, TRUE) = TRUE AND COALESCE(e.status, 'ativo') = 'ativo'),
            MAX(NULLIF(e.cidade, ''))
        )                         AS CidadeBase,
        CASE
            WHEN EXISTS (
                SELECT 1
                  FROM estabelecimentos e2
                 WHERE e2.id_empresa = emp.id
                   AND e2.tipo_unidade = 'matriz'
                   AND lower(COALESCE(e2.nome_fantasia, '')) LIKE '%centro%'
            ) THEN 'centro'
            ELSE 'empresa'
        END                       AS TipoOrganizacao,
        COALESCE(BOOL_OR(COALESCE(e.ativo, TRUE) = TRUE AND COALESCE(e.status, 'ativo') = 'ativo'), TRUE) AS Ativa,
        emp.data_criacao          AS CreatedAt,
        emp.data_atualizacao      AS UpdatedAt
  FROM empresas emp
  LEFT JOIN estabelecimentos e ON e.id_empresa = emp.id
 WHERE (@EmpresaId IS NULL OR emp.id = @EmpresaId)
 GROUP BY emp.id, emp.id_int, emp.nome_fantasia, emp.razao_social, emp.cnpj, emp.email, emp.telefone, emp.site, emp.data_criacao, emp.data_atualizacao
 ORDER BY emp.nome_fantasia;";

            await using var connection = new NpgsqlConnection(_connectionString);
            var rows = await connection.QueryAsync<GestaoEmpresaRow>(sql, new { EmpresaId = empresaId });
            return rows.ToArray();
        }

        public async Task<GestaoEmpresaRow?> ObterEmpresaAsync(Guid empresaId)
        {
            return (await ListarEmpresasAsync(empresaId)).FirstOrDefault();
        }

        public async Task<Guid> CriarEmpresaAsync(SalvarEmpresaRequest request)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            var empresaId = await InserirEmpresaAsync(connection, transaction, request);

            await transaction.CommitAsync();
            return empresaId;
        }

        public async Task<Guid> CriarEmpresaComEstabelecimentoAsync(
            SalvarEmpresaRequest request,
            SalvarEstabelecimentoRequest estabelecimentoRequest)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            var empresaId = await InserirEmpresaAsync(connection, transaction, request);
            await InserirEstabelecimentoAsync(connection, transaction, estabelecimentoRequest, empresaId, "initialEstablishment.tipoEstabelecimentoSlug");

            await transaction.CommitAsync();
            return empresaId;
        }

        public async Task AtualizarEmpresaAsync(Guid empresaId, SalvarEmpresaRequest request)
        {
            const string sql = @"
UPDATE empresas
   SET nome_fantasia    = @NomeFantasia,
       razao_social     = @RazaoSocial,
       cnpj             = @Cnpj,
       telefone         = @Telefone,
       email            = @Email,
       site             = @Site,
       timezone_iana    = @TimezoneIana,
       data_atualizacao = NOW()
 WHERE id = @EmpresaId;";

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.ExecuteAsync(sql, new
            {
                EmpresaId = empresaId,
                NomeFantasia = NormalizeText(request.NomeFantasia),
                RazaoSocial = NormalizeNullable(request.RazaoSocial),
                Cnpj = NormalizeDocumentDigits(request.Cnpj),
                Telefone = NormalizeNullable(request.Telefone),
                Email = NormalizeNullable(request.Email),
                Site = NormalizeNullable(request.Site),
                TimezoneIana = "America/Sao_Paulo"
            });
        }

        public async Task AtualizarStatusEmpresaAsync(Guid empresaId, bool ativa)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            if (ativa)
            {
                await connection.ExecuteAsync(@"
UPDATE estabelecimentos
   SET ativo = TRUE,
       data_atualizacao = NOW()
 WHERE id_empresa = @EmpresaId
   AND COALESCE(ativo, TRUE) = FALSE
   AND COALESCE(status, 'ativo') IN ('ativo', 'trial');", new { EmpresaId = empresaId }, transaction);

                await connection.ExecuteAsync(@"
UPDATE usuario_estabelecimentos ue
   SET ativo = TRUE,
       data_remocao = NULL,
       updated_at = NOW()
  FROM estabelecimentos e
 WHERE e.id = ue.id_estabelecimento
   AND e.id_empresa = @EmpresaId
   AND COALESCE(e.ativo, TRUE) = TRUE
   AND COALESCE(e.status, 'ativo') IN ('ativo', 'trial')
   AND COALESCE(ue.ativo, TRUE) = FALSE
   AND COALESCE(ue.status, 'ativo') = 'ativo';", new { EmpresaId = empresaId }, transaction);

                await connection.ExecuteAsync(@"
UPDATE usuario_empresas
   SET ativo = TRUE,
       data_remocao = NULL,
       updated_at = NOW()
 WHERE id_empresa = @EmpresaId
   AND COALESCE(ativo, TRUE) = FALSE
   AND COALESCE(status, 'ativo') = 'ativo';", new { EmpresaId = empresaId }, transaction);
            }
            else
            {
                await connection.ExecuteAsync(@"
UPDATE usuario_estabelecimentos ue
   SET ativo = FALSE,
       updated_at = NOW()
  FROM estabelecimentos e
 WHERE e.id = ue.id_estabelecimento
   AND e.id_empresa = @EmpresaId
   AND COALESCE(ue.ativo, TRUE) = TRUE
   AND COALESCE(ue.status, 'ativo') = 'ativo';", new { EmpresaId = empresaId }, transaction);

                await connection.ExecuteAsync(@"
UPDATE usuario_empresas
   SET ativo = FALSE,
       updated_at = NOW()
 WHERE id_empresa = @EmpresaId
   AND COALESCE(ativo, TRUE) = TRUE
   AND COALESCE(status, 'ativo') = 'ativo';", new { EmpresaId = empresaId }, transaction);

                await connection.ExecuteAsync(@"
UPDATE estabelecimentos
   SET ativo = FALSE,
       data_atualizacao = NOW()
 WHERE id_empresa = @EmpresaId
   AND COALESCE(ativo, TRUE) = TRUE
   AND COALESCE(status, 'ativo') IN ('ativo', 'trial');", new { EmpresaId = empresaId }, transaction);
            }

            await transaction.CommitAsync();
        }

        public async Task<IReadOnlyCollection<GestaoEstabelecimentoRow>> ListarEstabelecimentosAsync(Guid? empresaId, Guid? estabelecimentoId)
        {
            const string sql = @"
SELECT  e.id                      AS Id,
        e.id_empresa              AS EmpresaId,
        e.tipo_unidade::text      AS TipoUnidade,
        e.nome_fantasia           AS NomeFantasia,
        e.cnpj_loja               AS CnpjLoja,
        e.inscricao_estadual      AS InscricaoEstadual,
        e.inscricao_municipal     AS InscricaoMunicipal,
        e.telefone                AS Telefone,
        e.whatsapp_e164           AS WhatsappE164,
        e.email                   AS Email,
        e.logradouro              AS Logradouro,
        e.numero                  AS Numero,
        e.complemento             AS Complemento,
        e.bairro                  AS Bairro,
        e.cidade                  AS Cidade,
        e.uf                      AS Uf,
        e.cep                     AS Cep,
        e.latitude                AS Latitude,
        e.longitude               AS Longitude,
        e.url_logo                AS UrlLogo,
        e.aceita_pedidos          AS AceitaPedidos,
        e.timezone_iana           AS TimezoneIana,
        e.raio_entrega_km         AS RaioEntregaKm,
        e.pedido_minimo           AS PedidoMinimo,
        e.taxa_entrega_fixa       AS TaxaEntregaFixa,
        e.taxa_entrega_por_km     AS TaxaEntregaPorKm,
        e.tempo_preparo_min       AS TempoPreparoMin,
        e.data_criacao            AS CreatedAt,
        e.data_atualizacao        AS UpdatedAt,
        e.id_tipo_estabelecimento AS TipoEstabelecimentoId,
        te.slug                   AS TipoEstabelecimentoSlug,
        te.nome                   AS TipoEstabelecimentoNome,
        e.slug                    AS Slug,
        e.modulos_ativos::text[]  AS ModulosAtivosRaw,
        e.ativo                   AS Ativo,
        COALESCE(e.status, 'ativo') AS Status
  FROM estabelecimentos e
  LEFT JOIN tipo_estabelecimento te ON te.id = e.id_tipo_estabelecimento
 WHERE (@EmpresaId IS NULL OR e.id_empresa = @EmpresaId)
   AND (@EstabelecimentoId IS NULL OR e.id = @EstabelecimentoId)
 ORDER BY e.nome_fantasia;";

            await using var connection = new NpgsqlConnection(_connectionString);
            var rows = await connection.QueryAsync<GestaoEstabelecimentoRow>(sql, new
            {
                EmpresaId = empresaId,
                EstabelecimentoId = estabelecimentoId
            });
            return rows.ToArray();
        }

        public async Task<GestaoEstabelecimentoRow?> ObterEstabelecimentoAsync(Guid estabelecimentoId)
        {
            return (await ListarEstabelecimentosAsync(null, estabelecimentoId)).FirstOrDefault();
        }

        public async Task<IReadOnlyCollection<GestaoTargetEstabelecimento>> ListarEstabelecimentosPorIdsAsync(IReadOnlyCollection<Guid> estabelecimentoIds)
        {
            if (estabelecimentoIds == null || estabelecimentoIds.Count == 0)
            {
                return Array.Empty<GestaoTargetEstabelecimento>();
            }

            const string sql = @"
SELECT  e.id                      AS Id,
        e.id_empresa              AS EmpresaId,
        emp.nome_fantasia         AS EmpresaNome,
        e.nome_fantasia           AS Nome,
        te.nome                   AS TipoEstabelecimentoNome,
        te.slug                   AS TipoEstabelecimentoSlug,
        COALESCE(e.ativo, TRUE)   AS Ativo,
        COALESCE(e.status, 'ativo') AS Status,
        e.modulos_ativos::text[]  AS ModulosAtivosRaw
  FROM estabelecimentos e
  JOIN empresas emp ON emp.id = e.id_empresa
  LEFT JOIN tipo_estabelecimento te ON te.id = e.id_tipo_estabelecimento
 WHERE e.id = ANY(@Ids);";

            await using var connection = new NpgsqlConnection(_connectionString);
            var rows = await connection.QueryAsync<GestaoTargetEstabelecimento>(sql, new
            {
                Ids = estabelecimentoIds.ToArray()
            });
            return rows.ToArray();
        }

        public async Task<Guid> CriarEstabelecimentoAsync(SalvarEstabelecimentoRequest request, Guid? empresaIdOverride = null)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            var id = await InserirEstabelecimentoAsync(connection, transaction, request, empresaIdOverride, "tipoEstabelecimentoSlug");

            await transaction.CommitAsync();
            return id;
        }

        public async Task AtualizarEstabelecimentoAsync(Guid estabelecimentoId, SalvarEstabelecimentoRequest request)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            var tipoEstabelecimentoId = await ResolverTipoEstabelecimentoIdAsync(connection, transaction, request, "tipoEstabelecimentoSlug");
            var tipoEstabelecimentoSlug = await ObterTipoEstabelecimentoSlugAsync(connection, transaction, tipoEstabelecimentoId)
                ?? NormalizeTypeSlug(request.TipoEstabelecimentoSlug, request.TipoEstabelecimentoNome);
            var slugAtual = await connection.ExecuteScalarAsync<string?>(@"
SELECT slug
  FROM estabelecimentos
 WHERE id = @EstabelecimentoId
 LIMIT 1
 FOR UPDATE;", new
            {
                EstabelecimentoId = estabelecimentoId
            }, transaction);
            var slugPreferencial = string.IsNullOrWhiteSpace(request.Slug) ? slugAtual : request.Slug;
            var slug = await GarantirSlugDisponivelAsync(connection, transaction, slugPreferencial, request.NomeFantasia, estabelecimentoId);
            var modulos = EstabelecimentoModuleMapper.ToDatabaseModules(request.ModulosAtivos, tipoEstabelecimentoSlug);
            var tipoUnidade = NormalizeTipoUnidade(request.TipoUnidade);

            const string sql = @"
UPDATE estabelecimentos
   SET id_empresa = @EmpresaId,
       tipo_unidade = @TipoUnidade::tipo_unidade_enum,
       nome_fantasia = @NomeFantasia,
       cnpj_loja = @CnpjLoja,
       inscricao_estadual = @InscricaoEstadual,
       inscricao_municipal = @InscricaoMunicipal,
       telefone = @Telefone,
       whatsapp_e164 = @WhatsappE164,
       email = @Email,
       logradouro = @Logradouro,
       numero = @Numero,
       complemento = @Complemento,
       bairro = @Bairro,
       cidade = @Cidade,
       uf = @Uf,
       cep = @Cep,
       latitude = @Latitude,
       longitude = @Longitude,
       url_logo = @UrlLogo,
       aceita_pedidos = @AceitaPedidos,
       timezone_iana = @TimezoneIana,
       raio_entrega_km = @RaioEntregaKm,
       pedido_minimo = @PedidoMinimo,
       taxa_entrega_fixa = @TaxaEntregaFixa,
       taxa_entrega_por_km = @TaxaEntregaPorKm,
       tempo_preparo_min = @TempoPreparoMin,
       modulos_ativos = @ModulosAtivos::modulo_enum[],
       id_tipo_estabelecimento = @TipoEstabelecimentoId,
       slug = @Slug,
       data_atualizacao = NOW()
 WHERE id = @EstabelecimentoId;";

            await connection.ExecuteAsync(sql, new
            {
                EstabelecimentoId = estabelecimentoId,
                EmpresaId = request.EmpresaId,
                TipoUnidade = tipoUnidade,
                NomeFantasia = NormalizeText(request.NomeFantasia),
                CnpjLoja = NormalizeDocumentDigits(request.CnpjLoja),
                InscricaoEstadual = NormalizeNullable(request.InscricaoEstadual),
                InscricaoMunicipal = NormalizeNullable(request.InscricaoMunicipal),
                Telefone = NormalizeNullable(request.Telefone),
                WhatsappE164 = NormalizeWhatsappE164(request.WhatsappE164),
                Email = NormalizeNullable(request.Email),
                Logradouro = NormalizeNullable(request.Logradouro),
                Numero = NormalizeNullable(request.Numero),
                Complemento = NormalizeNullable(request.Complemento),
                Bairro = NormalizeNullable(request.Bairro),
                Cidade = NormalizeNullable(request.Cidade),
                Uf = NormalizeNullable(request.Uf)?.ToUpperInvariant(),
                Cep = NormalizeNullable(request.Cep),
                Latitude = request.Latitude,
                Longitude = request.Longitude,
                UrlLogo = NormalizeNullable(request.UrlLogo),
                AceitaPedidos = request.AceitaPedidos ?? true,
                TimezoneIana = NormalizeNullable(request.TimezoneIana) ?? "America/Sao_Paulo",
                RaioEntregaKm = request.RaioEntregaKm ?? 0m,
                PedidoMinimo = request.PedidoMinimo ?? 0m,
                TaxaEntregaFixa = request.TaxaEntregaFixa ?? 0m,
                TaxaEntregaPorKm = request.TaxaEntregaPorKm ?? 0m,
                TempoPreparoMin = request.TempoPreparoMin ?? 20,
                ModulosAtivos = modulos,
                TipoEstabelecimentoId = tipoEstabelecimentoId,
                Slug = slug
            }, transaction);

            await transaction.CommitAsync();
        }

        public async Task AtualizarStatusEstabelecimentoAsync(Guid estabelecimentoId, bool ativa)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            var empresaId = await connection.ExecuteScalarAsync<Guid?>(@"
SELECT id_empresa
  FROM estabelecimentos
 WHERE id = @EstabelecimentoId
 LIMIT 1
 FOR UPDATE;", new { EstabelecimentoId = estabelecimentoId }, transaction);

            if (!empresaId.HasValue)
            {
                await transaction.RollbackAsync();
                return;
            }

            await connection.ExecuteAsync(@"
UPDATE estabelecimentos
   SET ativo = @Ativa,
       status = CASE
                    WHEN @Ativa AND COALESCE(status, 'ativo') = 'trial' THEN 'trial'
                    WHEN @Ativa THEN 'ativo'
                    ELSE 'suspenso'
                END,
       data_atualizacao = NOW()
 WHERE id = @EstabelecimentoId;", new { EstabelecimentoId = estabelecimentoId, Ativa = ativa }, transaction);

            if (ativa)
            {
                await connection.ExecuteAsync(@"
UPDATE usuario_estabelecimentos
   SET ativo = TRUE,
       data_remocao = NULL,
       updated_at = NOW()
 WHERE id_estabelecimento = @EstabelecimentoId
   AND COALESCE(ativo, TRUE) = FALSE
   AND COALESCE(status, 'ativo') = 'ativo';", new { EstabelecimentoId = estabelecimentoId }, transaction);
            }
            else
            {
                await connection.ExecuteAsync(@"
UPDATE usuario_estabelecimentos
   SET ativo = FALSE,
       updated_at = NOW()
 WHERE id_estabelecimento = @EstabelecimentoId
   AND COALESCE(ativo, TRUE) = TRUE
   AND COALESCE(status, 'ativo') = 'ativo';", new { EstabelecimentoId = estabelecimentoId }, transaction);
            }

            await SincronizarVinculosEmpresaPorEstabelecimentosAsync(connection, transaction, empresaId.Value);
            await transaction.CommitAsync();
        }

        public async Task<IReadOnlyCollection<GestaoUsuarioResumoRow>> ListarUsuariosResumoAsync(
            Guid? empresaId,
            Guid? estabelecimentoId,
            bool incluirSuperAdminsGlobais)
        {
            const string sql = @"
SELECT DISTINCT
       u.id                   AS Id,
       COALESCE(u.nome, '')   AS Nome,
       COALESCE(u.email, '')  AS Email,
       COALESCE(u.is_super_admin, FALSE) AS IsSuperAdmin,
       u.created_at           AS CreatedAt,
       u.deleted_at           AS DeletedAt
  FROM usuario u
 WHERE (@IncluirSuperAdminsGlobais = TRUE AND COALESCE(u.is_super_admin, FALSE) = TRUE)
    OR EXISTS (
        SELECT 1
          FROM usuario_empresas uemp
         WHERE uemp.id_usuario = u.id
           AND COALESCE(uemp.ativo, TRUE) = TRUE
           AND COALESCE(uemp.status, 'ativo') = 'ativo'
           AND (@EmpresaId IS NULL OR uemp.id_empresa = @EmpresaId)
           AND (
                @EstabelecimentoId IS NULL
                OR EXISTS (
                    SELECT 1
                      FROM usuario_estabelecimentos ue2
                     WHERE ue2.id_usuario = u.id
                       AND ue2.id_estabelecimento = @EstabelecimentoId
                       AND COALESCE(ue2.ativo, TRUE) = TRUE
                       AND COALESCE(ue2.status, 'ativo') = 'ativo'
                )
           )
    )
    OR EXISTS (
        SELECT 1
          FROM usuario_estabelecimentos ue
          JOIN estabelecimentos e ON e.id = ue.id_estabelecimento
         WHERE ue.id_usuario = u.id
           AND COALESCE(ue.ativo, TRUE) = TRUE
           AND COALESCE(ue.status, 'ativo') = 'ativo'
           AND (@EmpresaId IS NULL OR e.id_empresa = @EmpresaId)
           AND (@EstabelecimentoId IS NULL OR ue.id_estabelecimento = @EstabelecimentoId)
    )
 ORDER BY Nome, Email;";

            await using var connection = new NpgsqlConnection(_connectionString);
            var rows = await connection.QueryAsync<GestaoUsuarioResumoRow>(sql, new
            {
                EmpresaId = empresaId,
                EstabelecimentoId = estabelecimentoId,
                IncluirSuperAdminsGlobais = incluirSuperAdminsGlobais
            });
            return rows.ToArray();
        }

        public async Task<IReadOnlyCollection<GestaoUsuarioEmpresaRow>> ListarUsuarioEmpresasAsync(
            Guid? empresaId,
            Guid? estabelecimentoId,
            bool incluirSuperAdminsGlobais)
        {
            var builder = new StringBuilder(@"
SELECT  uemp.id                  AS VinculoId,
        uemp.id_usuario          AS UserId,
        uemp.id_empresa          AS EmpresaId,
        emp.nome_fantasia        AS EmpresaNome,
        COALESCE(uemp.tipo_acesso, 'colaborador') AS TipoAcesso,
        COALESCE(uemp.status, 'ativo') AS Status,
        uemp.ativo               AS Ativo
  FROM usuario_empresas uemp
  JOIN empresas emp ON emp.id = uemp.id_empresa
 WHERE COALESCE(uemp.ativo, TRUE) = TRUE
   AND COALESCE(uemp.status, 'ativo') = 'ativo'
   AND (@EmpresaId IS NULL OR uemp.id_empresa = @EmpresaId)");

            if (estabelecimentoId.HasValue)
            {
                builder.Append(@"
   AND EXISTS (
        SELECT 1
          FROM usuario_estabelecimentos ue
         WHERE ue.id_usuario = uemp.id_usuario
           AND ue.id_estabelecimento = @EstabelecimentoId
           AND COALESCE(ue.ativo, TRUE) = TRUE
           AND COALESCE(ue.status, 'ativo') = 'ativo'
   )");
            }

            await using var connection = new NpgsqlConnection(_connectionString);
            var rows = await connection.QueryAsync<GestaoUsuarioEmpresaRow>(builder.ToString(), new
            {
                EmpresaId = empresaId,
                EstabelecimentoId = estabelecimentoId,
                IncluirSuperAdminsGlobais = incluirSuperAdminsGlobais
            });
            return rows.ToArray();
        }

        public async Task<IReadOnlyCollection<GestaoUsuarioEstabelecimentoRow>> ListarUsuarioEstabelecimentosAsync(Guid? empresaId, Guid? estabelecimentoId)
        {
            const string sql = @"
SELECT  ue.id                     AS VinculoId,
        ue.id_usuario             AS UserId,
        e.id_empresa              AS EmpresaId,
        ue.id_estabelecimento     AS EstabelecimentoId,
        e.nome_fantasia           AS EstabelecimentoNome,
        te.nome                   AS TipoEstabelecimentoNome,
        te.slug                   AS TipoEstabelecimentoSlug,
        ue.tipo_acesso            AS TipoAcesso,
        COALESCE(ue.status, 'ativo') AS Status,
        ue.ativo                  AS Ativo,
        ue.permissoes_customizadas::text AS PermissoesCustomizadas
  FROM usuario_estabelecimentos ue
  JOIN estabelecimentos e ON e.id = ue.id_estabelecimento
  LEFT JOIN tipo_estabelecimento te ON te.id = e.id_tipo_estabelecimento
 WHERE COALESCE(ue.ativo, TRUE) = TRUE
   AND COALESCE(ue.status, 'ativo') = 'ativo'
   AND (@EmpresaId IS NULL OR e.id_empresa = @EmpresaId)
   AND (@EstabelecimentoId IS NULL OR ue.id_estabelecimento = @EstabelecimentoId)
 ORDER BY ue.id_usuario, e.nome_fantasia;";

            await using var connection = new NpgsqlConnection(_connectionString);
            var rows = await connection.QueryAsync<GestaoUsuarioEstabelecimentoRow>(sql, new
            {
                EmpresaId = empresaId,
                EstabelecimentoId = estabelecimentoId
            });
            return rows.ToArray();
        }

        public async Task<Dictionary<string, Dictionary<string, List<string>>>> ListarPermissoesPadraoPorTipoAsync(IReadOnlyCollection<string> tiposAcesso)
        {
            if (tiposAcesso == null || tiposAcesso.Count == 0)
            {
                return new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.OrdinalIgnoreCase);
            }

            const string sql = @"
SELECT  pp.tipo_acesso          AS TipoAcesso,
        m.nome                  AS Modulo,
        pp.permissoes::text     AS Permissoes
  FROM permissoes_padrao pp
  JOIN modulos_disponiveis m ON m.id = pp.id_modulo
 WHERE pp.tipo_acesso = ANY(@TiposAcesso);";

            await using var connection = new NpgsqlConnection(_connectionString);
            var rows = await connection.QueryAsync<PermissaoPadraoRow>(sql, new { TiposAcesso = tiposAcesso.ToArray() });

            var result = new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                if (!result.TryGetValue(row.TipoAcesso, out var moduloMap))
                {
                    moduloMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                    result[row.TipoAcesso] = moduloMap;
                }

                moduloMap[row.Modulo] = ParsePermissionList(row.Permissoes);
            }

            return result;
        }

        public async Task<bool> EmailExisteAsync(string email, int? ignoreUserId = null)
        {
            const string sql = @"
SELECT EXISTS(
    SELECT 1
      FROM usuario
     WHERE lower(email) = lower(@Email)
       AND (@IgnoreUserId IS NULL OR id <> @IgnoreUserId)
);";

            await using var connection = new NpgsqlConnection(_connectionString);
            return await connection.ExecuteScalarAsync<bool>(sql, new { Email = email, IgnoreUserId = ignoreUserId });
        }

        public async Task<GestaoUsuarioResumoRow?> ObterUsuarioResumoAsync(int userId)
        {
            const string sql = @"
SELECT  id                      AS Id,
        COALESCE(nome, '')      AS Nome,
        COALESCE(email, '')     AS Email,
        COALESCE(is_super_admin, FALSE) AS IsSuperAdmin,
        created_at              AS CreatedAt,
        deleted_at              AS DeletedAt
  FROM usuario
 WHERE id = @UserId
 LIMIT 1;";

            await using var connection = new NpgsqlConnection(_connectionString);
            return await connection.QueryFirstOrDefaultAsync<GestaoUsuarioResumoRow>(sql, new { UserId = userId });
        }

        public async Task<IReadOnlyCollection<GestaoUsuarioEmpresaRow>> ObterUsuarioEmpresasAsync(int userId)
        {
            const string sql = @"
SELECT  uemp.id                  AS VinculoId,
        uemp.id_usuario          AS UserId,
        uemp.id_empresa          AS EmpresaId,
        emp.nome_fantasia        AS EmpresaNome,
        COALESCE(uemp.tipo_acesso, 'colaborador') AS TipoAcesso,
        COALESCE(uemp.status, 'ativo') AS Status,
        uemp.ativo               AS Ativo
  FROM usuario_empresas uemp
  JOIN empresas emp ON emp.id = uemp.id_empresa
 WHERE uemp.id_usuario = @UserId
   AND COALESCE(uemp.ativo, TRUE) = TRUE
   AND COALESCE(uemp.status, 'ativo') = 'ativo';";

            await using var connection = new NpgsqlConnection(_connectionString);
            var rows = await connection.QueryAsync<GestaoUsuarioEmpresaRow>(sql, new { UserId = userId });
            return rows.ToArray();
        }

        public async Task<IReadOnlyCollection<GestaoUsuarioEstabelecimentoRow>> ObterUsuarioEstabelecimentosAsync(int userId)
        {
            const string sql = @"
SELECT  ue.id                     AS VinculoId,
        ue.id_usuario             AS UserId,
        e.id_empresa              AS EmpresaId,
        ue.id_estabelecimento     AS EstabelecimentoId,
        e.nome_fantasia           AS EstabelecimentoNome,
        te.nome                   AS TipoEstabelecimentoNome,
        te.slug                   AS TipoEstabelecimentoSlug,
        ue.tipo_acesso            AS TipoAcesso,
        COALESCE(ue.status, 'ativo') AS Status,
        ue.ativo                  AS Ativo,
        ue.permissoes_customizadas::text AS PermissoesCustomizadas
  FROM usuario_estabelecimentos ue
  JOIN estabelecimentos e ON e.id = ue.id_estabelecimento
  LEFT JOIN tipo_estabelecimento te ON te.id = e.id_tipo_estabelecimento
 WHERE ue.id_usuario = @UserId
   AND COALESCE(ue.ativo, TRUE) = TRUE
   AND COALESCE(ue.status, 'ativo') = 'ativo';";

            await using var connection = new NpgsqlConnection(_connectionString);
            var rows = await connection.QueryAsync<GestaoUsuarioEstabelecimentoRow>(sql, new { UserId = userId });
            return rows.ToArray();
        }

        public async Task<int> SalvarUsuarioAsync(GestaoPersistenciaUsuarioCommand command)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            int userId;
            if (command.UsuarioId.HasValue)
            {
                const string sqlUpdate = @"
UPDATE usuario
   SET nome = @Nome,
       email = @Email,
       senha = COALESCE(@SenhaHash, senha),
       is_super_admin = @IsSuperAdmin,
       ultimo_estabelecimento_acessado = @UltimoEstabelecimentoAcessado,
       deleted_at = NULL,
       updated_at = NOW()
 WHERE id = @UserId;";

                await connection.ExecuteAsync(sqlUpdate, new
                {
                    UserId = command.UsuarioId.Value,
                    command.Nome,
                    command.Email,
                    command.SenhaHash,
                    command.IsSuperAdmin,
                    UltimoEstabelecimentoAcessado = command.EstabelecimentoIds.FirstOrDefault() == Guid.Empty
                        ? (Guid?)null
                        : command.EstabelecimentoIds.FirstOrDefault()
                }, transaction);

                userId = command.UsuarioId.Value;

                await connection.ExecuteAsync(@"
UPDATE usuario_empresas
   SET ativo = FALSE,
       status = 'removido',
       data_remocao = NOW(),
       updated_at = NOW()
 WHERE id_usuario = @UserId
   AND COALESCE(ativo, TRUE) = TRUE;", new { UserId = userId }, transaction);

                await connection.ExecuteAsync(@"
UPDATE usuario_estabelecimentos
   SET ativo = FALSE,
       status = 'removido',
       data_remocao = NOW(),
       updated_at = NOW()
 WHERE id_usuario = @UserId
   AND COALESCE(ativo, TRUE) = TRUE;", new { UserId = userId }, transaction);
            }
            else
            {
                const string sqlInsert = @"
INSERT INTO usuario (nome, email, senha, is_super_admin, ultimo_estabelecimento_acessado, provider, created_at, updated_at)
VALUES (@Nome, @Email, @SenhaHash, @IsSuperAdmin, @UltimoEstabelecimentoAcessado, 'local', NOW(), NOW())
RETURNING id;";

                userId = await connection.ExecuteScalarAsync<int>(sqlInsert, new
                {
                    command.Nome,
                    command.Email,
                    command.SenhaHash,
                    command.IsSuperAdmin,
                    UltimoEstabelecimentoAcessado = command.EstabelecimentoIds.FirstOrDefault() == Guid.Empty
                        ? (Guid?)null
                        : command.EstabelecimentoIds.FirstOrDefault()
                }, transaction);
            }

            const string sqlUsuarioEmpresa = @"
INSERT INTO usuario_empresas (
    id, id_usuario, id_empresa, tipo_acesso, status, convidado_por, aprovado_por,
    data_convite, data_aprovacao, ativo, created_at, updated_at)
VALUES (
    @Id, @UserId, @EmpresaId, @TipoAcesso, 'ativo', @ActorUserId, @ActorUserId,
    NOW(), NOW(), TRUE, NOW(), NOW())
ON CONFLICT (id_usuario, id_empresa) DO UPDATE
SET tipo_acesso     = EXCLUDED.tipo_acesso,
    status          = 'ativo',
    ativo           = TRUE,
    data_remocao    = NULL,
    aprovado_por    = EXCLUDED.aprovado_por,
    data_aprovacao  = NOW(),
    updated_at      = NOW();";

            await connection.ExecuteAsync(sqlUsuarioEmpresa, new
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                command.EmpresaId,
                TipoAcesso = string.IsNullOrWhiteSpace(command.CompanyRole) ? "colaborador" : command.CompanyRole,
                command.ActorUserId
            }, transaction);

            if (command.EstabelecimentoIds.Count > 0)
            {
                const string sqlUsuarioEstabelecimento = @"
INSERT INTO usuario_estabelecimentos (
    id, id_usuario, id_estabelecimento, tipo_acesso, status, convidado_por, aprovado_por,
    data_convite, data_aprovacao, permissoes_customizadas, ativo, created_at, updated_at)
VALUES (
    @Id, @UserId, @EstabelecimentoId, @TipoAcesso, 'ativo', @ActorUserId, @ActorUserId,
    NOW(), NOW(), @PermissoesCustomizadas::jsonb, TRUE, NOW(), NOW())
ON CONFLICT (id_usuario, id_estabelecimento) DO UPDATE
SET tipo_acesso              = EXCLUDED.tipo_acesso,
    status                   = 'ativo',
    ativo                    = TRUE,
    data_remocao             = NULL,
    aprovado_por             = EXCLUDED.aprovado_por,
    data_aprovacao           = NOW(),
    permissoes_customizadas  = EXCLUDED.permissoes_customizadas,
    updated_at               = NOW();";

                foreach (var estabelecimentoId in command.EstabelecimentoIds)
                {
                    await connection.ExecuteAsync(sqlUsuarioEstabelecimento, new
                    {
                        Id = Guid.NewGuid(),
                        UserId = userId,
                        EstabelecimentoId = estabelecimentoId,
                        TipoAcesso = command.EstablishmentRole ?? "atendente",
                        command.ActorUserId,
                        PermissoesCustomizadas = command.PermissoesCustomizadasJson
                    }, transaction);
                }
            }

            await transaction.CommitAsync();
            return userId;
        }

        public async Task AtualizarStatusUsuarioAsync(int userId, bool ativo)
        {
            const string sql = @"
UPDATE usuario
   SET deleted_at = CASE WHEN @Ativo THEN NULL ELSE NOW() END,
       updated_at = NOW()
 WHERE id = @UserId;";

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.ExecuteAsync(sql, new { UserId = userId, Ativo = ativo });
        }

        public async Task RemoverUsuarioAsync(int userId)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            await connection.ExecuteAsync(@"
UPDATE usuario
   SET deleted_at = NOW(),
       updated_at = NOW()
 WHERE id = @UserId;", new { UserId = userId }, transaction);

            await connection.ExecuteAsync(@"
UPDATE usuario_empresas
   SET ativo = FALSE,
       status = 'removido',
       data_remocao = NOW(),
       updated_at = NOW()
 WHERE id_usuario = @UserId
   AND COALESCE(ativo, TRUE) = TRUE;", new { UserId = userId }, transaction);

            await connection.ExecuteAsync(@"
UPDATE usuario_estabelecimentos
   SET ativo = FALSE,
       status = 'removido',
       data_remocao = NOW(),
       updated_at = NOW()
 WHERE id_usuario = @UserId
   AND COALESCE(ativo, TRUE) = TRUE;", new { UserId = userId }, transaction);

            await transaction.CommitAsync();
        }

        private async Task<Guid> InserirEmpresaAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, SalvarEmpresaRequest request)
        {
            const string sql = @"
INSERT INTO empresas (nome_fantasia, razao_social, cnpj, telefone, email, site, timezone_iana)
VALUES (@NomeFantasia, @RazaoSocial, @Cnpj, @Telefone, @Email, @Site, @TimezoneIana)
RETURNING id;";

            return await connection.ExecuteScalarAsync<Guid>(sql, new
            {
                NomeFantasia = NormalizeText(request.NomeFantasia),
                RazaoSocial = NormalizeNullable(request.RazaoSocial),
                Cnpj = NormalizeDocumentDigits(request.Cnpj),
                Telefone = NormalizeNullable(request.Telefone),
                Email = NormalizeNullable(request.Email),
                Site = NormalizeNullable(request.Site),
                TimezoneIana = "America/Sao_Paulo"
            }, transaction);
        }

        private async Task<Guid> InserirEstabelecimentoAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            SalvarEstabelecimentoRequest request,
            Guid? empresaIdOverride,
            string tipoEstabelecimentoField)
        {
            var empresaId = empresaIdOverride ?? request.EmpresaId;
            if (!empresaId.HasValue || empresaId == Guid.Empty)
            {
                throw new InvalidOperationException("Empresa obrigatoria para salvar estabelecimento.");
            }

            var tipoEstabelecimentoId = await ResolverTipoEstabelecimentoIdAsync(connection, transaction, request, tipoEstabelecimentoField);
            var tipoEstabelecimentoSlug = await ObterTipoEstabelecimentoSlugAsync(connection, transaction, tipoEstabelecimentoId)
                ?? NormalizeTypeSlug(request.TipoEstabelecimentoSlug, request.TipoEstabelecimentoNome);
            var slug = await GarantirSlugDisponivelAsync(connection, transaction, request.Slug, request.NomeFantasia, null);
            var modulos = EstabelecimentoModuleMapper.ToDatabaseModules(request.ModulosAtivos, tipoEstabelecimentoSlug);
            var tipoUnidade = NormalizeTipoUnidade(request.TipoUnidade);

            const string sql = @"
INSERT INTO estabelecimentos (
    id_empresa, tipo_unidade, nome_fantasia, cnpj_loja, inscricao_estadual, inscricao_municipal,
    telefone, whatsapp_e164, email, logradouro, numero, complemento, bairro, cidade, uf, cep,
    latitude, longitude, url_logo, ativo, aceita_pedidos, timezone_iana, raio_entrega_km, pedido_minimo,
    taxa_entrega_fixa, taxa_entrega_por_km, tempo_preparo_min, modulos_ativos, id_tipo_estabelecimento, slug, status)
VALUES (
    @EmpresaId, @TipoUnidade::tipo_unidade_enum, @NomeFantasia, @CnpjLoja, @InscricaoEstadual, @InscricaoMunicipal,
    @Telefone, @WhatsappE164, @Email, @Logradouro, @Numero, @Complemento, @Bairro, @Cidade, @Uf, @Cep,
    @Latitude, @Longitude, @UrlLogo, TRUE, @AceitaPedidos, @TimezoneIana, @RaioEntregaKm, @PedidoMinimo,
    @TaxaEntregaFixa, @TaxaEntregaPorKm, @TempoPreparoMin, @ModulosAtivos::modulo_enum[], @TipoEstabelecimentoId, @Slug, 'ativo')
RETURNING id;";

            return await connection.ExecuteScalarAsync<Guid>(sql, new
            {
                EmpresaId = empresaId.Value,
                TipoUnidade = tipoUnidade,
                NomeFantasia = NormalizeText(request.NomeFantasia),
                CnpjLoja = NormalizeDocumentDigits(request.CnpjLoja),
                InscricaoEstadual = NormalizeNullable(request.InscricaoEstadual),
                InscricaoMunicipal = NormalizeNullable(request.InscricaoMunicipal),
                Telefone = NormalizeNullable(request.Telefone),
                WhatsappE164 = NormalizeWhatsappE164(request.WhatsappE164),
                Email = NormalizeNullable(request.Email),
                Logradouro = NormalizeNullable(request.Logradouro),
                Numero = NormalizeNullable(request.Numero),
                Complemento = NormalizeNullable(request.Complemento),
                Bairro = NormalizeNullable(request.Bairro),
                Cidade = NormalizeNullable(request.Cidade),
                Uf = NormalizeNullable(request.Uf)?.ToUpperInvariant(),
                Cep = NormalizeNullable(request.Cep),
                Latitude = request.Latitude,
                Longitude = request.Longitude,
                UrlLogo = NormalizeNullable(request.UrlLogo),
                AceitaPedidos = request.AceitaPedidos ?? true,
                TimezoneIana = NormalizeNullable(request.TimezoneIana) ?? "America/Sao_Paulo",
                RaioEntregaKm = request.RaioEntregaKm ?? 0m,
                PedidoMinimo = request.PedidoMinimo ?? 0m,
                TaxaEntregaFixa = request.TaxaEntregaFixa ?? 0m,
                TaxaEntregaPorKm = request.TaxaEntregaPorKm ?? 0m,
                TempoPreparoMin = request.TempoPreparoMin ?? 20,
                ModulosAtivos = modulos,
                TipoEstabelecimentoId = tipoEstabelecimentoId,
                Slug = slug
            }, transaction);
        }

        private async Task<Guid> ResolverTipoEstabelecimentoIdAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            SalvarEstabelecimentoRequest request,
            string fieldName)
        {
            var requestedType =
                (request.TipoEstabelecimentoId.HasValue && request.TipoEstabelecimentoId.Value != Guid.Empty) ||
                !string.IsNullOrWhiteSpace(request.TipoEstabelecimentoSlug) ||
                !string.IsNullOrWhiteSpace(request.TipoEstabelecimentoNome);

            if (request.TipoEstabelecimentoId.HasValue && request.TipoEstabelecimentoId.Value != Guid.Empty)
            {
                var byId = await connection.ExecuteScalarAsync<Guid?>(@"
SELECT id
  FROM tipo_estabelecimento
 WHERE id = @Id
 LIMIT 1;", new { Id = request.TipoEstabelecimentoId.Value }, transaction);

                if (byId.HasValue)
                {
                    return byId.Value;
                }
            }

            var normalizedSlug = NormalizeTypeSlug(request.TipoEstabelecimentoSlug, request.TipoEstabelecimentoNome);
            if (!string.IsNullOrWhiteSpace(normalizedSlug))
            {
                var bySlug = await connection.ExecuteScalarAsync<Guid?>(@"
SELECT id
  FROM tipo_estabelecimento
 WHERE lower(slug) = lower(@Slug)
 ORDER BY ativo DESC NULLS LAST
 LIMIT 1;", new { Slug = normalizedSlug }, transaction);

                if (bySlug.HasValue)
                {
                    return bySlug.Value;
                }
            }

            if (!string.IsNullOrWhiteSpace(request.TipoEstabelecimentoNome))
            {
                var byName = await connection.ExecuteScalarAsync<Guid?>(@"
SELECT id
  FROM tipo_estabelecimento
 WHERE lower(nome) = lower(@Nome)
 ORDER BY ativo DESC NULLS LAST
 LIMIT 1;", new { Nome = request.TipoEstabelecimentoNome.Trim() }, transaction);

                if (byName.HasValue)
                {
                    return byName.Value;
                }
            }

            if (requestedType)
            {
                var requestedValue =
                    !string.IsNullOrWhiteSpace(request.TipoEstabelecimentoSlug)
                        ? request.TipoEstabelecimentoSlug.Trim()
                        : !string.IsNullOrWhiteSpace(request.TipoEstabelecimentoNome)
                            ? request.TipoEstabelecimentoNome.Trim()
                            : request.TipoEstabelecimentoId?.ToString();

                var message = string.IsNullOrWhiteSpace(requestedValue)
                    ? "Tipo de estabelecimento informado nao existe."
                    : $"Tipo de estabelecimento informado nao existe: {requestedValue}.";

                throw BuildValidationException(fieldName, message);
            }

            var fallback = await connection.ExecuteScalarAsync<Guid?>(@"
SELECT id
  FROM tipo_estabelecimento
 ORDER BY ativo DESC NULLS LAST, nome
 LIMIT 1;", transaction: transaction);

            if (!fallback.HasValue)
            {
                throw BuildValidationException(fieldName, "Nenhum tipo de estabelecimento cadastrado.");
            }

            return fallback.Value;
        }

        private async Task<string?> ObterTipoEstabelecimentoSlugAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            Guid tipoEstabelecimentoId)
        {
            return await connection.ExecuteScalarAsync<string?>(@"
SELECT slug
  FROM tipo_estabelecimento
 WHERE id = @Id
 LIMIT 1;", new { Id = tipoEstabelecimentoId }, transaction);
        }

        private static RequestValidationException BuildValidationException(string fieldName, string message)
        {
            return new RequestValidationException(
                "Dados invalidos.",
                new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    [fieldName] = new List<string> { message }
                });
        }

        private async Task<string> GarantirSlugDisponivelAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string? preferredSlug,
            string nomeFantasia,
            Guid? ignoreId)
        {
            var baseSlug = Slugify(string.IsNullOrWhiteSpace(preferredSlug) ? nomeFantasia : preferredSlug);
            if (string.IsNullOrWhiteSpace(baseSlug))
            {
                baseSlug = $"est-{Guid.NewGuid():N}"[..12];
            }

            var current = baseSlug;
            var counter = 2;
            while (true)
            {
                var exists = await connection.ExecuteScalarAsync<bool>(@"
SELECT EXISTS(
    SELECT 1
      FROM estabelecimentos
     WHERE lower(slug) = lower(@Slug)
       AND (@IgnoreId IS NULL OR id <> @IgnoreId)
);", new { Slug = current, IgnoreId = ignoreId }, transaction);

                if (!exists)
                {
                    return current;
                }

                current = $"{baseSlug}-{counter}";
                counter++;
            }
        }

        private static string NormalizeTipoUnidade(string? tipoUnidade)
        {
            return NormalizeToken(tipoUnidade) == "filial" ? "filial" : "matriz";
        }

        private static string NormalizeTypeSlug(string? slug, string? nome)
        {
            var normalizedSlug = NormalizeToken(slug);
            if (normalizedSlug == "admin")
            {
                return "restaurante";
            }

            if (!string.IsNullOrWhiteSpace(normalizedSlug))
            {
                return normalizedSlug;
            }

            var normalizedName = NormalizeToken(nome);
            return normalizedName switch
            {
                "administrativo" => "restaurante",
                "bar" => "restaurante",
                _ => normalizedName
            };
        }

        private static string Slugify(string value)
        {
            var normalized = NormalizeToken(value);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return string.Empty;
            }

            normalized = SlugRegex.Replace(normalized, "-");
            return normalized.Trim('-');
        }

        private static string NormalizeText(string? value)
        {
            return (value ?? string.Empty).Trim();
        }

        private static string? NormalizeNullable(string? value)
        {
            var normalized = NormalizeText(value);
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }

        private static string? NormalizeDocumentDigits(string? value)
        {
            var normalized = NormalizeNullable(value);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return null;
            }

            var builder = new StringBuilder(normalized.Length);
            foreach (var ch in normalized)
            {
                if (char.IsDigit(ch))
                {
                    builder.Append(ch);
                }
            }

            return builder.Length == 0 ? null : builder.ToString();
        }

        private static string? NormalizeWhatsappE164(string? value)
        {
            var normalized = NormalizeNullable(value);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return null;
            }

            var digits = NormalizeDocumentDigits(normalized);
            if (string.IsNullOrWhiteSpace(digits))
            {
                return null;
            }

            if (normalized.StartsWith("+", StringComparison.Ordinal))
            {
                return "+" + digits;
            }

            return digits.StartsWith("55", StringComparison.Ordinal)
                ? "+" + digits
                : "+55" + digits;
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
                if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.NonSpacingMark)
                {
                    builder.Append(ch);
                }
            }

            return builder.ToString().Normalize(NormalizationForm.FormC);
        }

        private static List<string> ParsePermissionList(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return new List<string>();
            }

            try
            {
                if (raw.TrimStart().StartsWith("[", StringComparison.Ordinal))
                {
                    var parsed = System.Text.Json.JsonSerializer.Deserialize<List<string>>(raw);
                    if (parsed != null)
                    {
                        return parsed
                            .Where(item => !string.IsNullOrWhiteSpace(item))
                            .Select(item => item.Trim())
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();
                    }
                }
            }
            catch
            {
                // Fallback
            }

            return raw
                .Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(item => item.Trim())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static async Task SincronizarVinculosEmpresaPorEstabelecimentosAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            Guid empresaId)
        {
            const string sqlDesativar = @"
UPDATE usuario_empresas uemp
   SET ativo = FALSE,
       updated_at = NOW()
 WHERE uemp.id_empresa = @EmpresaId
   AND COALESCE(uemp.status, 'ativo') = 'ativo'
   AND COALESCE(uemp.ativo, TRUE) = TRUE
   AND NOT EXISTS (
        SELECT 1
          FROM usuario_estabelecimentos ue
          JOIN estabelecimentos e ON e.id = ue.id_estabelecimento
         WHERE ue.id_usuario = uemp.id_usuario
           AND e.id_empresa = uemp.id_empresa
           AND COALESCE(ue.ativo, TRUE) = TRUE
           AND COALESCE(ue.status, 'ativo') = 'ativo'
           AND COALESCE(e.ativo, TRUE) = TRUE
           AND COALESCE(e.status, 'ativo') IN ('ativo', 'trial')
   );";

            const string sqlReativar = @"
UPDATE usuario_empresas uemp
   SET ativo = TRUE,
       data_remocao = NULL,
       updated_at = NOW()
 WHERE uemp.id_empresa = @EmpresaId
   AND COALESCE(uemp.status, 'ativo') = 'ativo'
   AND COALESCE(uemp.ativo, TRUE) = FALSE
   AND EXISTS (
        SELECT 1
          FROM usuario_estabelecimentos ue
          JOIN estabelecimentos e ON e.id = ue.id_estabelecimento
         WHERE ue.id_usuario = uemp.id_usuario
           AND e.id_empresa = uemp.id_empresa
           AND COALESCE(ue.ativo, TRUE) = TRUE
           AND COALESCE(ue.status, 'ativo') = 'ativo'
           AND COALESCE(e.ativo, TRUE) = TRUE
           AND COALESCE(e.status, 'ativo') IN ('ativo', 'trial')
   );";

            await connection.ExecuteAsync(sqlDesativar, new { EmpresaId = empresaId }, transaction);
            await connection.ExecuteAsync(sqlReativar, new { EmpresaId = empresaId }, transaction);
        }

        private sealed class PermissaoPadraoRow
        {
            public string TipoAcesso { get; set; } = string.Empty;
            public string Modulo { get; set; } = string.Empty;
            public string? Permissoes { get; set; }
        }
    }
}
