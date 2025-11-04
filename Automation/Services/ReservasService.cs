using System;
using System.Collections.Generic;
using System.Linq;
using APIBack.DTOs;
using APIBack.Repository.Interface;
using APIBack.Service.Interface;

namespace APIBack.Service
{
    public class ReservasService : IReservasService
    {
        private readonly IReservasRepository _reservasRepository;

        public ReservasService(IReservasRepository reservasRepository)
        {
            _reservasRepository = reservasRepository;
        }

        /// <summary>
        /// Obtém reservas de restaurante por mês/ano/estabelecimento agrupadas por data com total de pessoas confirmadas.
        /// </summary>
        public IEnumerable<ReservasDiaDTO> GetReservasRestaurante(int month, int year, Guid estabelecimentoId)
        {
            var reservas = _reservasRepository
                .GetReservasRestaurante(month, year, estabelecimentoId)
                ?.ToList() ?? new List<dynamic>();

            var grupos = reservas.GroupBy(ExtractDataReserva);
            var resultado = new List<ReservasDiaDTO>();

            foreach (var grupo in grupos.OrderBy(g => g.Key))
            {
                if (grupo.Key == DateTime.MinValue)
                {
                    continue;
                }

                var reservasDia = grupo.Cast<dynamic>().ToList();
                var totalPessoasDia = 0;

                foreach (var reserva in reservasDia)
                {
                    if (EhConfirmada(reserva))
                    {
                        totalPessoasDia += ExtrairQuantidadePessoas(reserva);
                    }
                }

                resultado.Add(new ReservasDiaDTO
                {
                    DataReserva = grupo.Key,
                    Reservas = reservasDia,
                    TotalPessoasDia = totalPessoasDia
                });
            }

            return resultado;
        }

        /// <summary>
        /// Obtém reservas de barbearia + lista de barbeiros ativos
        /// </summary>
        public object GetReservasBarbearia(int month, int year, Guid estabelecimentoId)
        {
            var (reservations, barbers) = _reservasRepository.GetReservasBarbearia(month, year, estabelecimentoId);

            return new
            {
                reservations,
                barbers
            };
        }

        /// <summary>
        /// Obtém métricas consolidadas de reservas confirmadas em uma data específica.
        /// </summary>
        public MetricasDiaDTO GetMetricasDia(DateTime data)
        {
            return _reservasRepository.GetMetricasDia(data.Date);
        }

        private static DateTime ExtractDataReserva(dynamic reserva)
        {
            if (reserva == null)
            {
                return DateTime.MinValue;
            }

            var value = reserva.data_reserva;

            if (value is DateTime dt)
            {
                return dt.Date;
            }

if (DateTime.TryParse(Convert.ToString(value), out DateTime parsed))            {
                return parsed.Date;
            }

            return DateTime.MinValue;
        }

        private static bool EhConfirmada(dynamic reserva)
        {
            var status = Convert.ToString(reserva?.status) ?? string.Empty;
return status.Equals("confirmada", StringComparison.OrdinalIgnoreCase) ||
       status.Equals("confirmado", StringComparison.OrdinalIgnoreCase);        }

        private static int ExtrairQuantidadePessoas(dynamic reserva)
        {
            if (reserva == null)
            {
                return 0;
            }

            var value = reserva.qtd_pessoas;

            if (value == null)
            {
                return 0;
            }

            if (value is int i)
            {
                return i;
            }

            if (value is long l)
            {
                return (int)l;
            }

if (int.TryParse(Convert.ToString(value), out int parsed))            {
                return parsed;
            }

            return 0;
        }
    }
}
