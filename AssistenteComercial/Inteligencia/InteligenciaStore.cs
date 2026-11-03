using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;

namespace APIBack.AssistenteComercial.Inteligencia
{
    public interface IInteligenciaStore
    {
        Task<IReadOnlyList<Objecao>> ListarObjecoesAsync(Guid oportunidadeId, CancellationToken ct);
        Task<Objecao?> ObterObjecaoAsync(Guid id, CancellationToken ct);
        /// <summary>false = ja existe (objecao de audio repetida): idempotente.</summary>
        Task<bool> InserirObjecaoAsync(Objecao o, CancellationToken ct);
        /// <summary>Grava se a versao ainda for a lida; false = alguem mexeu primeiro.</summary>
        Task<bool> AtualizarObjecaoAsync(Objecao o, int versaoLida, CancellationToken ct);
        Task AuditarObjecaoAsync(Guid objecaoId, Guid oportunidadeId, string evento, int ator, object? antes, object? depois, string? motivo, CancellationToken ct);

        Task<IReadOnlyList<EntradaAudioConfirmada>> EntradasAudioConfirmadasAsync(Guid oportunidadeId, CancellationToken ct);
        Task<bool> ConsentimentoAudioAsync(int usuarioId, CancellationToken ct);

        Task<IReadOnlyList<Recomendacao>> ListarRecomendacoesAsync(Guid oportunidadeId, CancellationToken ct);
        Task<Recomendacao?> ObterRecomendacaoAsync(Guid id, CancellationToken ct);
        /// <summary>false = ja existe sugestao aberta com a mesma impressao digital.</summary>
        Task<bool> InserirRecomendacaoAsync(Recomendacao r, CancellationToken ct);
        Task<bool> AtualizarRecomendacaoAsync(Recomendacao r, int versaoLida, CancellationToken ct);
        Task RegistrarEventoAsync(Guid recomendacaoId, string evento, int? ator, object? detalhe, CancellationToken ct);

        Task<IReadOnlyList<ObjecaoHistorica>> ObjecoesResolvidasComAbordagemAsync(string[] tipos, Guid exetoOportunidadeId, int limite, CancellationToken ct);
        Task<IReadOnlyList<ObjecaoDoResponsavel>> ObjecoesDoResponsavelAsync(int usuarioId, DateTime desde, CancellationToken ct);
        Task<IReadOnlyList<RecomendacaoDoResponsavel>> RecomendacoesDoResponsavelAsync(int usuarioId, DateTime desde, CancellationToken ct);
    }

    public sealed class InteligenciaPostgresStore : IInteligenciaStore
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private readonly NpgsqlDataSource _db;

        public InteligenciaPostgresStore(NpgsqlDataSource db) => _db = db;

        private const string ColObj = @"
id AS Id, oportunidade_id AS OportunidadeId, tipo AS Tipo, descricao AS Descricao, evidencia AS Evidencia, origem AS Origem,
entrada_audio_id AS EntradaAudioId, certeza AS Certeza, estado AS Estado, impacto AS Impacto, abordagem AS Abordagem,
modulo_citado AS ModuloCitado, resposta_prometida_ate AS RespostaPrometidaAte, ocorrida_em AS OcorridaEm, criado_por AS CriadoPor,
criado_em AS CriadoEm, atualizado_em AS AtualizadoEm, versao AS Versao";

        private const string ColRec = @"
id AS Id, oportunidade_id AS OportunidadeId, responsavel_usuario_id AS ResponsavelUsuarioId, regra AS Regra, prioridade AS Prioridade,
impressao_digital AS ImpressaoDigital, sugestao AS Sugestao, hipotese AS Hipotese, acao AS Acao, evidencias::text AS EvidenciasJson,
redacao_ia AS RedacaoIa, estado AS Estado, motivo_rejeicao AS MotivoRejeicao, reacao AS Reacao, resultado AS Resultado,
observacao AS Observacao, criado_em AS CriadoEm, atualizado_em AS AtualizadoEm, versao AS Versao";

        public async Task<IReadOnlyList<Objecao>> ListarObjecoesAsync(Guid oportunidadeId, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            return (await cx.QueryAsync<Objecao>(new CommandDefinition(
                $"SELECT {ColObj} FROM crm_objecao WHERE oportunidade_id = @O ORDER BY ocorrida_em DESC, criado_em DESC", new { O = oportunidadeId }, cancellationToken: ct))).ToList();
        }

        public async Task<Objecao?> ObterObjecaoAsync(Guid id, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            return await cx.QueryFirstOrDefaultAsync<Objecao>(new CommandDefinition($"SELECT {ColObj} FROM crm_objecao WHERE id = @Id", new { Id = id }, cancellationToken: ct));
        }

        public async Task<bool> InserirObjecaoAsync(Objecao o, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            var n = await cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO crm_objecao (id, oportunidade_id, tipo, descricao, evidencia, origem, entrada_audio_id, certeza, estado, impacto, abordagem,
                         modulo_citado, resposta_prometida_ate, ocorrida_em, criado_por)
VALUES (@Id, @OportunidadeId, @Tipo, @Descricao, @Evidencia, @Origem, @EntradaAudioId, @Certeza, @Estado, @Impacto, @Abordagem,
        @ModuloCitado, @RespostaPrometidaAte, @OcorridaEm, @CriadoPor)
ON CONFLICT DO NOTHING", o, cancellationToken: ct));
            return n == 1;
        }

        public async Task<bool> AtualizarObjecaoAsync(Objecao o, int versaoLida, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            var n = await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE crm_objecao SET tipo = @Tipo, descricao = @Descricao, evidencia = @Evidencia, certeza = @Certeza, estado = @Estado, impacto = @Impacto,
       abordagem = @Abordagem, modulo_citado = @ModuloCitado, resposta_prometida_ate = @RespostaPrometidaAte, ocorrida_em = @OcorridaEm,
       atualizado_em = NOW(), versao = versao + 1
 WHERE id = @Id AND versao = @Lida", new
            {
                o.Tipo, o.Descricao, o.Evidencia, o.Certeza, o.Estado, o.Impacto, o.Abordagem, o.ModuloCitado, o.RespostaPrometidaAte, o.OcorridaEm, o.Id, Lida = versaoLida
            }, cancellationToken: ct));
            return n == 1;
        }

        public async Task AuditarObjecaoAsync(Guid objecaoId, Guid oportunidadeId, string evento, int ator, object? antes, object? depois, string? motivo, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            await cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO crm_objecao_auditoria (objecao_id, oportunidade_id, evento, ator_usuario_id, antes, depois, motivo)
VALUES (@Obj, @Opp, @Evento, @Ator, @Antes::jsonb, @Depois::jsonb, @Motivo)", new
            {
                Obj = objecaoId, Opp = oportunidadeId, Evento = evento, Ator = ator, Motivo = motivo,
                Antes = antes == null ? null : JsonSerializer.Serialize(antes, Json),
                Depois = depois == null ? null : JsonSerializer.Serialize(depois, Json),
            }, cancellationToken: ct));
        }

        public async Task<IReadOnlyList<EntradaAudioConfirmada>> EntradasAudioConfirmadasAsync(Guid oportunidadeId, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            // So entradas que a pessoa confirmou; a proposta ainda pode ter sido apagada pela retencao (entao nada a importar).
            return (await cx.QueryAsync<EntradaAudioConfirmada>(new CommandDefinition(@"
SELECT id AS Id, usuario_id AS UsuarioId, proposta::text AS PropostaJson, resultado::text AS ResultadoJson, atualizado_em AS AtualizadaEm
  FROM crm_assistente_entrada
 WHERE oportunidade_id = @O AND estado = 'confirmada' AND proposta IS NOT NULL AND resultado IS NOT NULL
 ORDER BY atualizado_em", new { O = oportunidadeId }, cancellationToken: ct))).ToList();
        }

        public async Task<bool> ConsentimentoAudioAsync(int usuarioId, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            return await cx.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS (SELECT 1 FROM crm_assistente_consentimento WHERE usuario_id = @U AND revogado_em IS NULL)", new { U = usuarioId }, cancellationToken: ct));
        }

        public async Task<IReadOnlyList<Recomendacao>> ListarRecomendacoesAsync(Guid oportunidadeId, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            return (await cx.QueryAsync<Recomendacao>(new CommandDefinition(
                $"SELECT {ColRec} FROM crm_recomendacao WHERE oportunidade_id = @O ORDER BY prioridade, criado_em DESC", new { O = oportunidadeId }, cancellationToken: ct))).ToList();
        }

        public async Task<Recomendacao?> ObterRecomendacaoAsync(Guid id, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            return await cx.QueryFirstOrDefaultAsync<Recomendacao>(new CommandDefinition($"SELECT {ColRec} FROM crm_recomendacao WHERE id = @Id", new { Id = id }, cancellationToken: ct));
        }

        public async Task<bool> InserirRecomendacaoAsync(Recomendacao r, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            var n = await cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO crm_recomendacao (id, oportunidade_id, responsavel_usuario_id, regra, prioridade, impressao_digital, sugestao, hipotese, acao, evidencias, redacao_ia)
VALUES (@Id, @OportunidadeId, @ResponsavelUsuarioId, @Regra, @Prioridade, @ImpressaoDigital, @Sugestao, @Hipotese, @Acao, @EvidenciasJson::jsonb, @RedacaoIa)
ON CONFLICT DO NOTHING", r, cancellationToken: ct));
            return n == 1;
        }

        public async Task<bool> AtualizarRecomendacaoAsync(Recomendacao r, int versaoLida, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            var n = await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE crm_recomendacao SET estado = @Estado, motivo_rejeicao = @MotivoRejeicao, reacao = @Reacao, resultado = @Resultado, observacao = @Observacao,
       atualizado_em = NOW(), versao = versao + 1
 WHERE id = @Id AND versao = @Lida", new { r.Estado, r.MotivoRejeicao, r.Reacao, r.Resultado, r.Observacao, r.Id, Lida = versaoLida }, cancellationToken: ct));
            return n == 1;
        }

        public async Task RegistrarEventoAsync(Guid recomendacaoId, string evento, int? ator, object? detalhe, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            await cx.ExecuteAsync(new CommandDefinition(
                "INSERT INTO crm_recomendacao_evento (recomendacao_id, evento, ator_usuario_id, detalhe) VALUES (@R, @E, @A, @D::jsonb)",
                new { R = recomendacaoId, E = evento, A = ator, D = detalhe == null ? null : JsonSerializer.Serialize(detalhe, Json) }, cancellationToken: ct));
        }

        public async Task<IReadOnlyList<ObjecaoHistorica>> ObjecoesResolvidasComAbordagemAsync(string[] tipos, Guid exetoOportunidadeId, int limite, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            return (await cx.QueryAsync<ObjecaoHistorica>(new CommandDefinition(@"
SELECT id AS ObjecaoId, oportunidade_id AS OportunidadeId, tipo AS Tipo, abordagem AS Abordagem, evidencia AS Evidencia, atualizado_em AS AtualizadaEm
  FROM crm_objecao
 WHERE tipo = ANY(@Tipos) AND estado = 'resolvida' AND certeza = 'declarada' AND oportunidade_id <> @Exceto
   AND abordagem IS NOT NULL AND length(btrim(abordagem)) > 0
 ORDER BY atualizado_em DESC LIMIT @Limite", new { Tipos = tipos, Exceto = exetoOportunidadeId, Limite = limite }, cancellationToken: ct))).ToList();
        }

        public async Task<IReadOnlyList<ObjecaoDoResponsavel>> ObjecoesDoResponsavelAsync(int usuarioId, DateTime desde, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            return (await cx.QueryAsync<ObjecaoDoResponsavel>(new CommandDefinition(@"
SELECT ob.id AS ObjecaoId, ob.oportunidade_id AS OportunidadeId, ob.tipo AS Tipo, ob.estado AS Estado, ob.certeza AS Certeza, ob.impacto AS Impacto, ob.evidencia AS Evidencia,
       ob.abordagem AS Abordagem, ob.ocorrida_em AS OcorridaEm, ob.resposta_prometida_ate AS RespostaPrometidaAte
  FROM crm_objecao ob
  JOIN crm_oportunidade op ON op.id = ob.oportunidade_id
 WHERE op.responsavel_usuario_id = @U AND ob.ocorrida_em >= @Desde
 ORDER BY ob.ocorrida_em DESC", new { U = usuarioId, Desde = desde }, cancellationToken: ct))).ToList();
        }

        public async Task<IReadOnlyList<RecomendacaoDoResponsavel>> RecomendacoesDoResponsavelAsync(int usuarioId, DateTime desde, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            return (await cx.QueryAsync<RecomendacaoDoResponsavel>(new CommandDefinition(@"
SELECT r.id AS RecomendacaoId, r.oportunidade_id AS OportunidadeId, r.regra AS Regra, r.estado AS Estado, r.reacao AS Reacao, r.resultado AS Resultado, r.criado_em AS CriadoEm
  FROM crm_recomendacao r
  JOIN crm_oportunidade op ON op.id = r.oportunidade_id
 WHERE op.responsavel_usuario_id = @U AND r.criado_em >= @Desde
 ORDER BY r.criado_em DESC", new { U = usuarioId, Desde = desde }, cancellationToken: ct))).ToList();
        }
    }
}
