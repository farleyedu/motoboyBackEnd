using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using APIBack.Automation.Dtos;
using APIBack.Automation.Interfaces;
using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace APIBack.Automation.Infra
{
    public class SqlGaragemPainelRepository : IGaragemPainelRepository
    {
        private readonly string _connectionString;

        public SqlGaragemPainelRepository(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection")
                ?? configuration["ConnectionStrings:DefaultConnection"]
                ?? throw new InvalidOperationException("Connection string 'DefaultConnection' nao encontrada.");
        }

        public async Task<(IReadOnlyList<GarageLeadListItemDto> Itens, int Total)> ListarLeadsAsync(
            Guid idEstabelecimento,
            string? busca,
            string? status,
            string? objetivo,
            int pagina,
            int tamanhoPagina)
        {
            var paginaNormalizada = pagina < 1 ? 1 : pagina;
            var tamanhoNormalizado = tamanhoPagina < 1 ? 20 : tamanhoPagina;
            var offset = (paginaNormalizada - 1) * tamanhoNormalizado;

            const string sql = @"
WITH base AS (
    SELECT cg.*
      FROM cliente_garagem cg
     WHERE cg.id_estabelecimento = @IdEstabelecimento
       AND (@Status IS NULL OR cg.status = @Status)
       AND (@Objetivo IS NULL OR cg.objetivo = @Objetivo)
       AND (
            @Busca IS NULL OR @Busca = '' OR
            COALESCE(cg.nome_cliente, '') ILIKE '%' || @Busca || '%' OR
            COALESCE(cg.telefone_e164, '') ILIKE '%' || @Busca || '%' OR
            COALESCE(cg.modelo_interesse, '') ILIKE '%' || @Busca || '%' OR
            COALESCE(cg.troca_modelo_desejado, '') ILIKE '%' || @Busca || '%' OR
            COALESCE(cg.venda_modelo, '') ILIKE '%' || @Busca || '%'
       )
),
tot AS (
    SELECT COUNT(*)::int AS total FROM base
),
sim AS (
    SELECT id_lead, COUNT(*)::int AS total
      FROM cliente_garagem_simulacao
     GROUP BY id_lead
)
SELECT b.id,
       COALESCE(b.telefone_e164, '') AS Telefone,
       COALESCE(b.nome_cliente, '') AS Nome,
       b.cpf AS Cpf,
       COALESCE(b.objetivo, '') AS Objetivo,
       CASE
         WHEN b.objetivo = 'trocar' THEN COALESCE(b.troca_modelo_desejado, b.modelo_interesse, '')
         WHEN b.objetivo = 'vender' THEN COALESCE(b.venda_modelo, '')
         ELSE COALESCE(b.modelo_interesse, '')
       END AS Modelo,
       b.modelo_interesse AS ModeloInteresse,
       b.troca_modelo_desejado AS TrocaModeloDesejado,
       b.venda_modelo AS VendaModelo,
       b.faixa_investimento AS Faixa,
       b.forma_pagamento AS Pagamento,
       b.valor_entrada_texto AS Entrada,
       b.urgencia AS Urgencia,
       COALESCE(b.status, '') AS Status,
       COALESCE(b.data_conclusao, b.data_atualizacao, b.data_criacao) AS Data,
       COALESCE(sim.total, 0) AS SimulacoesCount,
       (SELECT total FROM tot) AS TotalRegistros
  FROM base b
  LEFT JOIN sim ON sim.id_lead = b.id
 ORDER BY COALESCE(b.data_conclusao, b.data_atualizacao, b.data_criacao) DESC
 LIMIT @Limit OFFSET @Offset;";

            await using var connection = new NpgsqlConnection(_connectionString);
            var rows = (await connection.QueryAsync<GarageLeadListRow>(sql, new
            {
                IdEstabelecimento = idEstabelecimento,
                Busca = LimparFiltro(busca),
                Status = LimparFiltro(status),
                Objetivo = LimparFiltro(objetivo),
                Limit = tamanhoNormalizado,
                Offset = offset
            })).ToList();

            var total = rows.FirstOrDefault()?.TotalRegistros ?? 0;
            var itens = rows.Select(row =>
            {
                var carroInteresse = ResolverCarroInteresse(
                    row.Objetivo,
                    row.ModeloInteresse,
                    row.TrocaModeloDesejado,
                    row.VendaModelo);

                return new GarageLeadListItemDto
                {
                    Id = row.Id,
                    Telefone = row.Telefone,
                    Nome = row.Nome,
                    Cpf = LimparFiltro(row.Cpf),
                    Objetivo = row.Objetivo,
                    Modelo = string.IsNullOrWhiteSpace(row.Modelo) ? (carroInteresse ?? string.Empty) : row.Modelo,
                    CarroInteresse = carroInteresse,
                    Faixa = row.Faixa,
                    Pagamento = row.Pagamento,
                    Entrada = row.Entrada,
                    Urgencia = row.Urgencia,
                    Status = row.Status,
                    Data = row.Data,
                    SimulacoesCount = row.SimulacoesCount
                };
            }).ToList();

            return (itens, total);
        }

        public async Task<IReadOnlyList<GarageLeadStatusCountDto>> ContarLeadsPorStatusAsync(
            Guid idEstabelecimento,
            string? busca,
            string? objetivo)
        {
            const string sql = @"
SELECT COALESCE(cg.status, '') AS Status,
       COUNT(*)::int AS Total
  FROM cliente_garagem cg
 WHERE cg.id_estabelecimento = @IdEstabelecimento
   AND (@Objetivo IS NULL OR cg.objetivo = @Objetivo)
   AND (
        @Busca IS NULL OR @Busca = '' OR
        COALESCE(cg.nome_cliente, '') ILIKE '%' || @Busca || '%' OR
        COALESCE(cg.telefone_e164, '') ILIKE '%' || @Busca || '%' OR
        COALESCE(cg.modelo_interesse, '') ILIKE '%' || @Busca || '%' OR
        COALESCE(cg.troca_modelo_desejado, '') ILIKE '%' || @Busca || '%' OR
        COALESCE(cg.venda_modelo, '') ILIKE '%' || @Busca || '%'
   )
 GROUP BY cg.status;";

            await using var connection = new NpgsqlConnection(_connectionString);
            var rows = await connection.QueryAsync<GarageLeadStatusCountDto>(sql, new
            {
                IdEstabelecimento = idEstabelecimento,
                Busca = LimparFiltro(busca),
                Objetivo = LimparFiltro(objetivo)
            });

            return rows.ToList();
        }

        public async Task<GarageLeadDetailDto?> ObterLeadDetalheAsync(Guid idEstabelecimento, Guid idLead)
        {
            const string sqlLead = @"
SELECT cg.id,
       cg.id_conversa AS IdConversa,
       cg.id_cliente AS IdCliente,
       COALESCE(cg.telefone_e164, '') AS Telefone,
       COALESCE(cg.nome_cliente, '') AS Nome,
       cg.cpf AS Cpf,
       COALESCE(cg.objetivo, '') AS Objetivo,
       CASE
         WHEN cg.objetivo = 'trocar' THEN COALESCE(cg.troca_modelo_desejado, cg.modelo_interesse, '')
         WHEN cg.objetivo = 'vender' THEN COALESCE(cg.venda_modelo, '')
         ELSE COALESCE(cg.modelo_interesse, '')
       END AS Modelo,
       cg.faixa_investimento AS Faixa,
       cg.forma_pagamento AS Pagamento,
       cg.valor_entrada_texto AS Entrada,
       cg.urgencia AS Urgencia,
       COALESCE(cg.status, '') AS Status,
       COALESCE(cg.data_conclusao, cg.data_atualizacao, cg.data_criacao) AS Data,
       cg.via_numero_central AS ViaNumeroCentral,
       cg.data_criacao AS DataCriacao,
       cg.data_atualizacao AS DataAtualizacao,
       cg.data_conclusao AS DataConclusao,
       cg.modelo_interesse AS ModeloInteresse,
       cg.faixa_investimento AS FaixaInvestimento,
       cg.forma_pagamento AS FormaPagamento,
       cg.valor_entrada_texto AS ValorEntradaTexto,
       cg.troca_modelo_atual AS TrocaModeloAtual,
       cg.troca_ano_modelo AS TrocaAnoModelo,
       cg.troca_km AS TrocaKm,
       cg.troca_quitado AS TrocaQuitado,
       cg.troca_modelo_desejado AS TrocaModeloDesejado,
       cg.troca_condicao AS TrocaCondicao,
       cg.venda_modelo AS VendaModelo,
       cg.venda_ano AS VendaAno,
       cg.venda_km AS VendaKm,
       cg.venda_quitado AS VendaQuitado
  FROM cliente_garagem cg
 WHERE cg.id_estabelecimento = @IdEstabelecimento
   AND cg.id = @IdLead
 LIMIT 1;";

            const string sqlSimulacoes = @"
SELECT s.id,
       s.titulo,
       s.status,
       s.comentario,
       s.valor,
       s.arquivos_json::text AS ArquivosJson,
       s.data_criacao AS Criado,
       s.data_atualizacao AS Atualizado
  FROM cliente_garagem_simulacao s
 WHERE s.id_lead = @IdLead
 ORDER BY s.data_criacao DESC;";

            await using var connection = new NpgsqlConnection(_connectionString);
            var detalhe = await connection.QueryFirstOrDefaultAsync<GarageLeadDetailDto>(sqlLead, new
            {
                IdEstabelecimento = idEstabelecimento,
                IdLead = idLead
            });

            if (detalhe == null)
            {
                return null;
            }

            detalhe.Cpf = LimparFiltro(detalhe.Cpf);
            detalhe.CarroInteresse = ResolverCarroInteresse(
                detalhe.Objetivo,
                detalhe.ModeloInteresse,
                detalhe.TrocaModeloDesejado,
                detalhe.VendaModelo);
            var simulacoes = await connection.QueryAsync<GarageLeadSimulationRow>(sqlSimulacoes, new { IdLead = idLead });
            detalhe.Simulacoes = simulacoes.Select(MapearSimulacao).ToList();
            detalhe.SimulacoesCount = detalhe.Simulacoes.Count;
            return detalhe;
        }

        public async Task<bool> AtualizarStatusLeadAsync(Guid idEstabelecimento, Guid idLead, string status)
        {
            const string sql = @"
UPDATE cliente_garagem
   SET status = @Status,
       data_conclusao = CASE
            WHEN @Status = 'concluido' THEN COALESCE(data_conclusao, NOW())
            WHEN @Status <> 'concluido' THEN NULL
            ELSE data_conclusao
       END,
       data_atualizacao = NOW()
 WHERE id_estabelecimento = @IdEstabelecimento
   AND id = @IdLead;";

            await using var connection = new NpgsqlConnection(_connectionString);
            var linhas = await connection.ExecuteAsync(sql, new
            {
                IdEstabelecimento = idEstabelecimento,
                IdLead = idLead,
                Status = status
            });

            return linhas > 0;
        }

        public async Task<GarageLeadSimulationDto?> CriarSimulacaoAsync(
            Guid idEstabelecimento,
            Guid idLead,
            CreateGarageLeadSimulationRequest request)
        {
            if (!await LeadPertenceAoEstabelecimentoAsync(idEstabelecimento, idLead))
            {
                return null;
            }

            const string sql = @"
INSERT INTO cliente_garagem_simulacao (
    id,
    id_lead,
    titulo,
    status,
    comentario,
    valor,
    arquivos_json,
    data_criacao,
    data_atualizacao
) VALUES (
    @Id,
    @IdLead,
    @Titulo,
    @Status,
    @Comentario,
    @Valor,
    CAST(@ArquivosJson AS jsonb),
    NOW(),
    NOW()
)
RETURNING id,
          titulo,
          status,
          comentario,
          valor,
          arquivos_json::text AS ArquivosJson,
          data_criacao AS Criado,
          data_atualizacao AS Atualizado;";

            await using var connection = new NpgsqlConnection(_connectionString);
            var row = await connection.QueryFirstOrDefaultAsync<GarageLeadSimulationRow>(sql, new
            {
                Id = Guid.NewGuid(),
                IdLead = idLead,
                Titulo = request.Titulo.Trim(),
                Status = string.IsNullOrWhiteSpace(request.Status) ? "rascunho" : request.Status.Trim(),
                Comentario = string.IsNullOrWhiteSpace(request.Comentario) ? null : request.Comentario.Trim(),
                Valor = string.IsNullOrWhiteSpace(request.Valor) ? null : request.Valor.Trim(),
                ArquivosJson = SerializeArquivos(request.Arquivos)
            });

            return row == null ? null : MapearSimulacao(row);
        }

        public async Task<GarageLeadSimulationDto?> AtualizarSimulacaoAsync(
            Guid idEstabelecimento,
            Guid idLead,
            Guid idSimulacao,
            UpdateGarageLeadSimulationRequest request)
        {
            const string sql = @"
UPDATE cliente_garagem_simulacao s
   SET titulo = COALESCE(@Titulo, s.titulo),
       status = COALESCE(@Status, s.status),
       comentario = COALESCE(@Comentario, s.comentario),
       valor = COALESCE(@Valor, s.valor),
       arquivos_json = COALESCE(CAST(@ArquivosJson AS jsonb), s.arquivos_json),
       data_atualizacao = NOW()
  FROM cliente_garagem l
 WHERE s.id = @IdSimulacao
   AND s.id_lead = l.id
   AND l.id = @IdLead
   AND l.id_estabelecimento = @IdEstabelecimento
RETURNING s.id,
          s.titulo,
          s.status,
          s.comentario,
          s.valor,
          s.arquivos_json::text AS ArquivosJson,
          s.data_criacao AS Criado,
          s.data_atualizacao AS Atualizado;";

            await using var connection = new NpgsqlConnection(_connectionString);
            var row = await connection.QueryFirstOrDefaultAsync<GarageLeadSimulationRow>(sql, new
            {
                IdEstabelecimento = idEstabelecimento,
                IdLead = idLead,
                IdSimulacao = idSimulacao,
                Titulo = string.IsNullOrWhiteSpace(request.Titulo) ? null : request.Titulo.Trim(),
                Status = string.IsNullOrWhiteSpace(request.Status) ? null : request.Status.Trim(),
                Comentario = request.Comentario == null ? null : request.Comentario.Trim(),
                Valor = request.Valor == null ? null : request.Valor.Trim(),
                ArquivosJson = request.Arquivos == null ? null : SerializeArquivos(request.Arquivos)
            });

            return row == null ? null : MapearSimulacao(row);
        }

        public async Task<bool> RemoverSimulacaoAsync(Guid idEstabelecimento, Guid idLead, Guid idSimulacao)
        {
            const string sql = @"
DELETE FROM cliente_garagem_simulacao s
 USING cliente_garagem l
 WHERE s.id = @IdSimulacao
   AND s.id_lead = l.id
   AND l.id = @IdLead
   AND l.id_estabelecimento = @IdEstabelecimento;";

            await using var connection = new NpgsqlConnection(_connectionString);
            var linhas = await connection.ExecuteAsync(sql, new
            {
                IdEstabelecimento = idEstabelecimento,
                IdLead = idLead,
                IdSimulacao = idSimulacao
            });

            return linhas > 0;
        }

        private async Task<bool> LeadPertenceAoEstabelecimentoAsync(Guid idEstabelecimento, Guid idLead)
        {
            const string sql = @"
SELECT 1
  FROM cliente_garagem
 WHERE id_estabelecimento = @IdEstabelecimento
   AND id = @IdLead
 LIMIT 1;";

            await using var connection = new NpgsqlConnection(_connectionString);
            var encontrado = await connection.ExecuteScalarAsync<int?>(sql, new
            {
                IdEstabelecimento = idEstabelecimento,
                IdLead = idLead
            });

            return encontrado.HasValue;
        }

        private static string? LimparFiltro(string? valor)
        {
            if (string.IsNullOrWhiteSpace(valor))
            {
                return null;
            }

            return valor.Trim();
        }

        internal static string? ResolverCarroInteresse(
            string? objetivo,
            string? modeloInteresse,
            string? trocaModeloDesejado,
            string? vendaModelo)
        {
            var objetivoNormalizado = LimparFiltro(objetivo)?.ToLowerInvariant();

            return objetivoNormalizado switch
            {
                "trocar" => LimparFiltro(trocaModeloDesejado) ?? LimparFiltro(modeloInteresse),
                "vender" => LimparFiltro(vendaModelo),
                _ => LimparFiltro(modeloInteresse)
            };
        }

        private static string SerializeArquivos(IReadOnlyCollection<GarageLeadSimulationFileDto>? arquivos)
        {
            var lista = arquivos?.ToList() ?? new List<GarageLeadSimulationFileDto>();
            return JsonSerializer.Serialize(lista);
        }

        private static List<GarageLeadSimulationFileDto> DeserializeArquivos(string? arquivosJson)
        {
            if (string.IsNullOrWhiteSpace(arquivosJson))
            {
                return new List<GarageLeadSimulationFileDto>();
            }

            try
            {
                return JsonSerializer.Deserialize<List<GarageLeadSimulationFileDto>>(arquivosJson) ?? new List<GarageLeadSimulationFileDto>();
            }
            catch
            {
                return new List<GarageLeadSimulationFileDto>();
            }
        }

        private static GarageLeadSimulationDto MapearSimulacao(GarageLeadSimulationRow row)
        {
            return new GarageLeadSimulationDto
            {
                Id = row.Id,
                Titulo = row.Titulo ?? string.Empty,
                Status = row.Status ?? string.Empty,
                Comentario = row.Comentario,
                Valor = row.Valor,
                Criado = row.Criado,
                Atualizado = row.Atualizado,
                Arquivos = DeserializeArquivos(row.ArquivosJson)
            };
        }

        private sealed class GarageLeadListRow
        {
            public Guid Id { get; set; }
            public string Telefone { get; set; } = string.Empty;
            public string Nome { get; set; } = string.Empty;
            public string? Cpf { get; set; }
            public string Objetivo { get; set; } = string.Empty;
            public string Modelo { get; set; } = string.Empty;
            public string? ModeloInteresse { get; set; }
            public string? TrocaModeloDesejado { get; set; }
            public string? VendaModelo { get; set; }
            public string? Faixa { get; set; }
            public string? Pagamento { get; set; }
            public string? Entrada { get; set; }
            public string? Urgencia { get; set; }
            public string Status { get; set; } = string.Empty;
            public DateTime Data { get; set; }
            public int SimulacoesCount { get; set; }
            public int TotalRegistros { get; set; }
        }

        private sealed class GarageLeadSimulationRow
        {
            public Guid Id { get; set; }
            public string? Titulo { get; set; }
            public string? Status { get; set; }
            public string? Comentario { get; set; }
            public string? Valor { get; set; }
            public string? ArquivosJson { get; set; }
            public DateTime Criado { get; set; }
            public DateTime Atualizado { get; set; }
        }
    }
}
