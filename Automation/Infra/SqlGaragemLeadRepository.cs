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
       objetivo,
       modelo_interesse   AS ModeloInteresse,
       faixa_investimento AS FaixaInvestimento,
       forma_pagamento    AS FormaPagamento,
       valor_entrada_texto AS ValorEntradaTexto,
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
       objetivo,
       modelo_interesse   AS ModeloInteresse,
       faixa_investimento AS FaixaInvestimento,
       forma_pagamento    AS FormaPagamento,
       valor_entrada_texto AS ValorEntradaTexto,
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
    objetivo,
    modelo_interesse,
    faixa_investimento,
    forma_pagamento,
    valor_entrada_texto,
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
    @Objetivo,
    @ModeloInteresse,
    @FaixaInvestimento,
    @FormaPagamento,
    @ValorEntradaTexto,
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
       objetivo = @Objetivo,
       modelo_interesse = @ModeloInteresse,
       faixa_investimento = @FaixaInvestimento,
       forma_pagamento = @FormaPagamento,
       valor_entrada_texto = @ValorEntradaTexto,
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

        public async Task ConcluirAsync(
            Guid idLead,
            string nomeCliente,
            string objetivo,
            string modeloInteresse,
            string faixaInvestimento,
            string formaPagamento,
            string valorEntradaTexto,
            string urgencia)
        {
            const string sql = @"
UPDATE cliente_garagem
   SET nome_cliente = @NomeCliente,
       objetivo = @Objetivo,
       modelo_interesse = @ModeloInteresse,
       faixa_investimento = @FaixaInvestimento,
       forma_pagamento = @FormaPagamento,
       valor_entrada_texto = @ValorEntradaTexto,
       urgencia = @Urgencia,
       status = 'concluido',
       data_conclusao = NOW(),
       data_atualizacao = NOW()
 WHERE id = @IdLead;";

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.ExecuteAsync(sql, new
            {
                IdLead = idLead,
                NomeCliente = nomeCliente,
                Objetivo = objetivo,
                ModeloInteresse = modeloInteresse,
                FaixaInvestimento = faixaInvestimento,
                FormaPagamento = formaPagamento,
                ValorEntradaTexto = valorEntradaTexto,
                Urgencia = urgencia
            });
        }
    }
}
