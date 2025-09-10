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

        public SqlConversationRepository(IConfiguration configuracao)
        {
            _connectionString = configuracao.GetConnectionString("DefaultConnection")
                                 ?? configuracao["ConnectionStrings:DefaultConnection"]
                                 ?? throw new InvalidOperationException("Connection string 'DefaultConnection' não encontrada.");
        }

        public async Task<Conversation?> ObterPorIdAsync(Guid id)
        {
            const string sql = @"
SELECT
  id_conversa,
  id_wa,
  modo,
  agente_designado,
  ultimo_usuario_em,
  janela_24h_expira_em,
  criado_em,
  atualizado_em
FROM conversas
WHERE id_conversa = @IdConversa;";

            await using var conexao = new NpgsqlConnection(_connectionString);
            var conversa = await conexao.QueryFirstOrDefaultAsync<Conversation>(sql, new { IdConversa = id });
            return conversa;
        }

        public async Task InserirOuAtualizarAsync(Conversation conversa)
        {
            const string sql = @"
INSERT INTO conversas
  (id_conversa, id_wa, modo, agente_designado, ultimo_usuario_em, janela_24h_expira_em, criado_em, atualizado_em)
VALUES
  (@IdConversa, @IdWa, @Modo, @AgenteDesignado, @UltimoUsuarioEm, @Janela24hExpiraEm, @CriadoEm, @AtualizadoEm)
ON CONFLICT (id_conversa) DO UPDATE SET
  id_wa = EXCLUDED.id_wa,
  modo = EXCLUDED.modo,
  agente_designado = EXCLUDED.agente_designado,
  ultimo_usuario_em = EXCLUDED.ultimo_usuario_em,
  janela_24h_expira_em = EXCLUDED.janela_24h_expira_em,
  atualizado_em = EXCLUDED.atualizado_em;";

            await using var conexao = new NpgsqlConnection(_connectionString);
            await conexao.ExecuteAsync(sql, new
            {
                conversa.IdConversa,
                conversa.IdWa,
                conversa.Modo,
                conversa.AgenteDesignado,
                conversa.UltimoUsuarioEm,
                conversa.Janela24hExpiraEm,
                conversa.CriadoEm,
                conversa.AtualizadoEm
            });
        }

        public async Task DefinirModoAsync(Guid id, ModoConversa modo, string? agenteDesignado)
        {
            const string sql = @"
UPDATE conversas
SET modo = @Modo,
    agente_designado = @AgenteDesignado,
    atualizado_em = NOW()
WHERE id_conversa = @IdConversa;";

            await using var conexao = new NpgsqlConnection(_connectionString);
            await conexao.ExecuteAsync(sql, new
            {
                IdConversa = id,
                Modo = modo,
                AgenteDesignado = agenteDesignado
            });
        }

        public async Task AcrescentarMensagemAsync(Message mensagem)
        {
            const string inserirMensagemSql = @"
INSERT INTO mensagens
  (id, id_conversa, direcao, tipo, status, id_provedor, codigo_erro, mensagem_erro, tentativas, criada_por, data_envio, data_entrega, data_leitura, data_criacao)
VALUES
  (@Id, @IdConversa, @Direcao, @Tipo, @Status, @IdProvedor, @CodigoErro, @MensagemErro, @Tentativas, @CriadaPor, @DataEnvio, @DataEntrega, @DataLeitura, @DataCriacao)
ON CONFLICT (id) DO NOTHING;";

            await using var conexao = new NpgsqlConnection(_connectionString);
            await conexao.OpenAsync();
            await using var tx = await conexao.BeginTransactionAsync();

            try
            {
                await conexao.ExecuteAsync(inserirMensagemSql, new
                {
                    mensagem.Id,
                    mensagem.IdConversa,
                    mensagem.Direcao,
                    mensagem.Tipo,
                    mensagem.Status,
                    // Usa IdProvedor, caindo para IdMensagemWa quando nulo/blank
                    IdProvedor = string.IsNullOrWhiteSpace(mensagem.IdProvedor)
                        ? mensagem.IdMensagemWa
                        : mensagem.IdProvedor,
                    mensagem.CodigoErro,
                    mensagem.MensagemErro,
                    mensagem.Tentativas,
                    mensagem.CriadaPor,
                    mensagem.DataEnvio,
                    mensagem.DataEntrega,
                    mensagem.DataLeitura,
                    mensagem.DataCriacao
                }, transaction: tx);

                await tx.CommitAsync();
            }
            catch
            {
                await tx.RollbackAsync();
                throw;
            }
        }

        public async Task<bool> ExisteIdMensagemWaAsync(string idMensagemWa)
        {
            // Usa a coluna existente id_provedor para idempotência de mensagens externas (WA)
            const string sql = "SELECT 1 FROM mensagens WHERE id_provedor = @IdMensagemWa LIMIT 1;";
            await using var conexao = new NpgsqlConnection(_connectionString);
            var existe = await conexao.ExecuteScalarAsync<int?>(sql, new { IdMensagemWa = idMensagemWa });
            return existe.HasValue;
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
