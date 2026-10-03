using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace APIBack.Atendimento
{
    public interface ICanalRepository
    {
        Task<IReadOnlyList<CanalWhatsapp>> ListarPorLojaAsync(Guid estabelecimentoId);
        Task<CanalWhatsapp?> ObterAsync(Guid canalId);
        /// <summary>Quem recebe uma mensagem que chegou por este ID da Meta (so numeros ativos).</summary>
        Task<CanalWhatsapp?> ObterAtivoPorPhoneNumberIdAsync(string phoneNumberId);
        /// <summary>Canal pelo telefone (so digitos, com DDI). Inclui numeros inativos: o cadastro precisa enxergar o dono.</summary>
        Task<CanalWhatsapp?> ObterPorNumeroAsync(string digitos);
        /// <summary>Primeiro numero em uso da loja (ativo ou ainda configurando), o mais antigo.</summary>
        Task<CanalWhatsapp?> ObterPrimeiroUsavelAsync(Guid estabelecimentoId);
        /// <summary>Numero que atende o servico na loja (para montar o link wa.me e enviar), o mais antigo ativo.</summary>
        Task<CanalWhatsapp?> ObterParaServicoAsync(Guid estabelecimentoId, string servicoCodigo);

        /// <exception cref="CanalConflitoException">O numero ou o ID da Meta ja existe (em qualquer loja).</exception>
        Task CriarAsync(CanalWhatsapp canal, IReadOnlyCollection<string> servicos);
        /// <exception cref="CanalConflitoException">O novo numero ou ID da Meta ja pertence a outro canal.</exception>
        Task AtualizarDadosAsync(CanalWhatsapp canal);
        Task AtualizarModoAsync(Guid canalId, string modo);
        Task AtualizarConfigAsync(Guid canalId, string configJson);
        Task DefinirServicosAsync(Guid canalId, IReadOnlyCollection<string> servicos);
        /// <summary>Passa o numero para outra loja: zera os servicos e volta para "configurando".</summary>
        Task MoverAsync(Guid canalId, Guid destinoEstabelecimentoId);
        Task RemoverAsync(Guid canalId);
        Task RegistrarAuditoriaAsync(Guid? canalId, string acao, Guid? origem, Guid? destino, int? usuarioId, string? detalheJson);
        Task MarcarRecebimentoAsync(Guid canalId);
        Task MarcarEnvioOkAsync(Guid canalId);
        Task MarcarErroAsync(Guid canalId, string erro, bool desativar);
        Task MarcarVerificadoAsync(Guid canalId, bool ok, string? erro);
    }

    public sealed class SqlCanalRepository : ICanalRepository
    {
        private const string Colunas = @"
       id AS Id, id_estabelecimento AS IdEstabelecimento, phone_number_id AS PhoneNumberId, numero_e164 AS NumeroE164,
       nome AS Nome, token_cifrado AS TokenCifrado, status AS Status, modo_atendimento AS ModoAtendimento,
       config::text AS ConfigJson, verificado_em AS VerificadoEm, ultimo_recebimento_em AS UltimoRecebimentoEm,
       ultimo_envio_ok_em AS UltimoEnvioOkEm, ultimo_erro AS UltimoErro, ultimo_erro_em AS UltimoErroEm";

        private readonly string _connectionString;

        public SqlCanalRepository(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection")
                ?? configuration["ConnectionStrings:DefaultConnection"]
                ?? throw new InvalidOperationException("Connection string 'DefaultConnection' nao encontrada.");
        }

        public async Task<IReadOnlyList<CanalWhatsapp>> ListarPorLojaAsync(Guid estabelecimentoId)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            var canais = (await connection.QueryAsync<CanalWhatsapp>(
                $"SELECT {Colunas} FROM canal_whatsapp WHERE id_estabelecimento = @Id ORDER BY created_at_utc, numero_e164;",
                new { Id = estabelecimentoId })).ToList();
            await PreencherServicosAsync(connection, canais);
            return canais;
        }

        public async Task<CanalWhatsapp?> ObterAsync(Guid canalId)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            return await ObterUmAsync(connection, "WHERE id = @Id", new { Id = canalId });
        }

        public async Task<CanalWhatsapp?> ObterAtivoPorPhoneNumberIdAsync(string phoneNumberId)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            return await ObterUmAsync(connection, "WHERE phone_number_id = @Id AND status <> 'inativo'", new { Id = phoneNumberId.Trim() });
        }

        public async Task<CanalWhatsapp?> ObterPorNumeroAsync(string digitos)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            return await ObterUmAsync(connection, "WHERE numero_e164 = @Numero", new { Numero = "+" + digitos });
        }

        public async Task<CanalWhatsapp?> ObterPrimeiroUsavelAsync(Guid estabelecimentoId)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            return await ObterUmAsync(connection,
                "WHERE id_estabelecimento = @Loja AND status IN ('ativo', 'configurando') ORDER BY created_at_utc",
                new { Loja = estabelecimentoId });
        }

        public async Task<CanalWhatsapp?> ObterParaServicoAsync(Guid estabelecimentoId, string servicoCodigo)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            return await ObterUmAsync(connection,
                @"WHERE id_estabelecimento = @Loja AND status IN ('ativo', 'configurando')
                    AND EXISTS (SELECT 1 FROM canal_servico cs WHERE cs.id_canal = canal_whatsapp.id AND cs.servico_codigo = @Servico)
                  ORDER BY created_at_utc",
                new { Loja = estabelecimentoId, Servico = servicoCodigo });
        }

        public async Task CriarAsync(CanalWhatsapp canal, IReadOnlyCollection<string> servicos)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var tx = await connection.BeginTransactionAsync();
            try
            {
                await connection.ExecuteAsync(@"
INSERT INTO canal_whatsapp (id, id_estabelecimento, phone_number_id, numero_e164, nome, token_cifrado, status, modo_atendimento, config)
VALUES (@Id, @IdEstabelecimento, @PhoneNumberId, @NumeroE164, @Nome, @TokenCifrado, @Status, @ModoAtendimento, CAST(@ConfigJson AS jsonb));",
                    canal, tx);
                await GravarServicosAsync(connection, tx, canal.Id, servicos);
                await tx.CommitAsync();
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                throw Conflito(ex);
            }
        }

        public async Task AtualizarDadosAsync(CanalWhatsapp canal)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            try
            {
                await connection.ExecuteAsync(@"
UPDATE canal_whatsapp
   SET phone_number_id = @PhoneNumberId, numero_e164 = @NumeroE164, nome = @Nome, token_cifrado = @TokenCifrado,
       status = @Status, ultimo_erro = @UltimoErro, updated_at_utc = NOW()
 WHERE id = @Id;", canal);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                throw Conflito(ex);
            }
        }

        public async Task AtualizarModoAsync(Guid canalId, string modo)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.ExecuteAsync(
                "UPDATE canal_whatsapp SET modo_atendimento = @Modo, updated_at_utc = NOW() WHERE id = @Id;",
                new { Id = canalId, Modo = modo });
        }

        public async Task AtualizarConfigAsync(Guid canalId, string configJson)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.ExecuteAsync(
                "UPDATE canal_whatsapp SET config = CAST(@Config AS jsonb), updated_at_utc = NOW() WHERE id = @Id;",
                new { Id = canalId, Config = configJson });
        }

        public async Task DefinirServicosAsync(Guid canalId, IReadOnlyCollection<string> servicos)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var tx = await connection.BeginTransactionAsync();
            await connection.ExecuteAsync("DELETE FROM canal_servico WHERE id_canal = @Id;", new { Id = canalId }, tx);
            await GravarServicosAsync(connection, tx, canalId, servicos);
            await tx.CommitAsync();
        }

        public async Task MoverAsync(Guid canalId, Guid destinoEstabelecimentoId)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var tx = await connection.BeginTransactionAsync();
            await connection.ExecuteAsync("DELETE FROM canal_servico WHERE id_canal = @Id;", new { Id = canalId }, tx);
            await connection.ExecuteAsync(@"
UPDATE canal_whatsapp
   SET id_estabelecimento = @Destino, status = 'configurando', verificado_em = NULL, updated_at_utc = NOW()
 WHERE id = @Id;", new { Id = canalId, Destino = destinoEstabelecimentoId }, tx);
            await tx.CommitAsync();
        }

        public async Task RemoverAsync(Guid canalId)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.ExecuteAsync("DELETE FROM canal_whatsapp WHERE id = @Id;", new { Id = canalId });
        }

        public async Task RegistrarAuditoriaAsync(Guid? canalId, string acao, Guid? origem, Guid? destino, int? usuarioId, string? detalheJson)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.ExecuteAsync(@"
INSERT INTO canal_whatsapp_auditoria (id_canal, acao, id_estabelecimento_origem, id_estabelecimento_destino, usuario_id, detalhe)
VALUES (@Canal, @Acao, @Origem, @Destino, @Usuario, CAST(@Detalhe AS jsonb));",
                new { Canal = canalId, Acao = acao, Origem = origem, Destino = destino, Usuario = usuarioId, Detalhe = detalheJson });
        }

        public async Task MarcarRecebimentoAsync(Guid canalId)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.ExecuteAsync("UPDATE canal_whatsapp SET ultimo_recebimento_em = NOW() WHERE id = @Id;", new { Id = canalId });
        }

        public async Task MarcarEnvioOkAsync(Guid canalId)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.ExecuteAsync(
                "UPDATE canal_whatsapp SET ultimo_envio_ok_em = NOW(), ultimo_erro = NULL, ultimo_erro_em = NULL WHERE id = @Id;",
                new { Id = canalId });
        }

        public async Task MarcarErroAsync(Guid canalId, string erro, bool desativar)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.ExecuteAsync(@"
UPDATE canal_whatsapp
   SET ultimo_erro = @Erro, ultimo_erro_em = NOW(), status = CASE WHEN @Desativar THEN 'erro' ELSE status END
 WHERE id = @Id;", new { Id = canalId, Erro = erro.Length > 500 ? erro[..500] : erro, Desativar = desativar });
        }

        public async Task MarcarVerificadoAsync(Guid canalId, bool ok, string? erro)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.ExecuteAsync(@"
UPDATE canal_whatsapp
   SET status = CASE WHEN @Ok THEN 'ativo' ELSE 'erro' END,
       verificado_em = CASE WHEN @Ok THEN NOW() ELSE verificado_em END,
       ultimo_erro = CASE WHEN @Ok THEN NULL ELSE @Erro END,
       ultimo_erro_em = CASE WHEN @Ok THEN NULL ELSE NOW() END,
       updated_at_utc = NOW()
 WHERE id = @Id;", new { Id = canalId, Ok = ok, Erro = erro });
        }

        private static async Task<CanalWhatsapp?> ObterUmAsync(NpgsqlConnection connection, string filtro, object parametros)
        {
            var canal = await connection.QueryFirstOrDefaultAsync<CanalWhatsapp>(
                $"SELECT {Colunas} FROM canal_whatsapp {filtro} LIMIT 1;", parametros);
            if (canal != null) await PreencherServicosAsync(connection, new List<CanalWhatsapp> { canal });
            return canal;
        }

        private static async Task PreencherServicosAsync(NpgsqlConnection connection, List<CanalWhatsapp> canais)
        {
            if (canais.Count == 0) return;

            var linhas = await connection.QueryAsync<(Guid IdCanal, string Servico)>(
                "SELECT id_canal AS IdCanal, servico_codigo AS Servico FROM canal_servico WHERE id_canal = ANY(@Ids) ORDER BY servico_codigo;",
                new { Ids = canais.Select(c => c.Id).ToArray() });
            var porCanal = linhas.GroupBy(l => l.IdCanal).ToDictionary(g => g.Key, g => g.Select(l => l.Servico).ToList());
            foreach (var canal in canais)
            {
                canal.Servicos = porCanal.TryGetValue(canal.Id, out var lista) ? lista : new List<string>();
            }
        }

        private static Task GravarServicosAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid canalId, IReadOnlyCollection<string> servicos) =>
            servicos.Count == 0
                ? Task.CompletedTask
                : connection.ExecuteAsync(
                    "INSERT INTO canal_servico (id_canal, servico_codigo) SELECT @Id, s FROM unnest(@Servicos::text[]) AS s ON CONFLICT DO NOTHING;",
                    new { Id = canalId, Servicos = servicos.ToArray() }, tx);

        private static CanalConflitoException Conflito(PostgresException ex) => ex.ConstraintName switch
        {
            "ux_canal_whatsapp_numero" => new CanalConflitoException("numero",
                "Este numero de WhatsApp ja esta cadastrado. Um numero pertence a uma loja so: use \"mover numero\" para troca-lo de loja."),
            "ux_canal_whatsapp_phone_number_id" => new CanalConflitoException("phoneNumberId",
                "Este Phone Number ID da Meta ja esta cadastrado. Um numero pertence a uma loja so: use \"mover numero\" para troca-lo de loja."),
            _ => new CanalConflitoException("canal", "Ja existe um canal com estes dados.")
        };
    }
}
