using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Automation.Dtos;
using APIBack.Automation.Interfaces;
using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace APIBack.Automation.Infra
{
    public class SqlNauticaPainelRepository : INauticaPainelRepository
    {
        private readonly string _connectionString;
        private static volatile bool _colunasEnsured;
        private static readonly SemaphoreSlim ColunasLock = new(1, 1);

        public SqlNauticaPainelRepository(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection")
                ?? configuration["ConnectionStrings:DefaultConnection"]
                ?? throw new InvalidOperationException("Connection string 'DefaultConnection' nao encontrada.");
        }

        private async Task EnsureColunasAsync()
        {
            if (_colunasEnsured)
            {
                return;
            }

            await ColunasLock.WaitAsync();
            try
            {
                if (_colunasEnsured)
                {
                    return;
                }

                const string sql = @"
ALTER TABLE cliente_nautica ADD COLUMN IF NOT EXISTS etapa_atual VARCHAR(120);
ALTER TABLE cliente_nautica ADD COLUMN IF NOT EXISTS ultima_pergunta TEXT;";

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.ExecuteAsync(sql);
                _colunasEnsured = true;
            }
            finally
            {
                ColunasLock.Release();
            }
        }

        public async Task<(IReadOnlyList<NauticaLeadListItemDto> Itens, int Total)> ListarLeadsAsync(
            Guid idEstabelecimento,
            string? busca,
            string? status,
            int pagina,
            int tamanhoPagina)
        {
            await EnsureColunasAsync();

            var paginaNormalizada = pagina < 1 ? 1 : pagina;
            var tamanhoNormalizado = tamanhoPagina < 1 ? 20 : tamanhoPagina;
            var offset = (paginaNormalizada - 1) * tamanhoNormalizado;

            var statusNormalizado = NormalizarStatus(LimparFiltro(status));

            const string sql = @"
WITH unified AS (
    SELECT cn.id,
           cn.id_conversa AS IdConversa,
           cn.id_cliente AS IdCliente,
           COALESCE(cn.telefone_e164, '') AS Telefone,
           cn.nome_cliente AS NomeCliente,
           cn.nome_empresa AS NomeEmpresa,
           cn.cnpj AS Cnpj,
           cn.etapa_atual AS EtapaAtual,
           cn.ultima_pergunta AS UltimaPergunta,
           COALESCE(cn.status, '') AS Status,
           FALSE AS CapturadoDurantePausa,
           COALESCE(cn.data_conclusao, cn.data_atualizacao, cn.data_criacao) AS Data
      FROM cliente_nautica cn
     WHERE cn.id_estabelecimento = @IdEstabelecimento

    UNION ALL

    SELECT c.id,
           c.id AS IdConversa,
           c.id_cliente AS IdCliente,
           COALESCE(cl.telefone_e164, '') AS Telefone,
           cl.nome AS NomeCliente,
           NULL::text AS NomeEmpresa,
           NULL::text AS Cnpj,
           NULL::text AS EtapaAtual,
           NULL::text AS UltimaPergunta,
           'pausado' AS Status,
           TRUE AS CapturadoDurantePausa,
           COALESCE(c.data_ultima_mensagem, c.data_ultima_entrada, c.data_atualizacao, c.data_criacao) AS Data
      FROM conversas c
      JOIN clientes cl ON cl.id = c.id_cliente
     WHERE c.id_estabelecimento = @IdEstabelecimento
       AND COALESCE(NULLIF(c.status_atendimento, ''), '') = 'empresa_pausada'
       AND NOT EXISTS (
            SELECT 1
              FROM cliente_nautica cn2
             WHERE cn2.id_estabelecimento = c.id_estabelecimento
               AND cn2.id_conversa = c.id
       )
),
base AS (
    SELECT *
      FROM unified
     WHERE (@Status IS NULL OR Status = @Status)
       AND (
            @Busca IS NULL OR @Busca = '' OR
            COALESCE(NomeCliente, '') ILIKE '%' || @Busca || '%' OR
            COALESCE(NomeEmpresa, '') ILIKE '%' || @Busca || '%' OR
            COALESCE(Telefone, '') ILIKE '%' || @Busca || '%' OR
            COALESCE(Cnpj, '') ILIKE '%' || @Busca || '%'
       )
),
tot AS (
    SELECT COUNT(*)::int AS total FROM base
)
SELECT b.id,
       b.Telefone,
       b.NomeCliente,
       b.NomeEmpresa,
       b.Cnpj,
       b.EtapaAtual,
       b.UltimaPergunta,
       b.Status,
       b.CapturadoDurantePausa,
       b.Data,
       (SELECT total FROM tot) AS TotalRegistros
  FROM base b
 ORDER BY b.Data DESC
 LIMIT @Limit OFFSET @Offset;";

            await using var connection = new NpgsqlConnection(_connectionString);
            var rows = (await connection.QueryAsync<NauticaLeadListRow>(sql, new
            {
                IdEstabelecimento = idEstabelecimento,
                Busca = LimparFiltro(busca),
                Status = statusNormalizado,
                Limit = tamanhoNormalizado,
                Offset = offset
            })).ToList();

            var total = rows.FirstOrDefault()?.TotalRegistros ?? 0;
            var itens = rows.Select(r => new NauticaLeadListItemDto
            {
                Id = r.Id,
                Telefone = r.Telefone,
                NomeCliente = r.NomeCliente,
                NomeEmpresa = r.NomeEmpresa,
                Cnpj = r.Cnpj,
                EtapaAtual = r.EtapaAtual,
                UltimaPergunta = r.UltimaPergunta,
                Status = r.Status,
                CapturadoDurantePausa = r.CapturadoDurantePausa,
                Data = r.Data
            }).ToList();

            return (itens, total);
        }

        public async Task<IReadOnlyList<NauticaLeadStatusCountDto>> ContarLeadsPorStatusAsync(
            Guid idEstabelecimento,
            string? busca)
        {
            await EnsureColunasAsync();

            const string sql = @"
WITH unified AS (
    SELECT COALESCE(cn.status, '') AS Status,
           cn.nome_cliente AS NomeCliente,
           cn.nome_empresa AS NomeEmpresa,
           cn.telefone_e164 AS Telefone,
           cn.cnpj AS Cnpj
      FROM cliente_nautica cn
     WHERE cn.id_estabelecimento = @IdEstabelecimento

    UNION ALL

    SELECT 'pausado' AS Status,
           cl.nome AS NomeCliente,
           NULL::text AS NomeEmpresa,
           cl.telefone_e164 AS Telefone,
           NULL::text AS Cnpj
      FROM conversas c
      JOIN clientes cl ON cl.id = c.id_cliente
     WHERE c.id_estabelecimento = @IdEstabelecimento
       AND COALESCE(NULLIF(c.status_atendimento, ''), '') = 'empresa_pausada'
       AND NOT EXISTS (
            SELECT 1
              FROM cliente_nautica cn2
             WHERE cn2.id_estabelecimento = c.id_estabelecimento
               AND cn2.id_conversa = c.id
       )
)
SELECT Status,
       COUNT(*)::int AS Total
  FROM unified
 WHERE (
        @Busca IS NULL OR @Busca = '' OR
        COALESCE(NomeCliente, '') ILIKE '%' || @Busca || '%' OR
        COALESCE(NomeEmpresa, '') ILIKE '%' || @Busca || '%' OR
        COALESCE(Telefone, '') ILIKE '%' || @Busca || '%' OR
        COALESCE(Cnpj, '') ILIKE '%' || @Busca || '%'
   )
 GROUP BY Status;";

            await using var connection = new NpgsqlConnection(_connectionString);
            var rows = await connection.QueryAsync<NauticaLeadStatusCountDto>(sql, new
            {
                IdEstabelecimento = idEstabelecimento,
                Busca = LimparFiltro(busca)
            });

            return rows.ToList();
        }

        public async Task<NauticaLeadDetailDto?> ObterLeadDetalheAsync(Guid idEstabelecimento, Guid idLead)
        {
            await EnsureColunasAsync();

            const string sql = @"
SELECT cn.id,
       cn.id_conversa          AS IdConversa,
       cn.id_cliente           AS IdCliente,
       COALESCE(cn.telefone_e164, '') AS Telefone,
       cn.nome_cliente         AS NomeCliente,
       cn.nome_empresa         AS NomeEmpresa,
       cn.cnpj                 AS Cnpj,
       cn.etapa_atual          AS EtapaAtual,
       cn.ultima_pergunta      AS UltimaPergunta,
       cn.tem_loja_fisica      AS TemLojaFisica,
       cn.consegue_minimo      AS ConsegueMinimo,
       cn.cidade_estado        AS CidadeEstado,
       COALESCE(cn.status, '') AS Status,
       cn.via_numero_central   AS ViaNumeroCentral,
       COALESCE(cn.data_conclusao, cn.data_atualizacao, cn.data_criacao) AS Data,
       cn.data_criacao         AS DataCriacao,
       cn.data_atualizacao     AS DataAtualizacao,
       cn.data_conclusao       AS DataConclusao
  FROM cliente_nautica cn
 WHERE cn.id_estabelecimento = @IdEstabelecimento
   AND cn.id = @IdLead
 LIMIT 1;";

            await using var connection = new NpgsqlConnection(_connectionString);
            var lead = await connection.QueryFirstOrDefaultAsync<NauticaLeadDetailDto>(sql, new
            {
                IdEstabelecimento = idEstabelecimento,
                IdLead = idLead
            });

            if (lead != null)
            {
                return lead;
            }

            const string pausedSql = @"
SELECT c.id,
       c.id AS IdConversa,
       c.id_cliente AS IdCliente,
       COALESCE(cl.telefone_e164, '') AS Telefone,
       cl.nome AS NomeCliente,
       NULL::text AS NomeEmpresa,
       NULL::text AS Cnpj,
       NULL::text AS EtapaAtual,
       NULL::text AS UltimaPergunta,
       NULL::boolean AS TemLojaFisica,
       NULL::boolean AS ConsegueMinimo,
       NULL::text AS CidadeEstado,
       'pausado' AS Status,
       FALSE AS ViaNumeroCentral,
       TRUE AS CapturadoDurantePausa,
       COALESCE(c.data_ultima_mensagem, c.data_ultima_entrada, c.data_atualizacao, c.data_criacao) AS Data,
       c.data_criacao AS DataCriacao,
       c.data_atualizacao AS DataAtualizacao,
       NULL::timestamp AS DataConclusao
  FROM conversas c
  JOIN clientes cl ON cl.id = c.id_cliente
 WHERE c.id_estabelecimento = @IdEstabelecimento
   AND c.id = @IdLead
   AND COALESCE(NULLIF(c.status_atendimento, ''), '') = 'empresa_pausada'
   AND NOT EXISTS (
        SELECT 1
          FROM cliente_nautica cn2
         WHERE cn2.id_estabelecimento = c.id_estabelecimento
           AND cn2.id_conversa = c.id
   )
 LIMIT 1;";

            return await connection.QueryFirstOrDefaultAsync<NauticaLeadDetailDto>(pausedSql, new
            {
                IdEstabelecimento = idEstabelecimento,
                IdLead = idLead
            });
        }

        public async Task<bool> AtualizarStatusLeadAsync(Guid idEstabelecimento, Guid idLead, string status)
        {
            await EnsureColunasAsync();

            // Normalizar alias legado antes de persistir
            var statusFinal = NormalizarStatus(status) ?? status;

            const string sql = @"
UPDATE cliente_nautica
   SET status = @Status,
       data_conclusao = CASE
            WHEN @Status IN ('lojista_qualificado', 'consumidor_final', 'lojista', 'cancelado') THEN COALESCE(data_conclusao, NOW())
            ELSE NULL
       END,
       data_atualizacao = NOW()
 WHERE id_estabelecimento = @IdEstabelecimento
   AND id = @IdLead;";

            await using var connection = new NpgsqlConnection(_connectionString);
            return await connection.ExecuteAsync(sql, new
            {
                IdEstabelecimento = idEstabelecimento,
                IdLead = idLead,
                Status = statusFinal
            }) > 0;
        }

        public async Task<PromoverContatoPausadoResponseDto?> PromoverContatoPausadoAsync(Guid idEstabelecimento, Guid idConversa)
        {
            await EnsureColunasAsync();

            const string selectSql = @"
SELECT c.id AS IdConversa,
       c.id_cliente AS IdCliente,
       c.id_estabelecimento AS IdEstabelecimento,
       COALESCE(cl.telefone_e164, '') AS Telefone,
       cl.nome AS NomeCliente,
       c.data_criacao AS DataCriacao
  FROM conversas c
  JOIN clientes cl ON cl.id = c.id_cliente
 WHERE c.id_estabelecimento = @IdEstabelecimento
   AND c.id = @IdConversa
   AND COALESCE(NULLIF(c.status_atendimento, ''), '') = 'empresa_pausada'
 LIMIT 1;";

            const string existingSql = @"
SELECT id
  FROM cliente_nautica
 WHERE id_estabelecimento = @IdEstabelecimento
   AND (id_conversa = @IdConversa OR (telefone_e164 = @Telefone AND status = 'incompleto'))
 ORDER BY CASE WHEN id_conversa = @IdConversa THEN 0 ELSE 1 END, data_criacao DESC
 LIMIT 1;";

            const string insertSql = @"
INSERT INTO cliente_nautica (
    id,
    id_estabelecimento,
    id_conversa,
    id_cliente,
    telefone_e164,
    nome_cliente,
    etapa_atual,
    ultima_pergunta,
    status,
    via_numero_central,
    data_conclusao,
    data_criacao,
    data_atualizacao
) VALUES (
    @Id,
    @IdEstabelecimento,
    @IdConversa,
    @IdCliente,
    @Telefone,
    @NomeCliente,
    NULL,
    NULL,
    'incompleto',
    FALSE,
    NULL,
    NOW(),
    NOW()
);";

            const string updateLeadSql = @"
UPDATE cliente_nautica
   SET id_conversa = @IdConversa,
       id_cliente = @IdCliente,
       telefone_e164 = @Telefone,
       nome_cliente = COALESCE(NULLIF(nome_cliente, ''), @NomeCliente),
       status = 'incompleto',
       data_conclusao = NULL,
       data_atualizacao = NOW()
 WHERE id = @IdLead;";

            const string updateConversationSql = @"
UPDATE conversas
   SET status_atendimento = 'em_andamento',
       motivo_fechamento = NULL,
       data_atualizacao = NOW()
 WHERE id_estabelecimento = @IdEstabelecimento
   AND id = @IdConversa;";

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            var row = await connection.QueryFirstOrDefaultAsync<ContatoPausadoRow>(
                selectSql,
                new { IdEstabelecimento = idEstabelecimento, IdConversa = idConversa },
                transaction);

            if (row == null || string.IsNullOrWhiteSpace(row.Telefone))
            {
                await transaction.RollbackAsync();
                return null;
            }

            var leadId = await connection.ExecuteScalarAsync<Guid?>(
                existingSql,
                new { IdEstabelecimento = idEstabelecimento, IdConversa = idConversa, row.Telefone },
                transaction);

            if (leadId.HasValue)
            {
                await connection.ExecuteAsync(
                    updateLeadSql,
                    new { IdLead = leadId.Value, row.IdConversa, row.IdCliente, row.Telefone, row.NomeCliente },
                    transaction);
            }
            else
            {
                leadId = Guid.NewGuid();
                await connection.ExecuteAsync(
                    insertSql,
                    new
                    {
                        Id = leadId.Value,
                        IdEstabelecimento = idEstabelecimento,
                        row.IdConversa,
                        row.IdCliente,
                        row.Telefone,
                        row.NomeCliente
                    },
                    transaction);
            }

            await connection.ExecuteAsync(
                updateConversationSql,
                new { IdEstabelecimento = idEstabelecimento, IdConversa = idConversa },
                transaction);

            await transaction.CommitAsync();

            return new PromoverContatoPausadoResponseDto
            {
                IdLead = leadId.Value,
                IdConversa = idConversa,
                Status = "incompleto"
            };
        }

        private static string? LimparFiltro(string? valor)
        {
            return string.IsNullOrWhiteSpace(valor) ? null : valor.Trim();
        }

        // Aceita lojista_minimo como alias legado, responde sempre com lojista_qualificado
        private static string? NormalizarStatus(string? status)
        {
            if (status == null) return null;
            return string.Equals(status, "lojista_minimo", StringComparison.OrdinalIgnoreCase)
                ? "lojista_qualificado"
                : status;
        }

        private class NauticaLeadListRow
        {
            public Guid Id { get; set; }
            public string Telefone { get; set; } = string.Empty;
            public string? NomeCliente { get; set; }
            public string? NomeEmpresa { get; set; }
            public string? Cnpj { get; set; }
            public string? EtapaAtual { get; set; }
            public string? UltimaPergunta { get; set; }
            public string Status { get; set; } = string.Empty;
            public bool CapturadoDurantePausa { get; set; }
            public DateTime Data { get; set; }
            public int TotalRegistros { get; set; }
        }

        private sealed class ContatoPausadoRow
        {
            public Guid IdConversa { get; set; }
            public Guid IdCliente { get; set; }
            public string Telefone { get; set; } = string.Empty;
            public string? NomeCliente { get; set; }
        }
    }
}
