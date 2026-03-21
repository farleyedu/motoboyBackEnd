using System;
using System.Threading.Tasks;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Models;
using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace APIBack.Automation.Infra
{
    public class SqlNauticaLeadRepository : INauticaLeadRepository
    {
        private readonly string _connectionString;

        public SqlNauticaLeadRepository(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection")
                ?? configuration["ConnectionStrings:DefaultConnection"]
                ?? throw new InvalidOperationException("Connection string 'DefaultConnection' nao encontrada.");
        }

        public async Task<NauticaLead?> ObterLeadAbertoAsync(Guid idEstabelecimento, string telefoneE164)
        {
            const string sql = @"
SELECT id,
       id_estabelecimento   AS IdEstabelecimento,
       id_conversa          AS IdConversa,
       id_cliente           AS IdCliente,
       telefone_e164        AS TelefoneE164,
       tem_loja_fisica      AS TemLojaFisica,
       cnpj                 AS Cnpj,
       segmento             AS Segmento,
       consegue_minimo      AS ConsegueMinimo,
       nome_empresa         AS NomeEmpresa,
       cidade_estado        AS CidadeEstado,
       historico_nautica    AS HistoricoNautica,
       desafio_loja         AS DesafioLoja,
       publico_alvo         AS PublicoAlvo,
       status               AS Status,
       motivo_desqualificacao AS MotivoDesqualificacao,
       via_numero_central   AS ViaNumeroCentral,
       data_conclusao       AS DataConclusao,
       data_criacao         AS DataCriacao,
       data_atualizacao     AS DataAtualizacao
  FROM cliente_nautica
 WHERE id_estabelecimento = @IdEstabelecimento
   AND telefone_e164 = @TelefoneE164
   AND status = 'em_andamento'
 ORDER BY data_criacao DESC
 LIMIT 1;";

            await using var connection = new NpgsqlConnection(_connectionString);
            return await connection.QueryFirstOrDefaultAsync<NauticaLead>(sql, new
            {
                IdEstabelecimento = idEstabelecimento,
                TelefoneE164 = telefoneE164
            });
        }

        public async Task<NauticaLead?> ObterPorConversaAsync(Guid idConversa)
        {
            const string sql = @"
SELECT id,
       id_estabelecimento   AS IdEstabelecimento,
       id_conversa          AS IdConversa,
       id_cliente           AS IdCliente,
       telefone_e164        AS TelefoneE164,
       tem_loja_fisica      AS TemLojaFisica,
       cnpj                 AS Cnpj,
       segmento             AS Segmento,
       consegue_minimo      AS ConsegueMinimo,
       nome_empresa         AS NomeEmpresa,
       cidade_estado        AS CidadeEstado,
       historico_nautica    AS HistoricoNautica,
       desafio_loja         AS DesafioLoja,
       publico_alvo         AS PublicoAlvo,
       status               AS Status,
       motivo_desqualificacao AS MotivoDesqualificacao,
       via_numero_central   AS ViaNumeroCentral,
       data_conclusao       AS DataConclusao,
       data_criacao         AS DataCriacao,
       data_atualizacao     AS DataAtualizacao
  FROM cliente_nautica
 WHERE id_conversa = @IdConversa
 ORDER BY data_criacao DESC
 LIMIT 1;";

            await using var connection = new NpgsqlConnection(_connectionString);
            return await connection.QueryFirstOrDefaultAsync<NauticaLead>(sql, new { IdConversa = idConversa });
        }

        public async Task<Guid> CriarAsync(NauticaLead lead)
        {
            const string sql = @"
INSERT INTO cliente_nautica (
    id,
    id_estabelecimento,
    id_conversa,
    id_cliente,
    telefone_e164,
    tem_loja_fisica,
    cnpj,
    segmento,
    consegue_minimo,
    nome_empresa,
    cidade_estado,
    historico_nautica,
    desafio_loja,
    publico_alvo,
    status,
    motivo_desqualificacao,
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
    @TemLojaFisica,
    @Cnpj,
    @Segmento,
    @ConsegueMinimo,
    @NomeEmpresa,
    @CidadeEstado,
    @HistoricoNautica,
    @DesafioLoja,
    @PublicoAlvo,
    @Status,
    @MotivoDesqualificacao,
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

        public async Task AtualizarAsync(NauticaLead lead)
        {
            const string sql = @"
UPDATE cliente_nautica
   SET id_conversa          = @IdConversa,
       id_cliente           = @IdCliente,
       telefone_e164        = @TelefoneE164,
       tem_loja_fisica      = @TemLojaFisica,
       cnpj                 = @Cnpj,
       segmento             = @Segmento,
       consegue_minimo      = @ConsegueMinimo,
       nome_empresa         = @NomeEmpresa,
       cidade_estado        = @CidadeEstado,
       historico_nautica    = @HistoricoNautica,
       desafio_loja         = @DesafioLoja,
       publico_alvo         = @PublicoAlvo,
       status               = @Status,
       motivo_desqualificacao = @MotivoDesqualificacao,
       via_numero_central   = @ViaNumeroCentral,
       data_conclusao       = @DataConclusao,
       data_atualizacao     = @DataAtualizacao
 WHERE id = @Id;";

            lead.DataAtualizacao = DateTime.UtcNow;

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.ExecuteAsync(sql, lead);
        }

        public async Task ConcluirAsync(NauticaLead lead)
        {
            const string sql = @"
UPDATE cliente_nautica
   SET tem_loja_fisica      = @TemLojaFisica,
       cnpj                 = @Cnpj,
       segmento             = @Segmento,
       consegue_minimo      = @ConsegueMinimo,
       nome_empresa         = @NomeEmpresa,
       cidade_estado        = @CidadeEstado,
       historico_nautica    = @HistoricoNautica,
       desafio_loja         = @DesafioLoja,
       publico_alvo         = @PublicoAlvo,
       status               = 'concluido',
       data_conclusao       = NOW(),
       data_atualizacao     = NOW()
 WHERE id = @Id;";

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.ExecuteAsync(sql, lead);
        }

        public async Task DesqualificarAsync(NauticaLead lead, string motivo)
        {
            const string sql = @"
UPDATE cliente_nautica
   SET status                  = 'desqualificado',
       motivo_desqualificacao  = @Motivo,
       data_conclusao          = NOW(),
       data_atualizacao        = NOW()
 WHERE id = @Id;";

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.ExecuteAsync(sql, new { lead.Id, Motivo = motivo });
        }
    }
}
