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
        Task<IReadOnlyList<HorarioEspecialDto>> ListarEspeciaisAsync(Guid estabelecimentoId);
        Task<HorarioEspecialDto> SalvarEspecialAsync(Guid estabelecimentoId, SalvarHorarioEspecialRequest request);
        Task<bool> ExcluirEspecialAsync(Guid estabelecimentoId, long id);
    }
}
