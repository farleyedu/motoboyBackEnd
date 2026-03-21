using System;
using System.Collections.Generic;
using System.Linq;
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

        public SqlNauticaPainelRepository(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection")
                ?? configuration["ConnectionStrings:DefaultConnection"]
                ?? throw new InvalidOperationException("Connection string 'DefaultConnection' nao encontrada.");
        }

        public async Task<(IReadOnlyList<NauticaLeadListItemDto> Itens, int Total)> ListarLeadsAsync(
            Guid idEstabelecimento,
            string? busca,
            string? status,
            int pagina,
            int tamanhoPagina)
        {
            var paginaNormalizada = pagina < 1 ? 1 : pagina;
            var tamanhoNormalizado = tamanhoPagina < 1 ? 20 : tamanhoPagina;
            var offset = (paginaNormalizada - 1) * tamanhoNormalizado;

            const string sql = @"
WITH base AS (
    SELECT cn.*
      FROM cliente_nautica cn
     WHERE cn.id_estabelecimento = @IdEstabelecimento
       AND (@Status IS NULL OR cn.status = @Status)
       AND (
            @Busca IS NULL OR @Busca = '' OR
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
       b.nome_empresa AS NomeEmpresa,
       b.cnpj AS Cnpj,
       b.segmento AS Segmento,
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
                Status = LimparFiltro(status),
                Limit = tamanhoNormalizado,
                Offset = offset
            })).ToList();

            var total = rows.FirstOrDefault()?.TotalRegistros ?? 0;
            var itens = rows.Select(r => new NauticaLeadListItemDto
            {
                Id = r.Id,
                Telefone = r.Telefone,
                NomeEmpresa = r.NomeEmpresa,
                Cnpj = r.Cnpj,
                Segmento = r.Segmento,
                Status = r.Status,
                Data = r.Data
            }).ToList();

            return (itens, total);
        }

        public async Task<IReadOnlyList<NauticaLeadStatusCountDto>> ContarLeadsPorStatusAsync(
            Guid idEstabelecimento,
            string? busca)
        {
            const string sql = @"
SELECT COALESCE(cn.status, '') AS Status,
       COUNT(*)::int AS Total
  FROM cliente_nautica cn
 WHERE cn.id_estabelecimento = @IdEstabelecimento
   AND (
        @Busca IS NULL OR @Busca = '' OR
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
            const string sql = @"
SELECT cn.id,
       cn.id_conversa          AS IdConversa,
       cn.id_cliente           AS IdCliente,
       COALESCE(cn.telefone_e164, '') AS Telefone,
       cn.nome_empresa         AS NomeEmpresa,
       cn.cnpj                 AS Cnpj,
       cn.segmento             AS Segmento,
       cn.tem_loja_fisica      AS TemLojaFisica,
       cn.consegue_minimo      AS ConsegueMinimo,
       cn.cidade_estado        AS CidadeEstado,
       cn.historico_nautica    AS HistoricoNautica,
       cn.desafio_loja         AS DesafioLoja,
       cn.publico_alvo         AS PublicoAlvo,
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
            const string sql = @"
UPDATE cliente_nautica
   SET status = @Status,
       data_conclusao = CASE
            WHEN @Status IN ('lojista_minimo', 'consumidor_final', 'lojista') THEN COALESCE(data_conclusao, NOW())
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
                Status = status
            }) > 0;
        }

        private static string? LimparFiltro(string? valor)
        {
            return string.IsNullOrWhiteSpace(valor) ? null : valor.Trim();
        }

        private class NauticaLeadListRow
        {
            public Guid Id { get; set; }
            public string Telefone { get; set; } = string.Empty;
            public string? NomeEmpresa { get; set; }
            public string? Cnpj { get; set; }
            public string? Segmento { get; set; }
            public string Status { get; set; } = string.Empty;
            public DateTime Data { get; set; }
            public int TotalRegistros { get; set; }
        }
    }
}
