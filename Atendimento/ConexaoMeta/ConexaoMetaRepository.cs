using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace APIBack.Atendimento.ConexaoMeta
{
    /// <summary>Autorizacao que uma loja concedeu ao app integrador. O token so existe cifrado e nunca sai do servidor.</summary>
    public sealed class AutorizacaoMeta
    {
        public Guid Id { get; set; }
        public Guid IdEstabelecimento { get; set; }
        public string AppId { get; set; } = string.Empty;
        public string WabaId { get; set; } = string.Empty;
        public string? BusinessId { get; set; }
        public bool BusinessIdConfirmado { get; set; }
        public string? TokenCifrado { get; set; }
        public DateTime? TokenExpiraEm { get; set; }
        public string[] Permissoes { get; set; } = Array.Empty<string>();
        public string Estado { get; set; } = EstadoAutorizacao.Ativa;
        public string? TipoFluxo { get; set; }
        public string? UltimoErro { get; set; }
        public DateTime? ValidadaEm { get; set; }
        public DateTime? RevogadaEm { get; set; }
        public int? CriadoPor { get; set; }
    }

    public sealed class TentativaConexao
    {
        public Guid Id { get; set; }
        public Guid IdEstabelecimento { get; set; }
        public int UsuarioId { get; set; }
        public string StateHash { get; set; } = string.Empty;
        public string Modo { get; set; } = "conectar";
        public Guid? IdCanal { get; set; }
        public string Estado { get; set; } = EstadoTentativa.Iniciada;
        public string? Etapa { get; set; }
        public string? EventoFluxo { get; set; }
        public bool Coexistencia { get; set; }
        public Guid? IdAutorizacao { get; set; }
        public string? WabaId { get; set; }
        public string? BusinessId { get; set; }
        public string? PhoneNumberId { get; set; }
        public string NumerosJson { get; set; } = "[]";
        public string? ErroCodigo { get; set; }
        public string? ErroMensagem { get; set; }
        public DateTime ExpiraEm { get; set; }
        public DateTime? ConcluidaEm { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
    }

    public interface IConexaoMetaRepository
    {
        /// <summary>
        /// Cria a tentativa e encerra as anteriores da loja ainda abertas (substituida). Recusa (false) se outra aba esta trocando o codigo
        /// agora; troca parada ha mais de <see cref="ConexaoMetaRegras.OperacaoTravada"/> e dada como interrompida.
        /// </summary>
        Task<bool> CriarTentativaAsync(TentativaConexao tentativa);
        Task<TentativaConexao?> ObterTentativaAsync(Guid id);
        Task<TentativaConexao?> UltimaTentativaAsync(Guid estabelecimentoId);
        /// <summary>iniciada -> trocando, atomico: so uma requisicao ganha (replay e abas concorrentes recebem false).</summary>
        Task<bool> ReservarParaTrocaAsync(Guid id);
        /// <summary>autorizada/erro (com autorizacao, dentro do prazo) -> ativando, atomico. Ativacao travada pode ser retomada.</summary>
        Task<bool> ReservarParaAtivacaoAsync(Guid id);
        Task AtualizarTentativaAsync(TentativaConexao tentativa);

        /// <summary>Uma linha por loja + app + WABA: autorizar de novo atualiza a mesma linha (idempotente). Devolve o id.</summary>
        Task<Guid> GravarAutorizacaoAsync(AutorizacaoMeta autorizacao);
        Task<AutorizacaoMeta?> ObterAutorizacaoAsync(Guid id);
        Task MarcarAutorizacaoAsync(Guid id, string estado, string? erro);
        Task MarcarAutorizacaoValidadaAsync(Guid id, DateTime? expiraEm, IReadOnlyCollection<string> permissoes);
        /// <summary>Autorizacoes ativas desta WABA, de qualquer loja, menos <paramref name="exceto"/>.</summary>
        Task<int> ContarOutrasAutorizacoesAtivasAsync(string wabaId, Guid exceto);
        /// <summary>Canais nao inativos ligados a esta autorizacao, menos <paramref name="excetoCanal"/>.</summary>
        Task<int> ContarCanaisDaAutorizacaoAsync(Guid autorizacaoId, Guid excetoCanal);
        /// <summary>
        /// A Meta avisou que o cliente removeu o app (PARTNER_REMOVED): revoga as autorizacoes desta WABA para o app e marca os canais
        /// autorizados ligados a elas com erro. Devolve quantos canais foram afetados.
        /// </summary>
        Task<int> RevogarPorWabaAsync(string wabaId, string? appId, string motivo);

        /// <summary>O app ZippyGo cadastrado pela Gestao (linha unica), ou nulo se ainda nao foi cadastrado.</summary>
        Task<AppMetaConfig?> ObterAppAsync();
        Task SalvarAppAsync(AppMetaConfig app);
    }

    public sealed class SqlConexaoMetaRepository : IConexaoMetaRepository
    {
        private const string ColunasTentativa = @"
       id AS Id, id_estabelecimento AS IdEstabelecimento, usuario_id AS UsuarioId, state_hash AS StateHash, modo AS Modo, id_canal AS IdCanal,
       estado AS Estado, etapa AS Etapa, evento_fluxo AS EventoFluxo, coexistencia AS Coexistencia, id_autorizacao AS IdAutorizacao,
       waba_id AS WabaId, business_id AS BusinessId, phone_number_id AS PhoneNumberId, numeros::text AS NumerosJson,
       erro_codigo AS ErroCodigo, erro_mensagem AS ErroMensagem, expira_em AS ExpiraEm, concluida_em AS ConcluidaEm,
       created_at_utc AS CreatedAtUtc, updated_at_utc AS UpdatedAtUtc";

        private const string ColunasAutorizacao = @"
       id AS Id, id_estabelecimento AS IdEstabelecimento, app_id AS AppId, waba_id AS WabaId, business_id AS BusinessId,
       business_id_confirmado AS BusinessIdConfirmado, token_cifrado AS TokenCifrado, token_expira_em AS TokenExpiraEm,
       permissoes AS Permissoes, estado AS Estado, tipo_fluxo AS TipoFluxo, ultimo_erro AS UltimoErro, validada_em AS ValidadaEm,
       revogada_em AS RevogadaEm, criado_por AS CriadoPor";

        private readonly string _connectionString;

        public SqlConexaoMetaRepository(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection")
                ?? configuration["ConnectionStrings:DefaultConnection"]
                ?? throw new InvalidOperationException("Connection string 'DefaultConnection' nao encontrada.");
        }

        public async Task<bool> CriarTentativaAsync(TentativaConexao t)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var tx = await connection.BeginTransactionAsync();
            var travadaAntes = DateTime.UtcNow - ConexaoMetaRegras.OperacaoTravada;

            // Troca/ativacao interrompida (queda) nao prende a loja para sempre.
            await connection.ExecuteAsync(@"
UPDATE whatsapp_conexao_tentativa
   SET estado = 'erro', erro_codigo = 'WA_META_OPERACAO_INTERROMPIDA', erro_mensagem = 'A operacao foi interrompida.', updated_at_utc = NOW()
 WHERE id_estabelecimento = @Loja AND estado IN ('trocando', 'ativando') AND updated_at_utc < @Antes;",
                new { Loja = t.IdEstabelecimento, Antes = travadaAntes }, tx);

            var emAndamento = await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM whatsapp_conexao_tentativa WHERE id_estabelecimento = @Loja AND estado IN ('trocando', 'ativando');",
                new { Loja = t.IdEstabelecimento }, tx);
            if (emAndamento > 0)
            {
                await tx.RollbackAsync();
                return false;
            }

            await connection.ExecuteAsync(@"
UPDATE whatsapp_conexao_tentativa SET estado = 'substituida', updated_at_utc = NOW()
 WHERE id_estabelecimento = @Loja AND estado IN ('iniciada', 'autorizada', 'erro');", new { Loja = t.IdEstabelecimento }, tx);

            await connection.ExecuteAsync(@"
INSERT INTO whatsapp_conexao_tentativa (id, id_estabelecimento, usuario_id, state_hash, modo, id_canal, estado, coexistencia, expira_em)
VALUES (@Id, @IdEstabelecimento, @UsuarioId, @StateHash, @Modo, @IdCanal, 'iniciada', @Coexistencia, @ExpiraEm);", t, tx);
            await tx.CommitAsync();
            return true;
        }

        public async Task<TentativaConexao?> ObterTentativaAsync(Guid id)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            return await connection.QueryFirstOrDefaultAsync<TentativaConexao>(
                $"SELECT {ColunasTentativa} FROM whatsapp_conexao_tentativa WHERE id = @Id;", new { Id = id });
        }

        public async Task<TentativaConexao?> UltimaTentativaAsync(Guid estabelecimentoId)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            return await connection.QueryFirstOrDefaultAsync<TentativaConexao>(
                $"SELECT {ColunasTentativa} FROM whatsapp_conexao_tentativa WHERE id_estabelecimento = @Loja ORDER BY created_at_utc DESC LIMIT 1;",
                new { Loja = estabelecimentoId });
        }

        public async Task<bool> ReservarParaTrocaAsync(Guid id)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            try
            {
                return await connection.ExecuteAsync(@"
UPDATE whatsapp_conexao_tentativa SET estado = 'trocando', updated_at_utc = NOW()
 WHERE id = @Id AND estado = 'iniciada' AND expira_em > NOW();", new { Id = id }) == 1;
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                // Outra tentativa da mesma loja esta trocando o codigo neste instante.
                return false;
            }
        }

        public async Task<bool> ReservarParaAtivacaoAsync(Guid id)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            return await connection.ExecuteAsync(@"
UPDATE whatsapp_conexao_tentativa SET estado = 'ativando', updated_at_utc = NOW()
 WHERE id = @Id AND id_autorizacao IS NOT NULL AND expira_em > NOW()
   AND (estado IN ('autorizada', 'erro') OR (estado = 'ativando' AND updated_at_utc < @Antes));",
                new { Id = id, Antes = DateTime.UtcNow - ConexaoMetaRegras.OperacaoTravada }) == 1;
        }

        public async Task AtualizarTentativaAsync(TentativaConexao t)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.ExecuteAsync(@"
UPDATE whatsapp_conexao_tentativa
   SET estado = @Estado, etapa = @Etapa, evento_fluxo = @EventoFluxo, coexistencia = @Coexistencia, id_autorizacao = @IdAutorizacao,
       waba_id = @WabaId, business_id = @BusinessId, phone_number_id = @PhoneNumberId, numeros = CAST(@NumerosJson AS jsonb),
       erro_codigo = @ErroCodigo, erro_mensagem = @ErroMensagem, expira_em = @ExpiraEm, concluida_em = @ConcluidaEm, updated_at_utc = NOW()
 WHERE id = @Id;", t);
        }

        public async Task<Guid> GravarAutorizacaoAsync(AutorizacaoMeta a)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            return await connection.ExecuteScalarAsync<Guid>(@"
INSERT INTO whatsapp_autorizacao_meta (id, id_estabelecimento, app_id, waba_id, business_id, business_id_confirmado, token_cifrado,
                                       token_expira_em, permissoes, estado, tipo_fluxo, validada_em, criado_por)
VALUES (@Id, @IdEstabelecimento, @AppId, @WabaId, @BusinessId, @BusinessIdConfirmado, @TokenCifrado,
        @TokenExpiraEm, @Permissoes, 'ativa', @TipoFluxo, NOW(), @CriadoPor)
ON CONFLICT (id_estabelecimento, app_id, waba_id) DO UPDATE
   SET business_id = COALESCE(EXCLUDED.business_id, whatsapp_autorizacao_meta.business_id),
       business_id_confirmado = EXCLUDED.business_id_confirmado OR whatsapp_autorizacao_meta.business_id_confirmado,
       token_cifrado = EXCLUDED.token_cifrado, token_expira_em = EXCLUDED.token_expira_em, permissoes = EXCLUDED.permissoes,
       estado = 'ativa', tipo_fluxo = EXCLUDED.tipo_fluxo, ultimo_erro = NULL, validada_em = NOW(), revogada_em = NULL, updated_at_utc = NOW()
RETURNING id;", a);
        }

        public async Task<AutorizacaoMeta?> ObterAutorizacaoAsync(Guid id)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            return await connection.QueryFirstOrDefaultAsync<AutorizacaoMeta>(
                $"SELECT {ColunasAutorizacao} FROM whatsapp_autorizacao_meta WHERE id = @Id;", new { Id = id });
        }

        public async Task MarcarAutorizacaoAsync(Guid id, string estado, string? erro)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.ExecuteAsync(@"
UPDATE whatsapp_autorizacao_meta
   SET estado = @Estado, ultimo_erro = @Erro,
       revogada_em = CASE WHEN @Estado = 'revogada' THEN COALESCE(revogada_em, NOW()) ELSE revogada_em END, updated_at_utc = NOW()
 WHERE id = @Id;", new { Id = id, Estado = estado, Erro = erro });
        }

        public async Task MarcarAutorizacaoValidadaAsync(Guid id, DateTime? expiraEm, IReadOnlyCollection<string> permissoes)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.ExecuteAsync(@"
UPDATE whatsapp_autorizacao_meta
   SET validada_em = NOW(), token_expira_em = @Expira, permissoes = @Permissoes, ultimo_erro = NULL, updated_at_utc = NOW()
 WHERE id = @Id;", new { Id = id, Expira = expiraEm, Permissoes = permissoes.ToArray() });
        }

        public async Task<int> ContarOutrasAutorizacoesAtivasAsync(string wabaId, Guid exceto)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            return await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM whatsapp_autorizacao_meta WHERE waba_id = @Waba AND id <> @Id AND estado = 'ativa';",
                new { Waba = wabaId, Id = exceto });
        }

        public async Task<int> ContarCanaisDaAutorizacaoAsync(Guid autorizacaoId, Guid excetoCanal)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            return await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM canal_whatsapp WHERE id_autorizacao_meta = @Id AND id <> @Canal AND status <> 'inativo';",
                new { Id = autorizacaoId, Canal = excetoCanal });
        }

        public async Task<AppMetaConfig?> ObterAppAsync()
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            return await connection.QueryFirstOrDefaultAsync<AppMetaConfig>(@"
SELECT app_id AS AppId, configuration_id AS ConfigurationId, app_secret_cifrado AS AppSecretCifrado, app_review_aprovado AS AppReviewAprovado,
       business_id_integrador AS BusinessIdIntegrador, atualizado_por AS AtualizadoPor, atualizado_em AS AtualizadoEm
  FROM whatsapp_meta_app WHERE id = 1;");
        }

        public async Task SalvarAppAsync(AppMetaConfig app)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.ExecuteAsync(@"
INSERT INTO whatsapp_meta_app (id, app_id, configuration_id, app_secret_cifrado, app_review_aprovado, business_id_integrador, atualizado_por, atualizado_em)
VALUES (1, @AppId, @ConfigurationId, @AppSecretCifrado, @AppReviewAprovado, @BusinessIdIntegrador, @AtualizadoPor, NOW())
ON CONFLICT (id) DO UPDATE
   SET app_id = EXCLUDED.app_id, configuration_id = EXCLUDED.configuration_id, app_secret_cifrado = EXCLUDED.app_secret_cifrado,
       app_review_aprovado = EXCLUDED.app_review_aprovado, business_id_integrador = EXCLUDED.business_id_integrador,
       atualizado_por = EXCLUDED.atualizado_por, atualizado_em = NOW();", app);
        }

        public async Task<int> RevogarPorWabaAsync(string wabaId, string? appId, string motivo)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var tx = await connection.BeginTransactionAsync();
            var ids = (await connection.QueryAsync<Guid>(@"
UPDATE whatsapp_autorizacao_meta
   SET estado = 'revogada', revogada_em = COALESCE(revogada_em, NOW()), ultimo_erro = @Motivo, updated_at_utc = NOW()
 WHERE waba_id = @Waba AND (@App IS NULL OR app_id = @App) AND estado <> 'revogada'
RETURNING id;", new { Waba = wabaId, App = appId, Motivo = motivo }, tx)).ToArray();

            var canais = ids.Length == 0 ? 0 : await connection.ExecuteAsync(@"
UPDATE canal_whatsapp
   SET status = 'erro', ultimo_erro = @Motivo, ultimo_erro_em = NOW(), updated_at_utc = NOW()
 WHERE origem = 'autorizado' AND id_autorizacao_meta = ANY(@Ids) AND status <> 'inativo';", new { Ids = ids, Motivo = motivo }, tx);
            await tx.CommitAsync();
            return canais;
        }
    }
}
