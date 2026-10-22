using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.DTOs.Delivery;
using APIBack.DTOs.Configuracoes;

namespace APIBack.Repository.Interface
{
    /// <summary>Se a loja esta dentro do horario agora e, se nao, por que (SEM_HORARIO | FORA_HORARIO) e quando abre.</summary>
    public sealed record AberturaLoja(bool Dentro, string? Motivo, string? ProximaAbertura, string Timezone);

    /// <summary>Horario de funcionamento (semanal, Fase 1) e excecoes pontuais (Fase 2), usados tanto
    /// pelas telas de Configuracoes quanto para bloquear pedido fora do horario.</summary>
    public interface IHorarioOperacaoRepository
    {
        /// <summary>
        /// Dentro do horario agora, no fuso da loja (estabelecimentos.timezone_iana). Sem horario semanal e sem excecao para o dia,
        /// FECHADA. <paramref name="timezoneIana"/> so vale se a loja nao tiver fuso proprio.
        /// </summary>
        Task<bool> EstaAbertoAgoraAsync(Guid estabelecimentoId, DateTimeOffset agoraUtc, string? timezoneIana = null);
        /// <summary>A mesma regra de <see cref="EstaAbertoAgoraAsync"/>, com o motivo e a proxima abertura.</summary>
        Task<AberturaLoja> AvaliarAberturaAsync(Guid estabelecimentoId, DateTimeOffset agoraUtc, string? timezoneIana = null);
        /// <summary>Hora de fechamento (local) de um dia, considerando as excecoes pontuais. Null = fechado nesse dia
        /// ou sem horario cadastrado (nesse caso nao ha fechamento a esperar).</summary>
        Task<TimeSpan?> ObterHoraFechamentoAsync(Guid estabelecimentoId, DateOnly dia);
        Task<IReadOnlyList<HorarioEspecialDto>> ListarEspeciaisAsync(Guid estabelecimentoId);
        Task<HorarioEspecialDto> SalvarEspecialAsync(Guid estabelecimentoId, SalvarHorarioEspecialRequest request);
        Task<bool> ExcluirEspecialAsync(Guid estabelecimentoId, long id);
        Task<StoreOperationDto> ObterEstadoAsync(Guid estabelecimentoId, CancellationToken cancellationToken = default);
        Task<StoreOperationDto> AbrirHojeAsync(Guid estabelecimentoId, int actorUserId, OpenStoreTodayRequest request, CancellationToken cancellationToken = default);
        /// <summary>Fecha a loja agora (excecao do dia que termina neste minuto). A grade semanal nao muda; o proximo turno volta ao normal.</summary>
        Task<StoreOperationDto> FecharAgoraAsync(Guid estabelecimentoId, int actorUserId, CancellationToken cancellationToken = default);
    }
}
