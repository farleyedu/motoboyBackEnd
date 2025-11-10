using System;
using System.Collections.Generic;
using System.Threading.Tasks;
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
        /// Conta total de reservas e pessoas confirmadas em um período específico.
        /// </summary>
        (int totalReservas, int totalPessoas) ContarReservasPorPeriodo(
            DateTime dataInicio,
            DateTime dataFim,
            Guid estabelecimentoId,
            long? barbeiroId = null);

        /// <summary>
        /// Atualiza o status de uma reserva específica.
        /// </summary>
        Task<bool> AtualizarStatusAsync(int id, string novoStatus);

        /// <summary>
        /// Lista reservas de um dia específico incluindo telefone do cliente.
        /// </summary>
        Task<IEnumerable<dynamic>> ListarPorDiaAsync(DateOnly data);

        /// <summary>
        /// Obtém métricas consolidadas das reservas confirmadas de um dia específico.
        /// </summary>
        MetricasDiaDTO GetMetricasDia(DateTime data);
    }
}
