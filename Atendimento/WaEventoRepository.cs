using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace APIBack.Atendimento
{
    public static class TipoEvento
    {
        public const string Mensagem = "mensagem";
        public const string Status = "status";
    }

    /// <summary>Evento do webhook gravado antes de qualquer processamento.</summary>
    public sealed class WaEvento
    {
        public Guid Id { get; set; }
        public string Tipo { get; set; } = TipoEvento.Mensagem;
        /// <summary>Mensagem: id da mensagem na Meta. Status: id da mensagem + ":" + status.</summary>
        public string Chave { get; set; } = string.Empty;
        public string? PhoneNumberId { get; set; }
        public string? DisplayPhone { get; set; }
        public Guid? IdCanal { get; set; }
        public string PayloadJson { get; set; } = "{}";
        public int Tentativas { get; set; }
        public DateTime RecebidoEm { get; set; }
    }

    public sealed record ResumoFila(int Pendentes, int Erros, DateTime? PendenteMaisAntigoEm);

    public interface IWaEventoRepository
    {
        /// <summary>Grava o evento. Devolve false quando ele ja existia (a Meta reenviou): nada e duplicado.</summary>
        Task<bool> RegistrarAsync(WaEvento evento);
        /// <summary>Reivindica ate <paramref name="max"/> eventos prontos (varios workers nao pegam o mesmo).</summary>
        Task<IReadOnlyList<WaEvento>> ReivindicarAsync(int max);
        Task MarcarProcessadoAsync(Guid id);
        Task MarcarIgnoradoAsync(Guid id, string motivo);
        /// <summary>Falhou: tenta de novo depois de <paramref name="espera"/>, ou desiste ("erro") quando <paramref name="definitivo"/>.</summary>
        Task ReagendarAsync(Guid id, string erro, TimeSpan espera, bool definitivo);
        /// <summary>Eventos presos em "processando" (o processo caiu no meio) voltam para a fila.</summary>
        Task<int> RecuperarPresosAsync(TimeSpan alem);
        Task<ResumoFila> ResumoAsync();
    }

    public sealed class SqlWaEventoRepository : IWaEventoRepository
    {
        private readonly string _connectionString;

        public SqlWaEventoRepository(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection")
                ?? configuration["ConnectionStrings:DefaultConnection"]
                ?? throw new InvalidOperationException("Connection string 'DefaultConnection' nao encontrada.");
        }

        public async Task<bool> RegistrarAsync(WaEvento evento)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            var inseridos = await connection.ExecuteAsync(@"
INSERT INTO wa_evento (id, tipo, chave, phone_number_id, display_phone, id_canal, payload)
VALUES (@Id, @Tipo, @Chave, @PhoneNumberId, @DisplayPhone, @IdCanal, CAST(@PayloadJson AS jsonb))
ON CONFLICT (tipo, chave) DO NOTHING;", evento);
            return inseridos > 0;
        }

        public async Task<IReadOnlyList<WaEvento>> ReivindicarAsync(int max)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            var linhas = await connection.QueryAsync<WaEvento>(@"
UPDATE wa_evento e
   SET estado = 'processando', tentativas = e.tentativas + 1, iniciado_em = NOW()
 WHERE e.id IN (
        SELECT id FROM wa_evento
         WHERE estado = 'pendente' AND proxima_tentativa_em <= NOW()
         ORDER BY recebido_em
         LIMIT @Max
         FOR UPDATE SKIP LOCKED)
RETURNING e.id AS Id, e.tipo AS Tipo, e.chave AS Chave, e.phone_number_id AS PhoneNumberId, e.display_phone AS DisplayPhone,
          e.id_canal AS IdCanal, e.payload::text AS PayloadJson, e.tentativas AS Tentativas, e.recebido_em AS RecebidoEm;",
                new { Max = max });
            return linhas.OrderBy(l => l.RecebidoEm).ToList();
        }

        public async Task MarcarProcessadoAsync(Guid id)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.ExecuteAsync(
                "UPDATE wa_evento SET estado = 'processado', processado_em = NOW(), motivo = NULL WHERE id = @Id;", new { Id = id });
        }

        public async Task MarcarIgnoradoAsync(Guid id, string motivo)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.ExecuteAsync(
                "UPDATE wa_evento SET estado = 'ignorado', processado_em = NOW(), motivo = @Motivo WHERE id = @Id;",
                new { Id = id, Motivo = Cortar(motivo) });
        }

        public async Task ReagendarAsync(Guid id, string erro, TimeSpan espera, bool definitivo)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.ExecuteAsync(@"
UPDATE wa_evento
   SET estado = CASE WHEN @Definitivo THEN 'erro' ELSE 'pendente' END,
       motivo = @Erro,
       proxima_tentativa_em = NOW() + make_interval(secs => @Segundos),
       processado_em = CASE WHEN @Definitivo THEN NOW() ELSE processado_em END
 WHERE id = @Id;", new { Id = id, Erro = Cortar(erro), Definitivo = definitivo, Segundos = espera.TotalSeconds });
        }

        public async Task<int> RecuperarPresosAsync(TimeSpan alem)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            return await connection.ExecuteAsync(@"
UPDATE wa_evento
   SET estado = 'pendente', motivo = 'processamento interrompido (reiniciado)'
 WHERE estado = 'processando' AND iniciado_em < NOW() - make_interval(secs => @Segundos);",
                new { Segundos = alem.TotalSeconds });
        }

        public async Task<ResumoFila> ResumoAsync()
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            var (pendentes, erros, maisAntigo) = await connection.QuerySingleAsync<(int, int, DateTime?)>(@"
SELECT (COUNT(*) FILTER (WHERE estado IN ('pendente', 'processando')))::int,
       (COUNT(*) FILTER (WHERE estado = 'erro' AND recebido_em > NOW() - INTERVAL '24 hours'))::int,
       MIN(recebido_em) FILTER (WHERE estado IN ('pendente', 'processando'))
  FROM wa_evento;");
            return new ResumoFila(pendentes, erros, maisAntigo);
        }

        private static string Cortar(string texto) => texto.Length > 500 ? texto[..500] : texto;
    }
}
