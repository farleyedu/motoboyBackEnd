// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Threading.Tasks;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Models;
using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace APIBack.Automation.Infra
{
    public class SqlConversationRepository : IConversationRepository
    {
        private readonly string _connectionString;
        private static bool _indexesEnsured;

        public SqlConversationRepository(IConfiguration config)
        {
            _connectionString = config.GetConnectionString("DefaultConnection")
                                 ?? config["ConnectionStrings:DefaultConnection"]
                                 ?? throw new InvalidOperationException("Connection string 'DefaultConnection' não encontrada.");

            // Garante índice único de idempotência (executa uma vez por processo)
            if (!_indexesEnsured)
            {
                try
                {
                    using var cx = new NpgsqlConnection(_connectionString);
                    cx.Execute("CREATE UNIQUE INDEX IF NOT EXISTS ux_mensagens_id_provedor ON mensagens (id_provedor);");
                    _indexesEnsured = true;
                }
                catch
                {
                    // Não interrompe a aplicação em caso de erro ao criar índice.
                }
            }
        }

        // Helpers de conversão para DateTime (UTC-safe)
        private static DateTime? ToUtc(DateTime? dt)
            => dt.HasValue ? DateTime.SpecifyKind(dt.Value, DateTimeKind.Utc) : (DateTime?)null;

        private static DateTime ToUtcNonNull(DateTime dt)
            => DateTime.SpecifyKind(dt, DateTimeKind.Utc);

        public async Task<Conversation?> ObterPorIdAsync(Guid id)
        {
            const string sql = @"
SELECT
  id                         AS Id,
  canal                      AS Canal,
  estado                     AS Estado,
  id_agente_atribuido        AS IdAgenteAtribuido,
  data_ultima_entrada        AS DataUltimaEntrada,
  janela_24h_fim             AS Janela24hFim,
  data_criacao               AS DataCriacao,
  data_atualizacao           AS DataAtualizacao
FROM conversas
WHERE id = @Id;";

            await using var cx = new NpgsqlConnection(_connectionString);
            var row = await cx.QueryFirstOrDefaultAsync<(Guid Id, string? Canal, string? Estado, Guid? IdAgenteAtribuido, DateTime? DataUltimaEntrada, DateTime? Janela24hFim, DateTime DataCriacao, DateTime DataAtualizacao)>(sql, new { Id = id });
            if (row.Equals(default((Guid, string?, string?, Guid?, DateTime?, DateTime?, DateTime, DateTime)))) return null;

            var conv = new Conversation
            {
                IdConversa        = row.Id,
                IdWa              = string.Empty, // não existe no schema
                Modo              = row.IdAgenteAtribuido == null ? ModoConversa.Bot : ModoConversa.Humano,
                AgenteDesignado   = row.IdAgenteAtribuido?.ToString(),
                UltimoUsuarioEm   = ToUtc(row.DataUltimaEntrada) ?? default,
                Janela24hExpiraEm = ToUtc(row.Janela24hFim),
                CriadoEm          = ToUtcNonNull(row.DataCriacao),
                AtualizadoEm      = ToUtcNonNull(row.DataAtualizacao)
            };
            return conv;
        }

        public async Task InserirOuAtualizarAsync(Conversation conversa)
        {
            var criado     = conversa.CriadoEm != default ? DateTime.SpecifyKind(conversa.CriadoEm, DateTimeKind.Utc) : DateTime.UtcNow;
            var atualizado = conversa.AtualizadoEm.HasValue && conversa.AtualizadoEm.Value != default
                                ? DateTime.SpecifyKind(conversa.AtualizadoEm.Value, DateTimeKind.Utc)
                                : criado;

            const string sql = @"
INSERT INTO conversas
  (id, canal, data_primeira_mensagem, data_ultima_mensagem, data_criacao, data_atualizacao)
VALUES
  (@Id, 'whatsapp', @PrimeiraMsg, @UltimaMsg, @CriadoEm, @AtualizadoEm)
ON CONFLICT (id) DO UPDATE SET
  data_ultima_mensagem = GREATEST(conversas.data_ultima_mensagem, EXCLUDED.data_ultima_mensagem),
  data_atualizacao     = EXCLUDED.data_atualizacao;";

            await using var cx = new NpgsqlConnection(_connectionString);
            await cx.ExecuteAsync(sql, new
            {
                Id           = conversa.IdConversa,
                PrimeiraMsg  = criado,
                UltimaMsg    = atualizado,
                CriadoEm     = criado,
                AtualizadoEm = atualizado
            });
        }

        public async Task DefinirModoAsync(Guid id, ModoConversa modo, string? agenteDesignado)
        {
            Guid? agenteId = null;
            if (!string.IsNullOrWhiteSpace(agenteDesignado) && Guid.TryParse(agenteDesignado, out var g))
                agenteId = g;

            string sql;
            object param;

            if (modo == ModoConversa.Humano)
            {
                sql = @"
UPDATE conversas
   SET id_agente_atribuido = @AgenteId,
       data_atualizacao     = NOW()
 WHERE id = @Id;";
                param = new { Id = id, AgenteId = (object?)agenteId };
            }
            else
            {
                sql = @"
UPDATE conversas
   SET id_agente_atribuido = NULL,
       data_atualizacao     = NOW()
 WHERE id = @Id;";
                param = new { Id = id };
            }

            await using var cx = new NpgsqlConnection(_connectionString);
            await cx.ExecuteAsync(sql, param);
        }

        public async Task AcrescentarMensagemAsync(Message mensagem)
        {
            var idMsg   = mensagem.Id != Guid.Empty ? mensagem.Id : Guid.NewGuid();
            var quando  = mensagem.DataHora != default ? DateTime.SpecifyKind(mensagem.DataHora, DateTimeKind.Utc) : DateTime.UtcNow;

            var direcao = mensagem.Direcao == DirecaoMensagem.Entrada ? "entrada" : "saida";
            var tipo    = string.IsNullOrWhiteSpace(mensagem.Tipo) ? "texto" : mensagem.Tipo!;
            var status  = string.IsNullOrWhiteSpace(mensagem.Status)
                          ? (mensagem.Direcao == DirecaoMensagem.Entrada ? "entregue" : "fila")
                          : mensagem.Status!;

            var idProv    = !string.IsNullOrWhiteSpace(mensagem.IdProvedor) ? mensagem.IdProvedor : mensagem.IdMensagemWa;
            var criadaPor = string.IsNullOrWhiteSpace(mensagem.CriadaPor)
                            ? (mensagem.Direcao == DirecaoMensagem.Entrada ? "bot" : "agente:1")
                            : mensagem.CriadaPor!;

            const string insertMsg = @"
INSERT INTO mensagens
  (id, id_conversa, direcao, tipo, status, id_provedor, conteudo, codigo_erro, mensagem_erro, tentativas, criada_por, data_envio, data_entrega, data_leitura, data_criacao)
VALUES
  (@Id, @IdConversa, @Direcao, @Tipo, @Status, @IdProvedor, @Conteudo, @CodigoErro, @MensagemErro, @Tentativas, @CriadaPor, @DataEnvio, @DataEntrega, @DataLeitura, @DataCriacao)
ON CONFLICT DO NOTHING;";

            const string updConv = @"
UPDATE conversas
   SET data_ultima_mensagem = GREATEST(COALESCE(data_ultima_mensagem, @Quando), @Quando),
       data_ultima_entrada  = CASE WHEN @Direcao = 'entrada' THEN @Quando ELSE data_ultima_entrada END,
       data_ultima_saida    = CASE WHEN @Direcao = 'saida'   THEN @Quando ELSE data_ultima_saida   END,
       data_primeira_mensagem = COALESCE(data_primeira_mensagem, @Quando),
       janela_24h_inicio    = CASE WHEN @Direcao = 'entrada' AND janela_24h_inicio IS NULL THEN @Quando ELSE janela_24h_inicio END,
       janela_24h_fim       = CASE WHEN @Direcao = 'entrada' AND janela_24h_fim    IS NULL THEN (@Quando + interval '24 hour') ELSE janela_24h_fim END,
       data_atualizacao     = NOW()
 WHERE id = @IdConversa;";

            await using var cx = new NpgsqlConnection(_connectionString);
            await cx.OpenAsync();
            await using var tx = await cx.BeginTransactionAsync();

            try
            {
                await cx.ExecuteAsync(insertMsg, new
                {
                    Id           = idMsg,
                    IdConversa   = mensagem.IdConversa,
                    Direcao      = direcao,
                    Tipo         = tipo,
                    Status       = status,
                    IdProvedor   = (object?)idProv,
                    Conteudo     = mensagem.Conteudo,
                    CodigoErro   = (string?)mensagem.CodigoErro,
                    MensagemErro = (string?)mensagem.MensagemErro,
                    Tentativas   = mensagem.Tentativas,
                    CriadaPor    = criadaPor,
                    DataEnvio    = (DateTime?)null,
                    DataEntrega  = (DateTime?)null,
                    DataLeitura  = (DateTime?)null,
                    DataCriacao  = quando
                }, transaction: tx);

                await cx.ExecuteAsync(updConv, new
                {
                    IdConversa = mensagem.IdConversa,
                    Quando     = quando,
                    Direcao    = direcao
                }, transaction: tx);

                await tx.CommitAsync();
            }
            catch
            {
                await tx.RollbackAsync();
                throw;
            }
        }

        public async Task<bool> ExisteIdMensagemPorProvedorWaAsync(string idMensagemWa)
        {
            const string sql = "SELECT 1 FROM mensagens WHERE id_provedor = @IdMensagemWa LIMIT 1;";
            await using var cx = new NpgsqlConnection(_connectionString);
            var existe = await cx.ExecuteScalarAsync<int?>(sql, new { IdMensagemWa = idMensagemWa });
            return existe.HasValue;
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) =================

