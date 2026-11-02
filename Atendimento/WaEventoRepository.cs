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
        /// <summary>account_update da conta (PARTNER_REMOVED etc.): chave = waba + evento + hash do corpo.</summary>
        public const string Conta = "conta";
        /// <summary>smb_message_echoes (coexistencia): mensagem enviada pelo app WhatsApp Business; chave = wamid.</summary>
        public const string Eco = "eco";
    }

    /// <summary>Para qual fluxo a mensagem foi capturada no ingresso (coluna wa_evento.rota).</summary>
    public static class RotaEvento
    {
        /// <summary>Restaurante (proprio, autorizado ou numero de teste geral): o fluxo de sempre, sem nenhuma mudanca.</summary>
        public const string Operacional = "operacional";
        /// <summary>Numero de teste com finalidade gestao: classifica o remetente (funcionario, contato, desconhecido, ambiguo).</summary>
        public const string Gestao = "gestao";
        /// <summary>Nenhum numero ativo tinha o Phone Number ID quando a mensagem chegou.</summary>
        public const string SemCanal = "sem_canal";

        public static string Para(CanalWhatsapp canal) =>
            canal.DeTeste && canal.FinalidadeTeste == FinalidadeTeste.Gestao ? Gestao : Operacional;
    }

    /// <summary>Quem mandou a mensagem para a loja Gestao (coluna wa_evento.categoria_remetente).</summary>
    public static class CategoriaRemetente
    {
        public const string FuncionarioVerificado = "funcionario_verificado";
        public const string ContatoCrm = "contato_crm";
        public const string Desconhecido = "desconhecido";
        public const string Ambiguo = "ambiguo";
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
        /// <summary>Canal da URL de webhook propria por onde chegou (nulo no webhook global).</summary>
        public Guid? IdCanal { get; set; }
        public string PayloadJson { get; set; } = "{}";
        public int Tentativas { get; set; }
        public DateTime RecebidoEm { get; set; }

        // ---- destino capturado no ingresso (imutavel: o reenvio da Meta nao sobrescreve) ----
        /// <summary><see cref="RotaEvento"/>. Nulo = evento sem captura (so testes antigos e status/eco).</summary>
        public string? Rota { get; set; }
        public DateTime? RotaCapturadaEm { get; set; }
        public Guid? IdCanalDestino { get; set; }
        public Guid? IdEstabelecimentoDestino { get; set; }
        /// <summary>O canal naquele instante: origem, teste, finalidade, principal, WABA e Phone Number ID.</summary>
        public string? RotaSnapshotJson { get; set; }
        /// <summary>wa_id de quem mandou. Nunca vai para log aberto (so mascarado).</summary>
        public string? Remetente { get; set; }
        public string? TipoMensagem { get; set; }
        /// <summary>ID da midia na Meta (audio, imagem...). Referencia para a Parte B baixar; o conteudo nunca e gravado aqui.</summary>
        public string? MidiaId { get; set; }
        public string? MidiaMime { get; set; }
        public DateTime? MensagemEm { get; set; }
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
        /// <summary>
        /// A conferencia de seguranca da rota falhou (destino alterado, acesso revogado, WABA errada...). Nao e "processado": fica
        /// com o codigo (403/409/422) e o motivo para diagnostico, e conta como erro no resumo da fila.
        /// </summary>
        Task MarcarBloqueadoAsync(Guid id, int codigo, string motivo);
        /// <summary>Categoria do remetente na loja Gestao (<see cref="CategoriaRemetente"/>), para o diagnostico da mensagem.</summary>
        Task RegistrarCategoriaAsync(Guid id, string categoria);
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
INSERT INTO wa_evento (id, tipo, chave, phone_number_id, display_phone, id_canal, payload,
                       rota, rota_capturada_em, id_canal_destino, id_estabelecimento_destino, rota_snapshot,
                       remetente, tipo_mensagem, midia_id, midia_mime, mensagem_em)
VALUES (@Id, @Tipo, @Chave, @PhoneNumberId, @DisplayPhone, @IdCanal, CAST(@PayloadJson AS jsonb),
        @Rota, @RotaCapturadaEm, @IdCanalDestino, @IdEstabelecimentoDestino, CAST(@RotaSnapshotJson AS jsonb),
        @Remetente, @TipoMensagem, @MidiaId, @MidiaMime, @MensagemEm)
-- Reenvio da Meta: o primeiro ingresso vale, inclusive o destino capturado (nunca e sobrescrito).
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
           AND (tipo <> 'mensagem' OR NOT EXISTS (
               SELECT 1 FROM wa_evento anterior
               WHERE anterior.tipo = 'mensagem' AND anterior.estado IN ('pendente', 'processando')
                 AND anterior.phone_number_id = wa_evento.phone_number_id
                 AND anterior.payload->'message'->>'from' = wa_evento.payload->'message'->>'from'
                 AND (anterior.recebido_em, anterior.id) < (wa_evento.recebido_em, wa_evento.id)))
         ORDER BY recebido_em
         LIMIT @Max
         FOR UPDATE SKIP LOCKED)
RETURNING e.id AS Id, e.tipo AS Tipo, e.chave AS Chave, e.phone_number_id AS PhoneNumberId, e.display_phone AS DisplayPhone,
          e.id_canal AS IdCanal, e.payload::text AS PayloadJson, e.tentativas AS Tentativas, e.recebido_em AS RecebidoEm,
          e.rota AS Rota, e.rota_capturada_em AS RotaCapturadaEm, e.id_canal_destino AS IdCanalDestino,
          e.id_estabelecimento_destino AS IdEstabelecimentoDestino, e.rota_snapshot::text AS RotaSnapshotJson,
          e.remetente AS Remetente, e.tipo_mensagem AS TipoMensagem, e.midia_id AS MidiaId, e.midia_mime AS MidiaMime,
          e.mensagem_em AS MensagemEm;",
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

        public async Task MarcarBloqueadoAsync(Guid id, int codigo, string motivo)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.ExecuteAsync(
                "UPDATE wa_evento SET estado = 'bloqueado', processado_em = NOW(), rota_codigo = @Codigo, motivo = @Motivo WHERE id = @Id;",
                new { Id = id, Codigo = codigo, Motivo = Cortar(motivo) });
        }

        public async Task RegistrarCategoriaAsync(Guid id, string categoria)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.ExecuteAsync("UPDATE wa_evento SET categoria_remetente = @Categoria WHERE id = @Id;", new { Id = id, Categoria = categoria });
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
       -- Rota bloqueada conta como erro: precisa aparecer, nunca some como processada.
       (COUNT(*) FILTER (WHERE estado IN ('erro', 'bloqueado') AND recebido_em > NOW() - INTERVAL '24 hours'))::int,
       MIN(recebido_em) FILTER (WHERE estado IN ('pendente', 'processando'))
  FROM wa_evento;");
            return new ResumoFila(pendentes, erros, maisAntigo);
        }

        private static string Cortar(string texto) => texto.Length > 500 ? texto[..500] : texto;
    }
}
