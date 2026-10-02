using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.DTOs.Delivery;

namespace APIBack.Repository.Interface
{
    /// <summary>Horario de funcionamento (semanal, Fase 1) e excecoes pontuais (Fase 2), usados tanto
    /// pelas telas de Configuracoes quanto para bloquear pedido fora do horario.</summary>
    public interface IHorarioOperacaoRepository
    {
        /// <summary>true quando as tabelas de horario ainda nao existem (aberto por padrao, nunca bloqueia).</summary>
        Task<bool> EstaAbertoAgoraAsync(Guid estabelecimentoId, DateTimeOffset agoraUtc, string timezoneIana);
        /// <summary>Hora de fechamento (local) de um dia, considerando as excecoes pontuais. Null = fechado nesse dia
        /// ou sem horario cadastrado (nesse caso nao ha fechamento a esperar).</summary>
        Task<TimeSpan?> ObterHoraFechamentoAsync(Guid estabelecimentoId, DateOnly dia);
        Task<IReadOnlyList<HorarioEspecialDto>> ListarEspeciaisAsync(Guid estabelecimentoId);
        Task<HorarioEspecialDto> SalvarEspecialAsync(Guid estabelecimentoId, SalvarHorarioEspecialRequest request);
        Task<bool> ExcluirEspecialAsync(Guid estabelecimentoId, long id);
    }
}
