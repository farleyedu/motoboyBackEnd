using System;
using System.Collections.Generic;
using APIBack.DTOs;

namespace APIBack.Repository.Interface
{
    public interface IReservasRepository
    {
        /// <summary>
        /// Obtém reservas de restaurante por mês/ano/estabelecimento
        /// </summary>
        IEnumerable<dynamic> GetReservasRestaurante(int month, int year, Guid estabelecimentoId);

        /// <summary>
        /// Obtém reservas de barbearia + lista de barbeiros ativos
        /// </summary>
        (IEnumerable<dynamic> reservations, IEnumerable<dynamic> barbers) GetReservasBarbearia(int month, int year, Guid estabelecimentoId);

        /// <summary>
        /// Obtém métricas consolidadas das reservas confirmadas de um dia específico.
        /// </summary>
        MetricasDiaDTO GetMetricasDia(DateTime data);
    }
}

