using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.DTOs;

namespace APIBack.Service.Interface
{
    public interface IReservasService
    {
        /// <summary>
        /// Obtém reservas de restaurante por mês/ano/estabelecimento
        /// </summary>
        IEnumerable<ReservasDiaDTO> GetReservasRestaurante(int month, int year, Guid estabelecimentoId);

        /// <summary>
        /// Obtém reservas de barbearia + lista de barbeiros ativos
        /// </summary>
        object GetReservasBarbearia(int month, int year, Guid estabelecimentoId);

        /// <summary>
        /// Obtém métricas de um dia específico (reservas confirmadas e total de pessoas).
        /// </summary>
        MetricasDiaDTO GetMetricasDia(DateTime data);

        /// <summary>
        /// Atualiza o status de uma reserva para concluído.
        /// </summary>
        Task MarcarChegadaAsync(int id);

        /// <summary>
        /// Gera o arquivo Excel com as reservas do dia informado.
        /// </summary>
        Task<byte[]> ExportarDiaAsync(DateOnly data);

        /// <summary>
        /// Obtém métricas agregadas por períodos (dia, semana, quinzena, mês).
        /// </summary>
        MetricasMesDTO GetMetricasMes(int month, int year, Guid estabelecimentoId);

        /// <summary>
        /// Obtém métricas agregadas para um período arbitrário.
        /// </summary>
        MetricasPeriodoDTO GetMetricasPeriodo(DateTime inicio, DateTime fim, Guid estabelecimentoId, long? barbeiroId);
    }
}
