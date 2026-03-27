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

            // Normalizar alias legado de status
            var statusNormalizado = NormalizarStatus(LimparFiltro(status));

            const string sql = @"
WITH base AS (
    SELECT cn.*
      FROM cliente_nautica cn
     WHERE cn.id_estabelecimento = @IdEstabelecimento
       AND (@Status IS NULL OR cn.status = @Status)
       AND (
            @Busca IS NULL OR @Busca = '' OR
            COALESCE(cn.nome_cliente, '') ILIKE '%' || @Busca || '%' OR
            COALESCE(cn.nome_empresa, '') ILIKE '%' || @Busca || '%' OR
            COALESCE(cn.telefone_e164, '') ILIKE '%' || @Busca || '%' OR
            COALESCE(cn.cnpj, '') ILIKE '%' || @Busca || '%' OR
            COALESCE(cn.cidade_estado, '') ILIKE '%' || @Busca || '%'
       )
),
tot AS (
    SELECT COUNT(*)::int AS total FROM base
)
SELECT b.id,
       COALESCE(b.telefone_e164, '') AS Telefone,
       b.nome_cliente AS NomeCliente,
       b.nome_empresa AS NomeEmpresa,
       b.cnpj AS Cnpj,
       b.etapa_atual AS EtapaAtual,
       b.ultima_pergunta AS UltimaPergunta,
       COALESCE(b.status, '') AS Status,
       COALESCE(b.data_conclusao, b.data_atualizacao, b.data_criacao) AS Data,
       (SELECT total FROM tot) AS TotalRegistros
  FROM base b
 ORDER BY COALESCE(b.data_conclusao, b.data_atualizacao, b.data_criacao) DESC
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
SELECT COALESCE(cn.status, '') AS Status,
       COUNT(*)::int AS Total
  FROM cliente_nautica cn
 WHERE cn.id_estabelecimento = @IdEstabelecimento
   AND (
        @Busca IS NULL OR @Busca = '' OR
        COALESCE(cn.nome_cliente, '') ILIKE '%' || @Busca || '%' OR
        COALESCE(cn.nome_empresa, '') ILIKE '%' || @Busca || '%' OR
        COALESCE(cn.telefone_e164, '') ILIKE '%' || @Busca || '%' OR
        COALESCE(cn.cnpj, '') ILIKE '%' || @Busca || '%' OR
        COALESCE(cn.cidade_estado, '') ILIKE '%' || @Busca || '%'
   )
 GROUP BY cn.status;";

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
            return await connection.QueryFirstOrDefaultAsync<NauticaLeadDetailDto>(sql, new
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
            public DateTime Data { get; set; }
            public int TotalRegistros { get; set; }
        }
    }
}
