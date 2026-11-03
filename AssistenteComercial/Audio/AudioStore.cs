using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;

namespace APIBack.AssistenteComercial.Audio
{
    public interface IAudioStore
    {
        /// <summary>Insere a entrada; se a mensagem da Meta ja existe devolve a existente (Nova = false). Idempotencia por wa_mensagem_id.</summary>
        Task<(EntradaAudio Entrada, bool Nova)> CriarAsync(EntradaAudio entrada, CancellationToken ct);
        Task<EntradaAudio?> ObterAsync(Guid id, CancellationToken ct);
        Task<IReadOnlyList<EntradaAudio>> ListarAsync(int usuarioId, string? estado, int limite, CancellationToken ct);
        Task<EntradaAudio?> UltimaAguardandoAsync(int usuarioId, Guid canalId, DateTime desde, CancellationToken ct);
        /// <summary>Pega a proxima entrada devida e trava por <paramref name="lease"/> (SKIP LOCKED: varios workers nao se pisam).</summary>
        Task<EntradaAudio?> ReservarProximaAsync(DateTime agora, TimeSpan lease, CancellationToken ct);
        /// <summary>Grava os campos mutaveis se a versao ainda for a lida. false = outro processo mexeu primeiro.</summary>
        Task<bool> SalvarAsync(EntradaAudio entrada, CancellationToken ct);
        /// <summary>Guarda o que ja foi aplicado durante a confirmacao (retomar nao repete alteracao).</summary>
        Task SalvarResultadoParcialAsync(Guid id, string resultadoJson, CancellationToken ct);
        /// <summary>Troca de estado atomica (confirmar/rejeitar/reprocessar concorrentes: so um vence).</summary>
        Task<bool> TransicionarAsync(Guid id, string[] de, string para, string? chaveConfirmacao, CancellationToken ct);
        Task<EntradaAudio?> DuplicadaPorHashAsync(int usuarioId, string hash, Guid exceto, CancellationToken ct);

        Task<bool> ConsentimentoAtivoAsync(int usuarioId, CancellationToken ct);
        Task RegistrarConsentimentoAsync(int usuarioId, string versaoTexto, CancellationToken ct);
        Task RevogarConsentimentoAsync(int usuarioId, CancellationToken ct);

        /// <summary>Segura <paramref name="valor"/> do teto do mes sob trava global; false = estouraria o teto.</summary>
        Task<bool> ReservarCustoAsync(Guid entradaId, DateTime competencia, decimal valor, decimal teto, string fornecedor, string modelo, CancellationToken ct);
        Task RegistrarUsoAsync(Guid entradaId, DateTime competencia, string etapa, string fornecedor, string modelo, decimal unidades, decimal custo, CancellationToken ct);
        Task LiberarReservaAsync(Guid entradaId, CancellationToken ct);
        Task<decimal> CustoDoMesAsync(DateTime competencia, CancellationToken ct);

        Task AuditarAsync(Guid entradaId, string evento, int? atorUsuarioId, object? detalhe, CancellationToken ct);
        /// <summary>Apaga transcricao e proposta de entradas encerradas antes de <paramref name="limite"/> e expira as pendentes antigas.</summary>
        Task<int> AplicarRetencaoAsync(DateTime limite, CancellationToken ct);
    }

    public sealed class AudioPostgresStore : IAudioStore
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private readonly NpgsqlDataSource _db;

        public AudioPostgresStore(NpgsqlDataSource db) => _db = db;

        private const string Colunas = @"
id AS Id, wa_mensagem_id AS WaMensagemId, usuario_id AS UsuarioId, canal_id AS CanalId, loja_gestao_id AS LojaGestaoId,
phone_number_id AS PhoneNumberId, numero_canal_e164 AS NumeroCanalE164, telefone_e164 AS TelefoneE164, midia_id AS MidiaId,
midia_mime AS MidiaMime, midia_hash AS MidiaHash, midia_bytes AS MidiaBytes, duracao_segundos AS DuracaoSegundos, estado AS Estado,
etapa AS Etapa, erro_codigo AS ErroCodigo, erro_detalhe AS ErroDetalhe, tentativas AS Tentativas, proxima_tentativa_em AS ProximaTentativaEm,
transcricao AS Transcricao, proposta::text AS PropostaJson, oportunidade_id AS OportunidadeId, duplicada_de AS DuplicadaDe,
custo_estimado AS CustoEstimado, custo_real AS CustoReal, chave_confirmacao AS ChaveConfirmacao, resultado::text AS ResultadoJson,
retorno_enviado_em AS RetornoEnviadoEm, recebida_em AS RecebidaEm, criado_em AS CriadaEm, atualizado_em AS AtualizadaEm, versao AS Versao";

        public async Task<(EntradaAudio Entrada, bool Nova)> CriarAsync(EntradaAudio e, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            var inseriu = await cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO crm_assistente_entrada (id, wa_mensagem_id, usuario_id, canal_id, loja_gestao_id, phone_number_id, numero_canal_e164, telefone_e164,
                                    midia_id, midia_mime, estado, proxima_tentativa_em, recebida_em)
VALUES (@Id, @WaMensagemId, @UsuarioId, @CanalId, @LojaGestaoId, @PhoneNumberId, @NumeroCanalE164, @TelefoneE164,
        @MidiaId, @MidiaMime, @Estado, NOW(), @RecebidaEm)
ON CONFLICT (wa_mensagem_id) DO NOTHING", e, cancellationToken: ct));
            var atual = await cx.QuerySingleAsync<EntradaAudio>(new CommandDefinition(
                $"SELECT {Colunas} FROM crm_assistente_entrada WHERE wa_mensagem_id = @Wa", new { Wa = e.WaMensagemId }, cancellationToken: ct));
            return (atual, inseriu == 1);
        }

        public async Task<EntradaAudio?> ObterAsync(Guid id, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            return await cx.QueryFirstOrDefaultAsync<EntradaAudio>(new CommandDefinition(
                $"SELECT {Colunas} FROM crm_assistente_entrada WHERE id = @Id", new { Id = id }, cancellationToken: ct));
        }

        public async Task<IReadOnlyList<EntradaAudio>> ListarAsync(int usuarioId, string? estado, int limite, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            var linhas = await cx.QueryAsync<EntradaAudio>(new CommandDefinition($@"
SELECT {Colunas} FROM crm_assistente_entrada
 WHERE usuario_id = @U AND (@Estado::text IS NULL OR estado = @Estado)
 ORDER BY recebida_em DESC LIMIT @Limite", new { U = usuarioId, Estado = estado, Limite = Math.Clamp(limite, 1, 100) }, cancellationToken: ct));
            return linhas.ToList();
        }

        public async Task<EntradaAudio?> UltimaAguardandoAsync(int usuarioId, Guid canalId, DateTime desde, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            return await cx.QueryFirstOrDefaultAsync<EntradaAudio>(new CommandDefinition($@"
SELECT {Colunas} FROM crm_assistente_entrada
 WHERE usuario_id = @U AND canal_id = @C AND estado = 'aguardando_revisao' AND retorno_enviado_em IS NOT NULL AND recebida_em >= @Desde
 ORDER BY recebida_em DESC LIMIT 1", new { U = usuarioId, C = canalId, Desde = desde }, cancellationToken: ct));
        }

        public async Task<EntradaAudio?> ReservarProximaAsync(DateTime agora, TimeSpan lease, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            return await cx.QueryFirstOrDefaultAsync<EntradaAudio>(new CommandDefinition($@"
UPDATE crm_assistente_entrada SET estado = 'processando', travado_ate = @Ate, atualizado_em = NOW(), versao = versao + 1
 WHERE id = (SELECT id FROM crm_assistente_entrada
              WHERE (estado = 'recebido' AND COALESCE(proxima_tentativa_em, NOW()) <= @Agora)
                 OR (estado = 'processando' AND COALESCE(travado_ate, NOW()) <= @Agora)
              ORDER BY recebida_em
              FOR UPDATE SKIP LOCKED LIMIT 1)
RETURNING {Colunas}", new { Agora = agora, Ate = agora + lease }, cancellationToken: ct));
        }

        public async Task<bool> SalvarAsync(EntradaAudio e, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            var n = await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE crm_assistente_entrada SET
       midia_hash = @MidiaHash, midia_bytes = @MidiaBytes, duracao_segundos = @DuracaoSegundos, estado = @Estado, etapa = @Etapa,
       erro_codigo = @ErroCodigo, erro_detalhe = @ErroDetalhe, tentativas = @Tentativas, proxima_tentativa_em = @ProximaTentativaEm,
       transcricao = @Transcricao, proposta = CAST(@PropostaJson AS jsonb), oportunidade_id = @OportunidadeId, duplicada_de = @DuplicadaDe,
       custo_estimado = @CustoEstimado, custo_real = @CustoReal, chave_confirmacao = @ChaveConfirmacao,
       resultado = CAST(@ResultadoJson AS jsonb), retorno_enviado_em = @RetornoEnviadoEm,
       travado_ate = CASE WHEN @Estado = 'processando' THEN travado_ate ELSE NULL END,
       encerrada_em = CASE WHEN @Estado IN ('confirmada','rejeitada','falha','duplicada','bloqueada','expirada') THEN COALESCE(encerrada_em, NOW()) ELSE NULL END,
       atualizado_em = NOW(), versao = versao + 1
 WHERE id = @Id AND versao = @Versao", e, cancellationToken: ct));
            if (n == 1) e.Versao++;
            return n == 1;
        }

        public async Task SalvarResultadoParcialAsync(Guid id, string resultadoJson, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            await cx.ExecuteAsync(new CommandDefinition(
                "UPDATE crm_assistente_entrada SET resultado = CAST(@R AS jsonb), atualizado_em = NOW() WHERE id = @Id AND estado = 'aplicando'",
                new { Id = id, R = resultadoJson }, cancellationToken: ct));
        }

        public async Task<bool> TransicionarAsync(Guid id, string[] de, string para, string? chaveConfirmacao, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            return await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE crm_assistente_entrada
   SET estado = @Para, chave_confirmacao = COALESCE(@Chave, chave_confirmacao), travado_ate = NULL,
       proxima_tentativa_em = CASE WHEN @Para = 'recebido' THEN NOW() ELSE proxima_tentativa_em END,
       tentativas = CASE WHEN @Para = 'recebido' THEN 0 ELSE tentativas END,
       erro_codigo = CASE WHEN @Para = 'recebido' THEN NULL ELSE erro_codigo END,
       erro_detalhe = CASE WHEN @Para = 'recebido' THEN NULL ELSE erro_detalhe END,
       encerrada_em = CASE WHEN @Para IN ('rejeitada','confirmada') THEN NOW() ELSE NULL END,
       atualizado_em = NOW(), versao = versao + 1
 WHERE id = @Id AND estado = ANY(@De)", new { Id = id, De = de, Para = para, Chave = chaveConfirmacao }, cancellationToken: ct)) == 1;
        }

        public async Task<EntradaAudio?> DuplicadaPorHashAsync(int usuarioId, string hash, Guid exceto, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            return await cx.QueryFirstOrDefaultAsync<EntradaAudio>(new CommandDefinition($@"
SELECT {Colunas} FROM crm_assistente_entrada
 WHERE usuario_id = @U AND midia_hash = @H AND id <> @Id AND estado NOT IN ('duplicada', 'falha', 'bloqueada', 'expirada')
 ORDER BY recebida_em LIMIT 1", new { U = usuarioId, H = hash, Id = exceto }, cancellationToken: ct));
        }

        public async Task<bool> ConsentimentoAtivoAsync(int usuarioId, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            return await cx.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS (SELECT 1 FROM crm_assistente_consentimento WHERE usuario_id = @U AND revogado_em IS NULL)", new { U = usuarioId }, cancellationToken: ct));
        }

        public async Task RegistrarConsentimentoAsync(int usuarioId, string versaoTexto, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            await cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO crm_assistente_consentimento (usuario_id, versao_texto) VALUES (@U, @V)
ON CONFLICT (usuario_id) DO UPDATE SET versao_texto = EXCLUDED.versao_texto, consentido_em = NOW(), revogado_em = NULL",
                new { U = usuarioId, V = versaoTexto }, cancellationToken: ct));
        }

        public async Task RevogarConsentimentoAsync(int usuarioId, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            await cx.ExecuteAsync(new CommandDefinition(
                "UPDATE crm_assistente_consentimento SET revogado_em = COALESCE(revogado_em, NOW()) WHERE usuario_id = @U", new { U = usuarioId }, cancellationToken: ct));
        }

        public async Task<bool> ReservarCustoAsync(Guid entradaId, DateTime competencia, decimal valor, decimal teto, string fornecedor, string modelo, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            await using var tx = await cx.BeginTransactionAsync(ct);
            // Uma trava para o teto inteiro: duas entradas concorrentes nao passam juntas pela mesma folga.
            await cx.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(75442215)", transaction: tx, cancellationToken: ct));
            await cx.ExecuteAsync(new CommandDefinition("DELETE FROM crm_assistente_uso WHERE entrada_id = @Id AND etapa = 'reserva'", new { Id = entradaId }, tx, cancellationToken: ct));
            var mes = new DateTime(competencia.Year, competencia.Month, 1);
            var usado = await cx.ExecuteScalarAsync<decimal>(new CommandDefinition(
                "SELECT COALESCE(SUM(custo), 0) FROM crm_assistente_uso WHERE competencia = @M", new { M = mes }, tx, cancellationToken: ct));
            if (usado + valor > teto)
            {
                await tx.RollbackAsync(ct);
                return false;
            }
            await cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO crm_assistente_uso (entrada_id, competencia, etapa, fornecedor, modelo, unidades, custo) VALUES (@Id, @M, 'reserva', @F, @Mo, 0, @V)",
                new { Id = entradaId, M = mes, F = fornecedor, Mo = modelo, V = valor }, tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
            return true;
        }

        public async Task RegistrarUsoAsync(Guid entradaId, DateTime competencia, string etapa, string fornecedor, string modelo, decimal unidades, decimal custo, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            await cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO crm_assistente_uso (entrada_id, competencia, etapa, fornecedor, modelo, unidades, custo) VALUES (@Id, @M, @E, @F, @Mo, @U, @C)",
                new { Id = entradaId, M = new DateTime(competencia.Year, competencia.Month, 1), E = etapa, F = fornecedor, Mo = modelo, U = unidades, C = custo }, cancellationToken: ct));
        }

        public async Task LiberarReservaAsync(Guid entradaId, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            await cx.ExecuteAsync(new CommandDefinition("DELETE FROM crm_assistente_uso WHERE entrada_id = @Id AND etapa = 'reserva'", new { Id = entradaId }, cancellationToken: ct));
        }

        public async Task<decimal> CustoDoMesAsync(DateTime competencia, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            return await cx.ExecuteScalarAsync<decimal>(new CommandDefinition(
                "SELECT COALESCE(SUM(custo), 0) FROM crm_assistente_uso WHERE competencia = @M",
                new { M = new DateTime(competencia.Year, competencia.Month, 1) }, cancellationToken: ct));
        }

        public async Task AuditarAsync(Guid entradaId, string evento, int? atorUsuarioId, object? detalhe, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            await cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO crm_assistente_auditoria (entrada_id, evento, ator_usuario_id, detalhe) VALUES (@Id, @E, @A, CAST(@D AS jsonb))",
                new { Id = entradaId, E = evento, A = atorUsuarioId, D = detalhe == null ? null : JsonSerializer.Serialize(detalhe, Json) }, cancellationToken: ct));
        }

        public async Task<int> AplicarRetencaoAsync(DateTime limite, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            // Pendentes velhas nao ficam eternas: expiram (e seguem para a limpeza no ciclo seguinte).
            await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE crm_assistente_entrada SET estado = 'expirada', encerrada_em = NOW(), atualizado_em = NOW(), versao = versao + 1
 WHERE estado IN ('aguardando_revisao', 'recebido') AND recebida_em < @L", new { L = limite }, cancellationToken: ct));
            return await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE crm_assistente_entrada SET transcricao = NULL, proposta = NULL, resultado = NULL, atualizado_em = NOW(), versao = versao + 1
 WHERE encerrada_em < @L AND (transcricao IS NOT NULL OR proposta IS NOT NULL OR resultado IS NOT NULL)", new { L = limite }, cancellationToken: ct));
        }
    }
}
