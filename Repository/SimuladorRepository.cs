using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using APIBack.DTOs.Simulador;
using APIBack.Service;
using Dapper;
using Npgsql;

namespace APIBack.Repository
{
    public interface ISimuladorRepository
    {
        Task<SimEventoDto> AddEventoAsync(Guid estabelecimentoId, int? usuarioId, string? usuarioNome, SimEventoInput evento);
        Task<IReadOnlyList<SimEventoDto>> ListEventosAsync(
            Guid estabelecimentoId, string? entidade, string? entidadeRef, string? cenarioId, string? status, int limit, long? antesDeId);
        Task<int> ClearEventosAsync(Guid estabelecimentoId, string? entidade, string? cenarioId);
        Task<SimResumoDto> GetResumoAsync(Guid estabelecimentoId);
        Task<SimSessaoDto> UpsertSessaoAsync(Guid estabelecimentoId, int? usuarioId, string tipo, string? refe, string? titulo, string estadoJson);
        Task<IReadOnlyList<SimSessaoDto>> ListSessoesAsync(Guid estabelecimentoId, string? tipo, bool apenasAtivas);
        Task<bool> EncerrarSessaoAsync(Guid estabelecimentoId, Guid sessaoId);
    }

    /// <summary>Eventos, sessoes e resumo do simulador (tabelas da migration 20260930_01).</summary>
    public sealed class SimuladorRepository : ISimuladorRepository
    {
        private readonly NpgsqlDataSource _dataSource;

        public SimuladorRepository(NpgsqlDataSource dataSource)
        {
            _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        }

        private const string EventoColumns = @"
       id AS Id, entidade AS Entidade, entidade_ref AS EntidadeRef, tipo AS Tipo, titulo AS Titulo, detalhe AS Detalhe,
       status AS Status, cenario_id AS CenarioId, usuario_nome AS UsuarioNome, criado_em_utc AS CriadoEmUtc";

        public async Task<SimEventoDto> AddEventoAsync(Guid estabelecimentoId, int? usuarioId, string? usuarioNome, SimEventoInput evento)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            return await connection.QuerySingleAsync<SimEventoDto>($@"
INSERT INTO simulador_evento (estabelecimento_id, entidade, entidade_ref, tipo, titulo, detalhe, status, cenario_id, dados,
                              usuario_id, usuario_nome)
VALUES (@Est, @Entidade, @EntidadeRef, @Tipo, @Titulo, @Detalhe, @Status, @CenarioId, @Dados::jsonb, @UsuarioId, COALESCE(@UsuarioNome, (SELECT nome::text FROM usuario WHERE id = @UsuarioId)))
RETURNING{EventoColumns};", new
            {
                Est = estabelecimentoId, evento.Entidade, evento.EntidadeRef, evento.Tipo, evento.Titulo, evento.Detalhe,
                evento.Status, evento.CenarioId, Dados = evento.DadosJson, UsuarioId = usuarioId, UsuarioNome = usuarioNome
            });
        }

        public async Task<IReadOnlyList<SimEventoDto>> ListEventosAsync(
            Guid estabelecimentoId, string? entidade, string? entidadeRef, string? cenarioId, string? status, int limit, long? antesDeId)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            return (await connection.QueryAsync<SimEventoDto>($@"
SELECT{EventoColumns}
  FROM simulador_evento
 WHERE estabelecimento_id = @Est
   AND (@Entidade IS NULL OR entidade = @Entidade)
   AND (@Ref IS NULL OR entidade_ref = @Ref)
   AND (@Cenario IS NULL OR cenario_id = @Cenario)
   AND (@Status IS NULL OR status = @Status)
   AND (@Antes IS NULL OR id < @Antes)
 ORDER BY id DESC
 LIMIT @Limit;", new
            {
                Est = estabelecimentoId, Entidade = entidade, Ref = entidadeRef, Cenario = cenarioId, Status = status,
                Antes = antesDeId, Limit = Math.Clamp(limit, 1, 200)
            })).ToList();
        }

        public async Task<int> ClearEventosAsync(Guid estabelecimentoId, string? entidade, string? cenarioId)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            return await connection.ExecuteAsync(@"
DELETE FROM simulador_evento
 WHERE estabelecimento_id = @Est
   AND (@Entidade IS NULL OR entidade = @Entidade)
   AND (@Cenario IS NULL OR cenario_id = @Cenario);", new { Est = estabelecimentoId, Entidade = entidade, Cenario = cenarioId });
        }

        // ------------------------------------------------------------------ resumo do hub

        public async Task<SimResumoDto> GetResumoAsync(Guid estabelecimentoId)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            var resumo = new SimResumoDto();

            // Cada bloco e defensivo: antes da migration (ou sem alguma coluna) o hub abre mesmo assim, zerado.
            async Task<T> Safe<T>(Func<Task<T>> read, T fallback)
            {
                try { return await read(); }
                catch (PostgresException) { return fallback; }
            }

            var p = new { Est = estabelecimentoId };

            var motoboyStats = await Safe(() => connection.QuerySingleAsync<(int Total, int Novos, int Sessoes, DateTime? Ultima)>(@"
SELECT COUNT(*)::int,
       COUNT(*) FILTER (WHERE me.created_at_utc >= NOW() - INTERVAL '24 hours')::int,
       (SELECT COUNT(*)::int FROM motoboy_active_sessions s
          JOIN motoboy sm ON sm.id = s.motoboy_id
         WHERE s.id_estabelecimento = @Est AND s.origin = 'simulator' AND sm.is_simulated = TRUE
           AND s.ended_at_utc IS NULL AND s.revoked_at IS NULL AND s.expires_at_utc > NOW()),
       (SELECT MAX(s.last_seen_at) FROM motoboy_active_sessions s
          JOIN motoboy sm ON sm.id = s.motoboy_id
         WHERE s.id_estabelecimento = @Est AND s.origin = 'simulator' AND sm.is_simulated = TRUE
           AND s.ended_at_utc IS NULL AND s.revoked_at IS NULL AND s.expires_at_utc > NOW())
  FROM motoboy_estabelecimento me
  JOIN motoboy m ON m.id = me.motoboy_id
 WHERE me.estabelecimento_id = @Est AND me.ativo = TRUE AND m.is_simulated = TRUE;", p), (Total: 0, Novos: 0, Sessoes: 0, Ultima: (DateTime?)null));

            var clienteStats = await Safe(() => connection.QuerySingleAsync<(int Total, int Novos)>(@"
SELECT COUNT(*)::int, COUNT(*) FILTER (WHERE data_criacao >= NOW() - INTERVAL '24 hours')::int
  FROM clientes WHERE id_estabelecimento = @Est AND simulado = TRUE AND ativo = TRUE;", p), (Total: 0, Novos: 0));

            var pedidoStats = await Safe(() => connection.QuerySingleAsync<int>(@"
SELECT COUNT(*)::int FROM pedido
 WHERE id_estabelecimento = @Est AND origem = 'simulador' AND COALESCE(status_pedido, 1) IN (1, 2, 5);", p), 0);

            var conversaStats = await Safe(() => connection.QuerySingleAsync<(int Total, DateTime? Ultima)>(@"
SELECT COUNT(DISTINCT c.id)::int, MAX(COALESCE(c.data_ultima_mensagem, c.data_criacao))
  FROM conversas c
  JOIN clientes cl ON cl.id = c.id_cliente
 WHERE c.id_estabelecimento = @Est AND cl.simulado = TRUE
   AND c.estado::text NOT IN ('fechado_automaticamente', 'fechado_agente', 'arquivada');", p), (Total: 0, Ultima: (DateTime?)null));

            var sessoes = await Safe(() => connection.QueryAsync<(string Tipo, int Ativas, DateTime? Ultima)>(@"
SELECT tipo, COUNT(*)::int, MAX(ultima_atividade_utc)
  FROM simulador_sessao WHERE estabelecimento_id = @Est AND ativa = TRUE GROUP BY tipo;", p), Enumerable.Empty<(string Tipo, int Ativas, DateTime? Ultima)>());
            var sessaoPorTipo = sessoes.ToDictionary(item => item.Tipo, item => item);

            var ultimoEvento = await Safe(() => connection.QueryAsync<(string Entidade, DateTime Ultima)>(@"
SELECT entidade, MAX(criado_em_utc) FROM simulador_evento WHERE estabelecimento_id = @Est GROUP BY entidade;", p),
                Enumerable.Empty<(string Entidade, DateTime Ultima)>());
            var ultimoPorEntidade = ultimoEvento.ToDictionary(item => item.Entidade, item => item.Ultima);

            int cliSessoes = sessaoPorTipo.TryGetValue("cliente", out var sc) ? sc.Ativas : 0;
            int pedSessoes = pedidoStats;
            resumo.Sessoes = new List<SimSessaoResumoDto>
            {
                new() { Tipo = "motoboy", Ativas = motoboyStats.Sessoes, UltimaAtividadeUtc = motoboyStats.Ultima },
                new() { Tipo = "cliente", Ativas = cliSessoes,
                        UltimaAtividadeUtc = Max(sessaoPorTipo.TryGetValue("cliente", out var sc2) ? sc2.Ultima : null, Get(ultimoPorEntidade, "cliente")) },
                new() { Tipo = "pedido", Ativas = pedSessoes, UltimaAtividadeUtc = Get(ultimoPorEntidade, "pedido") },
                new() { Tipo = "conversa", Ativas = conversaStats.Total, UltimaAtividadeUtc = Max(conversaStats.Ultima, Get(ultimoPorEntidade, "conversa")) },
            };

            var (simSerie, simUlt) = await Series(connection, estabelecimentoId, "TRUE");
            var (motSerie, motUlt) = await Series(connection, estabelecimentoId, "entidade = 'motoboy'");
            var (cliSerie, cliUlt) = await Series(connection, estabelecimentoId, "entidade IN ('cliente', 'conversa')");
            var (pedSerie, pedUlt) = await Series(connection, estabelecimentoId, "entidade = 'pedido'");
            var (msgSerie, msgUlt) = await Series(connection, estabelecimentoId, "tipo = 'mensagem_cliente'");
            var (altSerie, altUlt) = await Series(connection, estabelecimentoId, "status <> 'sucesso'");
            var altAnterior = await Safe(() => connection.ExecuteScalarAsync<int>(@"
SELECT COUNT(*)::int FROM simulador_evento
 WHERE estabelecimento_id = @Est AND status <> 'sucesso'
   AND criado_em_utc >= NOW() - INTERVAL '48 hours' AND criado_em_utc < NOW() - INTERVAL '24 hours';", p), 0);

            var totalSessoes = motoboyStats.Sessoes + sessaoPorTipo.Values.Sum(item => item.Ativas) + pedidoStats + conversaStats.Total;
            var novasSessoes = await Safe(() => connection.ExecuteScalarAsync<int>(@"
SELECT COUNT(*)::int FROM simulador_sessao WHERE estabelecimento_id = @Est AND criada_em_utc >= NOW() - INTERVAL '24 hours';", p), 0);
            var mensagens = await Safe(() => connection.ExecuteScalarAsync<int>(@"
SELECT COUNT(*)::int FROM simulador_evento WHERE estabelecimento_id = @Est AND tipo = 'mensagem_cliente';", p), 0);
            var alertas = await Safe(() => connection.ExecuteScalarAsync<int>(@"
SELECT COUNT(*)::int FROM simulador_evento
 WHERE estabelecimento_id = @Est AND status <> 'sucesso' AND criado_em_utc >= NOW() - INTERVAL '24 hours';", p), 0);

            resumo.Simulacoes = new SimMetricaDto { Total = totalSessoes, Delta = novasSessoes, Serie = simSerie };
            resumo.Motoboys = new SimMetricaDto { Total = motoboyStats.Total, Delta = motoboyStats.Novos, Serie = motSerie };
            resumo.Clientes = new SimMetricaDto { Total = clienteStats.Total, Delta = clienteStats.Novos, Serie = cliSerie };
            resumo.Pedidos = new SimMetricaDto { Total = pedidoStats, Delta = pedUlt, Serie = pedSerie };
            resumo.Mensagens = new SimMetricaDto { Total = mensagens, Delta = msgUlt, Serie = msgSerie };
            resumo.Alertas = new SimMetricaDto { Total = alertas, Delta = altUlt - altAnterior, Serie = altSerie };
            _ = simUlt; _ = motUlt; _ = cliUlt;
            return resumo;
        }

        private static DateTime? Get(Dictionary<string, DateTime> map, string key) => map.TryGetValue(key, out var value) ? value : null;

        private static DateTime? Max(DateTime? a, DateTime? b) => a.HasValue && b.HasValue ? (a > b ? a : b) : a ?? b;

        /// <summary>12 baldes de 2 h nas ultimas 24 h (do mais antigo ao mais novo) e o total do periodo.</summary>
        private static async Task<(int[] Serie, int Total)> Series(NpgsqlConnection connection, Guid estabelecimentoId, string filter)
        {
            var serie = new int[12];
            try
            {
                // O filtro e sempre texto fixo deste arquivo (nunca do cliente).
                var rows = await connection.QueryAsync<(int Bucket, int Total)>($@"
SELECT LEAST(11, GREATEST(0, 11 - FLOOR(EXTRACT(EPOCH FROM (NOW() - criado_em_utc)) / 7200)::int)) AS bucket, COUNT(*)::int
  FROM simulador_evento
 WHERE estabelecimento_id = @Est AND criado_em_utc >= NOW() - INTERVAL '24 hours' AND ({filter})
 GROUP BY 1;", new { Est = estabelecimentoId });
                foreach (var (bucket, total) in rows) serie[bucket] = total;
            }
            catch (PostgresException)
            {
                // tabela ainda nao existe: serie zerada
            }
            return (serie, serie.Sum());
        }

        // ------------------------------------------------------------------ sessoes

        private const string SessaoColumns = @"
       id AS Id, tipo AS Tipo, ref AS Ref, titulo AS Titulo, estado::text AS Estado, ativa AS Ativa,
       criada_em_utc AS CriadaEmUtc, ultima_atividade_utc AS UltimaAtividadeUtc";

        public async Task<SimSessaoDto> UpsertSessaoAsync(Guid estabelecimentoId, int? usuarioId, string tipo, string? refe, string? titulo, string estadoJson)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            var existing = await connection.QuerySingleOrDefaultAsync<Guid?>(@"
SELECT id FROM simulador_sessao
 WHERE estabelecimento_id = @Est AND tipo = @Tipo AND COALESCE(ref, '') = COALESCE(@Ref, '')
   AND COALESCE(usuario_id, 0) = COALESCE(@Usuario, 0)
 ORDER BY ultima_atividade_utc DESC LIMIT 1;", new { Est = estabelecimentoId, Tipo = tipo, Ref = refe, Usuario = usuarioId });

            if (existing.HasValue)
            {
                return await connection.QuerySingleAsync<SimSessaoDto>($@"
UPDATE simulador_sessao
   SET titulo = @Titulo, estado = @Estado::jsonb, ativa = TRUE, ultima_atividade_utc = NOW()
 WHERE id = @Id
RETURNING{SessaoColumns};", new { Id = existing.Value, Titulo = titulo, Estado = estadoJson });
            }

            return await connection.QuerySingleAsync<SimSessaoDto>($@"
INSERT INTO simulador_sessao (id, estabelecimento_id, usuario_id, tipo, ref, titulo, estado)
VALUES (@Id, @Est, @Usuario, @Tipo, @Ref, @Titulo, @Estado::jsonb)
RETURNING{SessaoColumns};", new
            {
                Id = Guid.NewGuid(), Est = estabelecimentoId, Usuario = usuarioId, Tipo = tipo, Ref = refe, Titulo = titulo, Estado = estadoJson
            });
        }

        public async Task<IReadOnlyList<SimSessaoDto>> ListSessoesAsync(Guid estabelecimentoId, string? tipo, bool apenasAtivas)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            return (await connection.QueryAsync<SimSessaoDto>($@"
SELECT{SessaoColumns} FROM simulador_sessao
 WHERE estabelecimento_id = @Est AND (@Tipo IS NULL OR tipo = @Tipo) AND (@Ativas = FALSE OR ativa = TRUE)
 ORDER BY ultima_atividade_utc DESC LIMIT 100;", new { Est = estabelecimentoId, Tipo = tipo, Ativas = apenasAtivas })).ToList();
        }

        public async Task<bool> EncerrarSessaoAsync(Guid estabelecimentoId, Guid sessaoId)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            var affected = await connection.ExecuteAsync(@"
UPDATE simulador_sessao SET ativa = FALSE, ultima_atividade_utc = NOW()
 WHERE id = @Id AND estabelecimento_id = @Est AND ativa = TRUE;", new { Id = sessaoId, Est = estabelecimentoId });
            return affected > 0;
        }
    }
}
