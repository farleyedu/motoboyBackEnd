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

        public async Task<(IReadOnlyList<GarageLeadListItemDto> Itens, int Total)> ListarLeadsAsync(Guid idEstabelecimento, string? busca, string? status, string? objetivo, int pagina, int tamanhoPagina)
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
            var itens = rows.Select(MapListItem).ToList();
            return (itens, total);
        }

        public async Task<IReadOnlyList<GarageLeadStatusCountDto>> ContarLeadsPorStatusAsync(Guid idEstabelecimento, string? busca, string? objetivo)
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

            await using var connection = new NpgsqlConnection(_connectionString);
            var detalhe = await connection.QueryFirstOrDefaultAsync<GarageLeadDetailDto>(sqlLead, new { IdEstabelecimento = idEstabelecimento, IdLead = idLead });
            if (detalhe == null)
            {
                return null;
            }

            detalhe.Cpf = LimparFiltro(detalhe.Cpf);
            detalhe.CarroInteresse = ResolverCarroInteresse(detalhe.Objetivo, detalhe.ModeloInteresse, detalhe.TrocaModeloDesejado, detalhe.VendaModelo);
            detalhe.Simulacoes = (await ObterSimulacoesAsync(connection, idLead)).ToList();
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
            return await connection.ExecuteAsync(sql, new { IdEstabelecimento = idEstabelecimento, IdLead = idLead, Status = status }) > 0;
        }

        public async Task<GarageLeadSimulationDto?> CriarSimulacaoAsync(Guid idEstabelecimento, Guid idLead, CreateGarageLeadSimulationRequest request)
        {
            if (!await LeadPertenceAoEstabelecimentoAsync(idEstabelecimento, idLead))
            {
                return null;
            }

            const string sql = @"
INSERT INTO cliente_garagem_simulacao (
    id, id_lead, titulo, status, tipo_simulacao, comentario, valor, veiculo_marca,
    veiculo_modelo, veiculo_versao, veiculo_ano, veiculo_km, veiculo_valor, entrada_valor,
    saldo_financiado, parcelas_quantidade, parcela_valor, taxa_juros_mensal, observacoes,
    validade_em, criado_por_usuario_id, arquivos_json, data_criacao, data_atualizacao
)
VALUES (
    @Id, @IdLead, @Titulo, @Status, @TipoSimulacao, @Comentario, @Valor, @VeiculoMarca,
    @VeiculoModelo, @VeiculoVersao, @VeiculoAno, @VeiculoKm, @VeiculoValor, @EntradaValor,
    @SaldoFinanciado, @ParcelasQuantidade, @ParcelaValor, @TaxaJurosMensal, @Observacoes,
    @ValidadeEm, @CriadoPorUsuarioId, CAST(@ArquivosJson AS jsonb), NOW(), NOW()
);";

            var id = Guid.NewGuid();
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.ExecuteAsync(sql, BuildSimulationParameters(idLead, id, request, isCreate: true));
            return await ObterSimulacaoAsync(idEstabelecimento, idLead, id);
        }

        public async Task<GarageLeadSimulationDto?> AtualizarSimulacaoAsync(Guid idEstabelecimento, Guid idLead, Guid idSimulacao, UpdateGarageLeadSimulationRequest request)
        {
            const string sql = @"
UPDATE cliente_garagem_simulacao s
   SET titulo = COALESCE(@Titulo, s.titulo),
       status = COALESCE(@Status, s.status),
       tipo_simulacao = COALESCE(@TipoSimulacao, s.tipo_simulacao),
       comentario = COALESCE(@Comentario, s.comentario),
       valor = COALESCE(@Valor, s.valor),
       veiculo_marca = COALESCE(@VeiculoMarca, s.veiculo_marca),
       veiculo_modelo = COALESCE(@VeiculoModelo, s.veiculo_modelo),
       veiculo_versao = COALESCE(@VeiculoVersao, s.veiculo_versao),
       veiculo_ano = COALESCE(@VeiculoAno, s.veiculo_ano),
       veiculo_km = COALESCE(@VeiculoKm, s.veiculo_km),
       veiculo_valor = COALESCE(@VeiculoValor, s.veiculo_valor),
       entrada_valor = COALESCE(@EntradaValor, s.entrada_valor),
       saldo_financiado = COALESCE(@SaldoFinanciado, s.saldo_financiado),
       parcelas_quantidade = COALESCE(@ParcelasQuantidade, s.parcelas_quantidade),
       parcela_valor = COALESCE(@ParcelaValor, s.parcela_valor),
       taxa_juros_mensal = COALESCE(@TaxaJurosMensal, s.taxa_juros_mensal),
       observacoes = COALESCE(@Observacoes, s.observacoes),
       validade_em = COALESCE(@ValidadeEm, s.validade_em),
       arquivos_json = COALESCE(CAST(@ArquivosJson AS jsonb), s.arquivos_json),
       data_atualizacao = NOW()
  FROM cliente_garagem l
 WHERE s.id = @IdSimulacao
   AND s.id_lead = l.id
   AND l.id = @IdLead
   AND l.id_estabelecimento = @IdEstabelecimento;";

            await using var connection = new NpgsqlConnection(_connectionString);
            var linhas = await connection.ExecuteAsync(sql, BuildSimulationParameters(idLead, idSimulacao, request, isCreate: false, idEstabelecimento));
            return linhas == 0 ? null : await ObterSimulacaoAsync(idEstabelecimento, idLead, idSimulacao);
        }

        public async Task<bool> RemoverSimulacaoAsync(Guid idEstabelecimento, Guid idLead, Guid idSimulacao)
        {
            const string sql = @"
DELETE FROM cliente_garagem_simulacao_arquivo a
 WHERE a.id_simulacao = @IdSimulacao;

DELETE FROM cliente_garagem_simulacao s
 USING cliente_garagem l
 WHERE s.id = @IdSimulacao
   AND s.id_lead = l.id
   AND l.id = @IdLead
   AND l.id_estabelecimento = @IdEstabelecimento;";

            await using var connection = new NpgsqlConnection(_connectionString);
            return await connection.ExecuteAsync(sql, new { IdEstabelecimento = idEstabelecimento, IdLead = idLead, IdSimulacao = idSimulacao }) > 0;
        }

        public async Task<GarageLeadSimulationDto?> ObterSimulacaoAsync(Guid idEstabelecimento, Guid idLead, Guid idSimulacao)
        {
            const string sql = @"
SELECT s.id,
       s.titulo,
       s.status,
       s.tipo_simulacao AS TipoSimulacao,
       s.comentario,
       s.valor,
       s.veiculo_marca AS VeiculoMarca,
       s.veiculo_modelo AS VeiculoModelo,
       s.veiculo_versao AS VeiculoVersao,
       s.veiculo_ano AS VeiculoAno,
       s.veiculo_km AS VeiculoKm,
       s.veiculo_valor AS VeiculoValor,
       s.entrada_valor AS EntradaValor,
       s.saldo_financiado AS SaldoFinanciado,
       s.parcelas_quantidade AS ParcelasQuantidade,
       s.parcela_valor AS ParcelaValor,
       s.taxa_juros_mensal AS TaxaJurosMensal,
       s.observacoes AS Observacoes,
       s.validade_em AS ValidadeEm,
       s.criado_por_usuario_id AS CriadoPorUsuarioId,
       s.arquivos_json::text AS ArquivosJson,
       s.data_criacao AS Criado,
       s.data_atualizacao AS Atualizado
  FROM cliente_garagem_simulacao s
  JOIN cliente_garagem l ON l.id = s.id_lead
 WHERE l.id_estabelecimento = @IdEstabelecimento
   AND l.id = @IdLead
   AND s.id = @IdSimulacao
 LIMIT 1;";

            await using var connection = new NpgsqlConnection(_connectionString);
            var row = await connection.QueryFirstOrDefaultAsync<GarageLeadSimulationRow>(sql, new { IdEstabelecimento = idEstabelecimento, IdLead = idLead, IdSimulacao = idSimulacao });
            return row == null ? null : await MapearSimulacaoAsync(connection, row);
        }

        public async Task<GarageLeadSimulationFileDto?> AdicionarArquivoSimulacaoAsync(Guid idEstabelecimento, Guid idLead, Guid idSimulacao, GarageLeadSimulationFileDto arquivo)
        {
            if (!await LeadPertenceAoEstabelecimentoAsync(idEstabelecimento, idLead))
            {
                return null;
            }

            const string sql = @"
INSERT INTO cliente_garagem_simulacao_arquivo (
    id, id_simulacao, nome_original, nome_armazenado, caminho_relativo, url_publica, content_type, tamanho, data_criacao
)
SELECT @Id, @IdSimulacao, @NomeOriginal, @NomeArmazenado, @CaminhoRelativo, @UrlPublica, @ContentType, @Tamanho, @DataCriacao
 WHERE EXISTS (
    SELECT 1
      FROM cliente_garagem_simulacao s
      JOIN cliente_garagem l ON l.id = s.id_lead
     WHERE s.id = @IdSimulacao
       AND l.id = @IdLead
       AND l.id_estabelecimento = @IdEstabelecimento
 );";

            var idArquivo = arquivo.Id == Guid.Empty ? Guid.NewGuid() : arquivo.Id;
            await using var connection = new NpgsqlConnection(_connectionString);
            var linhas = await connection.ExecuteAsync(sql, new
            {
                Id = idArquivo,
                IdSimulacao = idSimulacao,
                IdLead = idLead,
                IdEstabelecimento = idEstabelecimento,
                NomeOriginal = arquivo.Nome,
                NomeArmazenado = ExtractStoredName(arquivo.CaminhoRelativo, arquivo.Nome),
                CaminhoRelativo = arquivo.CaminhoRelativo,
                UrlPublica = arquivo.Url,
                ContentType = arquivo.ContentType,
                Tamanho = arquivo.Tamanho,
                DataCriacao = arquivo.Data ?? DateTime.UtcNow
            });

            if (linhas == 0)
            {
                return null;
            }

            return new GarageLeadSimulationFileDto
            {
                Id = idArquivo,
                Nome = arquivo.Nome,
                Tamanho = arquivo.Tamanho,
                ContentType = arquivo.ContentType,
                CaminhoRelativo = arquivo.CaminhoRelativo,
                Url = arquivo.Url,
                Data = arquivo.Data
            };
        }

        public async Task<bool> RemoverArquivoSimulacaoAsync(Guid idEstabelecimento, Guid idLead, Guid idSimulacao, Guid idArquivo)
        {
            const string sql = @"
DELETE FROM cliente_garagem_simulacao_arquivo a
 USING cliente_garagem_simulacao s, cliente_garagem l
 WHERE a.id = @IdArquivo
   AND a.id_simulacao = s.id
   AND s.id = @IdSimulacao
   AND s.id_lead = l.id
   AND l.id = @IdLead
   AND l.id_estabelecimento = @IdEstabelecimento;";

            await using var connection = new NpgsqlConnection(_connectionString);
            return await connection.ExecuteAsync(sql, new { IdArquivo = idArquivo, IdSimulacao = idSimulacao, IdLead = idLead, IdEstabelecimento = idEstabelecimento }) > 0;
        }

        private async Task<bool> LeadPertenceAoEstabelecimentoAsync(Guid idEstabelecimento, Guid idLead)
        {
            const string sql = @"SELECT 1 FROM cliente_garagem WHERE id_estabelecimento = @IdEstabelecimento AND id = @IdLead LIMIT 1;";
            await using var connection = new NpgsqlConnection(_connectionString);
            return (await connection.ExecuteScalarAsync<int?>(sql, new { IdEstabelecimento = idEstabelecimento, IdLead = idLead })).HasValue;
        }

        private async Task<IReadOnlyList<GarageLeadSimulationDto>> ObterSimulacoesAsync(NpgsqlConnection connection, Guid idLead)
        {
            const string sql = @"
SELECT s.id,
       s.titulo,
       s.status,
       s.tipo_simulacao AS TipoSimulacao,
       s.comentario,
       s.valor,
       s.veiculo_marca AS VeiculoMarca,
       s.veiculo_modelo AS VeiculoModelo,
       s.veiculo_versao AS VeiculoVersao,
       s.veiculo_ano AS VeiculoAno,
       s.veiculo_km AS VeiculoKm,
       s.veiculo_valor AS VeiculoValor,
       s.entrada_valor AS EntradaValor,
       s.saldo_financiado AS SaldoFinanciado,
       s.parcelas_quantidade AS ParcelasQuantidade,
       s.parcela_valor AS ParcelaValor,
       s.taxa_juros_mensal AS TaxaJurosMensal,
       s.observacoes AS Observacoes,
       s.validade_em AS ValidadeEm,
       s.criado_por_usuario_id AS CriadoPorUsuarioId,
       s.arquivos_json::text AS ArquivosJson,
       s.data_criacao AS Criado,
       s.data_atualizacao AS Atualizado
  FROM cliente_garagem_simulacao s
 WHERE s.id_lead = @IdLead
 ORDER BY s.data_criacao DESC;";

            var rows = (await connection.QueryAsync<GarageLeadSimulationRow>(sql, new { IdLead = idLead })).ToList();
            var simulacoes = new List<GarageLeadSimulationDto>(rows.Count);
            foreach (var row in rows)
            {
                simulacoes.Add(await MapearSimulacaoAsync(connection, row));
            }

            return simulacoes;
        }

        private async Task<GarageLeadSimulationDto> MapearSimulacaoAsync(NpgsqlConnection connection, GarageLeadSimulationRow row)
        {
            var arquivos = (await ObterArquivosRelacionaisAsync(connection, row.Id)).ToList();
            if (arquivos.Count == 0)
            {
                arquivos = DeserializeArquivos(row.ArquivosJson);
            }

            return new GarageLeadSimulationDto
            {
                Id = row.Id,
                Titulo = row.Titulo ?? string.Empty,
                Status = row.Status ?? string.Empty,
                TipoSimulacao = row.TipoSimulacao,
                Comentario = row.Comentario,
                Valor = row.Valor,
                VeiculoMarca = row.VeiculoMarca,
                VeiculoModelo = row.VeiculoModelo,
                VeiculoVersao = row.VeiculoVersao,
                VeiculoAno = row.VeiculoAno,
                VeiculoKm = row.VeiculoKm,
                VeiculoValor = row.VeiculoValor,
                EntradaValor = row.EntradaValor,
                SaldoFinanciado = row.SaldoFinanciado,
                ParcelasQuantidade = row.ParcelasQuantidade,
                ParcelaValor = row.ParcelaValor,
                TaxaJurosMensal = row.TaxaJurosMensal,
                Observacoes = row.Observacoes,
                ValidadeEm = row.ValidadeEm,
                CriadoPorUsuarioId = row.CriadoPorUsuarioId,
                Criado = row.Criado,
                Atualizado = row.Atualizado,
                Arquivos = arquivos
            };
        }

        private Task<IEnumerable<GarageLeadSimulationFileDto>> ObterArquivosRelacionaisAsync(NpgsqlConnection connection, Guid idSimulacao)
            => connection.QueryAsync<GarageLeadSimulationFileDto>(@"
SELECT id AS Id,
       nome_original AS Nome,
       tamanho AS Tamanho,
       content_type AS ContentType,
       caminho_relativo AS CaminhoRelativo,
       url_publica AS Url,
       data_criacao AS Data
  FROM cliente_garagem_simulacao_arquivo
 WHERE id_simulacao = @IdSimulacao
 ORDER BY data_criacao ASC;", new { IdSimulacao = idSimulacao });

        private static object BuildSimulationParameters(Guid idLead, Guid idSimulacao, UpsertGarageLeadSimulationRequest request, bool isCreate, Guid? idEstabelecimento = null)
            => new
            {
                IdEstabelecimento = idEstabelecimento,
                IdLead = idLead,
                IdSimulacao = idSimulacao,
                Id = idSimulacao,
                Titulo = string.IsNullOrWhiteSpace(request.Titulo) ? null : request.Titulo.Trim(),
                Status = string.IsNullOrWhiteSpace(request.Status) ? (isCreate ? "rascunho" : null) : request.Status.Trim().ToLowerInvariant(),
                TipoSimulacao = string.IsNullOrWhiteSpace(request.TipoSimulacao) ? null : request.TipoSimulacao.Trim(),
                Comentario = request.Comentario == null ? null : request.Comentario.Trim(),
                Valor = request.Valor == null ? null : request.Valor.Trim(),
                VeiculoMarca = string.IsNullOrWhiteSpace(request.VeiculoMarca) ? null : request.VeiculoMarca.Trim(),
                VeiculoModelo = string.IsNullOrWhiteSpace(request.VeiculoModelo) ? null : request.VeiculoModelo.Trim(),
                VeiculoVersao = string.IsNullOrWhiteSpace(request.VeiculoVersao) ? null : request.VeiculoVersao.Trim(),
                VeiculoAno = request.VeiculoAno,
                VeiculoKm = request.VeiculoKm,
                VeiculoValor = request.VeiculoValor,
                EntradaValor = request.EntradaValor,
                SaldoFinanciado = request.SaldoFinanciado,
                ParcelasQuantidade = request.ParcelasQuantidade,
                ParcelaValor = request.ParcelaValor,
                TaxaJurosMensal = request.TaxaJurosMensal,
                Observacoes = request.Observacoes == null ? null : request.Observacoes.Trim(),
                ValidadeEm = request.ValidadeEm,
                CriadoPorUsuarioId = request.CriadoPorUsuarioId,
                ArquivosJson = request.Arquivos == null ? null : JsonSerializer.Serialize(request.Arquivos)
            };

        private static GarageLeadListItemDto MapListItem(GarageLeadListRow row)
        {
            var carroInteresse = ResolverCarroInteresse(row.Objetivo, row.ModeloInteresse, row.TrocaModeloDesejado, row.VendaModelo);
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
        }

        private static string? LimparFiltro(string? valor) => string.IsNullOrWhiteSpace(valor) ? null : valor.Trim();

        internal static string? ResolverCarroInteresse(string? objetivo, string? modeloInteresse, string? trocaModeloDesejado, string? vendaModelo)
            => LimparFiltro(objetivo)?.ToLowerInvariant() switch
            {
                "trocar" => LimparFiltro(trocaModeloDesejado) ?? LimparFiltro(modeloInteresse),
                "vender" => LimparFiltro(vendaModelo),
                _ => LimparFiltro(modeloInteresse)
            };

        private static List<GarageLeadSimulationFileDto> DeserializeArquivos(string? arquivosJson)
        {
            try
            {
                return string.IsNullOrWhiteSpace(arquivosJson)
                    ? new List<GarageLeadSimulationFileDto>()
                    : JsonSerializer.Deserialize<List<GarageLeadSimulationFileDto>>(arquivosJson) ?? new List<GarageLeadSimulationFileDto>();
            }
            catch
            {
                return new List<GarageLeadSimulationFileDto>();
            }
        }

        private static string ExtractStoredName(string? caminhoRelativo, string nomeOriginal)
        {
            if (!string.IsNullOrWhiteSpace(caminhoRelativo))
            {
                var normalized = caminhoRelativo.Replace("\\", "/");
                var idx = normalized.LastIndexOf('/');
                return idx >= 0 ? normalized[(idx + 1)..] : normalized;
            }

            return nomeOriginal;
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
            public string? TipoSimulacao { get; set; }
            public string? Comentario { get; set; }
            public string? Valor { get; set; }
            public string? VeiculoMarca { get; set; }
            public string? VeiculoModelo { get; set; }
            public string? VeiculoVersao { get; set; }
            public int? VeiculoAno { get; set; }
            public int? VeiculoKm { get; set; }
            public decimal? VeiculoValor { get; set; }
            public decimal? EntradaValor { get; set; }
            public decimal? SaldoFinanciado { get; set; }
            public int? ParcelasQuantidade { get; set; }
            public decimal? ParcelaValor { get; set; }
            public decimal? TaxaJurosMensal { get; set; }
            public string? Observacoes { get; set; }
            public DateTime? ValidadeEm { get; set; }
            public int? CriadoPorUsuarioId { get; set; }
            public string? ArquivosJson { get; set; }
            public DateTime Criado { get; set; }
            public DateTime Atualizado { get; set; }
        }
    }
}
