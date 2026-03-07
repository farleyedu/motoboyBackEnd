using System;
using System.Threading.Tasks;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Models;
using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace APIBack.Automation.Infra
{
    public class SqlGaragemLeadRepository : IGaragemLeadRepository
    {
        private readonly string _connectionString;

        public SqlGaragemLeadRepository(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection")
                ?? configuration["ConnectionStrings:DefaultConnection"]
                ?? throw new InvalidOperationException("Connection string 'DefaultConnection' nao encontrada.");
        }

        public async Task<GarageLead?> ObterLeadAbertoAsync(Guid idEstabelecimento, string telefoneE164)
        {
            const string sql = @"
SELECT id,
       id_estabelecimento AS IdEstabelecimento,
       id_conversa        AS IdConversa,
       id_cliente         AS IdCliente,
       telefone_e164      AS TelefoneE164,
       nome_cliente       AS NomeCliente,
       cpf                AS Cpf,
       objetivo,
       modelo_interesse   AS ModeloInteresse,
       faixa_investimento AS FaixaInvestimento,
       forma_pagamento    AS FormaPagamento,
       valor_entrada_texto AS ValorEntradaTexto,
       troca_modelo_atual AS TrocaModeloAtual,
       troca_ano_modelo   AS TrocaAnoModelo,
       troca_km           AS TrocaKm,
       troca_quitado      AS TrocaQuitado,
       troca_modelo_desejado AS TrocaModeloDesejado,
       troca_condicao     AS TrocaCondicao,
       venda_modelo       AS VendaModelo,
       venda_ano          AS VendaAno,
       venda_km           AS VendaKm,
       venda_quitado      AS VendaQuitado,
       urgencia,
       status,
       via_numero_central AS ViaNumeroCentral,
       data_conclusao     AS DataConclusao,
       data_criacao       AS DataCriacao,
       data_atualizacao   AS DataAtualizacao
  FROM cliente_garagem
 WHERE id_estabelecimento = @IdEstabelecimento
   AND telefone_e164 = @TelefoneE164
   AND status = 'em_andamento'
 ORDER BY data_criacao DESC
 LIMIT 1;";

            await using var connection = new NpgsqlConnection(_connectionString);
            return await connection.QueryFirstOrDefaultAsync<GarageLead>(sql, new
            {
                IdEstabelecimento = idEstabelecimento,
                TelefoneE164 = telefoneE164
            });
        }

        public async Task<GarageLead?> ObterPorConversaAsync(Guid idConversa)
        {
            const string sql = @"
SELECT id,
       id_estabelecimento AS IdEstabelecimento,
       id_conversa        AS IdConversa,
       id_cliente         AS IdCliente,
       telefone_e164      AS TelefoneE164,
       nome_cliente       AS NomeCliente,
       cpf                AS Cpf,
       objetivo,
       modelo_interesse   AS ModeloInteresse,
       faixa_investimento AS FaixaInvestimento,
       forma_pagamento    AS FormaPagamento,
       valor_entrada_texto AS ValorEntradaTexto,
       troca_modelo_atual AS TrocaModeloAtual,
       troca_ano_modelo   AS TrocaAnoModelo,
       troca_km           AS TrocaKm,
       troca_quitado      AS TrocaQuitado,
       troca_modelo_desejado AS TrocaModeloDesejado,
       troca_condicao     AS TrocaCondicao,
       venda_modelo       AS VendaModelo,
       venda_ano          AS VendaAno,
       venda_km           AS VendaKm,
       venda_quitado      AS VendaQuitado,
       urgencia,
       status,
       via_numero_central AS ViaNumeroCentral,
       data_conclusao     AS DataConclusao,
       data_criacao       AS DataCriacao,
       data_atualizacao   AS DataAtualizacao
  FROM cliente_garagem
 WHERE id_conversa = @IdConversa
 ORDER BY data_criacao DESC
 LIMIT 1;";

            await using var connection = new NpgsqlConnection(_connectionString);
            return await connection.QueryFirstOrDefaultAsync<GarageLead>(sql, new { IdConversa = idConversa });
        }

        public async Task<Guid> CriarAsync(GarageLead lead)
        {
            const string sql = @"
INSERT INTO cliente_garagem (
    id,
    id_estabelecimento,
    id_conversa,
    id_cliente,
    telefone_e164,
    nome_cliente,
    cpf,
    objetivo,
    modelo_interesse,
    faixa_investimento,
    forma_pagamento,
    valor_entrada_texto,
    troca_modelo_atual,
    troca_ano_modelo,
    troca_km,
    troca_quitado,
    troca_modelo_desejado,
    troca_condicao,
    venda_modelo,
    venda_ano,
    venda_km,
    venda_quitado,
    urgencia,
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
    @TelefoneE164,
    @NomeCliente,
    @Cpf,
    @Objetivo,
    @ModeloInteresse,
    @FaixaInvestimento,
    @FormaPagamento,
    @ValorEntradaTexto,
    @TrocaModeloAtual,
    @TrocaAnoModelo,
    @TrocaKm,
    @TrocaQuitado,
    @TrocaModeloDesejado,
    @TrocaCondicao,
    @VendaModelo,
    @VendaAno,
    @VendaKm,
    @VendaQuitado,
    @Urgencia,
    @Status,
    @ViaNumeroCentral,
    @DataConclusao,
    @DataCriacao,
    @DataAtualizacao
);";

            if (lead.Id == Guid.Empty)
            {
                lead.Id = Guid.NewGuid();
            }

            var agora = DateTime.UtcNow;
            if (lead.DataCriacao == default)
            {
                lead.DataCriacao = agora;
            }

            lead.DataAtualizacao = agora;

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.ExecuteAsync(sql, lead);
            return lead.Id;
        }

        public async Task AtualizarAsync(GarageLead lead)
        {
            const string sql = @"
UPDATE cliente_garagem
   SET id_conversa = @IdConversa,
       id_cliente = @IdCliente,
       telefone_e164 = @TelefoneE164,
       nome_cliente = @NomeCliente,
       cpf = @Cpf,
       objetivo = @Objetivo,
       modelo_interesse = @ModeloInteresse,
       faixa_investimento = @FaixaInvestimento,
       forma_pagamento = @FormaPagamento,
       valor_entrada_texto = @ValorEntradaTexto,
       troca_modelo_atual = @TrocaModeloAtual,
       troca_ano_modelo = @TrocaAnoModelo,
       troca_km = @TrocaKm,
       troca_quitado = @TrocaQuitado,
       troca_modelo_desejado = @TrocaModeloDesejado,
       troca_condicao = @TrocaCondicao,
       venda_modelo = @VendaModelo,
       venda_ano = @VendaAno,
       venda_km = @VendaKm,
       venda_quitado = @VendaQuitado,
       urgencia = @Urgencia,
       status = @Status,
       via_numero_central = @ViaNumeroCentral,
       data_conclusao = @DataConclusao,
       data_atualizacao = @DataAtualizacao
 WHERE id = @Id;";

            lead.DataAtualizacao = DateTime.UtcNow;

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.ExecuteAsync(sql, lead);
        }

        public async Task ConcluirAsync(GarageLead lead)
        {
            const string sql = @"
UPDATE cliente_garagem
   SET nome_cliente = @NomeCliente,
       cpf = @Cpf,
       objetivo = @Objetivo,
       modelo_interesse = @ModeloInteresse,
       faixa_investimento = @FaixaInvestimento,
       forma_pagamento = @FormaPagamento,
       valor_entrada_texto = @ValorEntradaTexto,
       troca_modelo_atual = @TrocaModeloAtual,
       troca_ano_modelo = @TrocaAnoModelo,
       troca_km = @TrocaKm,
       troca_quitado = @TrocaQuitado,
       troca_modelo_desejado = @TrocaModeloDesejado,
       troca_condicao = @TrocaCondicao,
       venda_modelo = @VendaModelo,
       venda_ano = @VendaAno,
       venda_km = @VendaKm,
       venda_quitado = @VendaQuitado,
       urgencia = @Urgencia,
       status = 'concluido',
       data_conclusao = NOW(),
       data_atualizacao = NOW()
 WHERE id = @Id;";

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.ExecuteAsync(sql, lead);
        }
    }
}
