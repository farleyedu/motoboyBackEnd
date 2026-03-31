using System;
using System.Threading.Tasks;
using APIBack.Model;
using APIBack.Repository.Interface;
using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace APIBack.Repository
{
    public class EstabelecimentoAgendamentoConfigRepository : IEstabelecimentoAgendamentoConfigRepository
    {
        private readonly string _connectionString;

        public EstabelecimentoAgendamentoConfigRepository(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection")
                ?? throw new InvalidOperationException("Connection string 'DefaultConnection' nao encontrada.");
        }

        public async Task<EstabelecimentoAgendamentoConfig?> ObterAsync(Guid idEstabelecimento)
        {
            const string sql = @"
SELECT id_estabelecimento AS IdEstabelecimento,
       agenda_ativa AS AgendaAtiva,
       exige_servico AS ExigeServico,
       exige_profissional AS ExigeProfissional,
       permite_encaixe AS PermiteEncaixe,
       agenda_informativo AS AgendaInformativo,
       duracao_slot_minutos AS DuracaoSlotMinutos,
       intervalo_entre_slots_minutos AS IntervaloEntreSlotsMinutos,
       limite_por_slot AS LimitePorSlot,
       antecedencia_minima_horas AS AntecedenciaMinimaHoras,
       antecedencia_maxima_dias AS AntecedenciaMaximaDias,
       created_at AS CreatedAt,
       updated_at AS UpdatedAt
  FROM estabelecimento_agendamento_config
 WHERE id_estabelecimento = @IdEstabelecimento;";

            await using var connection = new NpgsqlConnection(_connectionString);
            return await connection.QueryFirstOrDefaultAsync<EstabelecimentoAgendamentoConfig>(sql, new
            {
                IdEstabelecimento = idEstabelecimento
            });
        }

        public async Task SalvarAsync(EstabelecimentoAgendamentoConfig entity)
        {
            const string sql = @"
INSERT INTO estabelecimento_agendamento_config (
    id_estabelecimento,
    agenda_ativa,
    exige_servico,
    exige_profissional,
    permite_encaixe,
    agenda_informativo,
    duracao_slot_minutos,
    intervalo_entre_slots_minutos,
    limite_por_slot,
    antecedencia_minima_horas,
    antecedencia_maxima_dias,
    created_at,
    updated_at
) VALUES (
    @IdEstabelecimento,
    @AgendaAtiva,
    @ExigeServico,
    @ExigeProfissional,
    @PermiteEncaixe,
    @AgendaInformativo,
    @DuracaoSlotMinutos,
    @IntervaloEntreSlotsMinutos,
    @LimitePorSlot,
    @AntecedenciaMinimaHoras,
    @AntecedenciaMaximaDias,
    @CreatedAt,
    @UpdatedAt
)
ON CONFLICT (id_estabelecimento) DO UPDATE
SET agenda_ativa = EXCLUDED.agenda_ativa,
    exige_servico = EXCLUDED.exige_servico,
    exige_profissional = EXCLUDED.exige_profissional,
    permite_encaixe = EXCLUDED.permite_encaixe,
    agenda_informativo = EXCLUDED.agenda_informativo,
    duracao_slot_minutos = EXCLUDED.duracao_slot_minutos,
    intervalo_entre_slots_minutos = EXCLUDED.intervalo_entre_slots_minutos,
    limite_por_slot = EXCLUDED.limite_por_slot,
    antecedencia_minima_horas = EXCLUDED.antecedencia_minima_horas,
    antecedencia_maxima_dias = EXCLUDED.antecedencia_maxima_dias,
    updated_at = EXCLUDED.updated_at;";

            var now = DateTime.UtcNow;
            if (entity.CreatedAt == default)
            {
                entity.CreatedAt = now;
            }

            entity.UpdatedAt = now;

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.ExecuteAsync(sql, entity);
        }
    }
}
