using System;
using System.Collections.Generic;
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
    }
}

