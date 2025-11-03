using System;
using System.Collections.Generic;
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
        /// Obtém reservas de restaurante por mês/ano/estabelecimento
        /// </summary>
        public IEnumerable<dynamic> GetReservasRestaurante(int month, int year, Guid estabelecimentoId)
        {
            return _reservasRepository.GetReservasRestaurante(month, year, estabelecimentoId);
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
    }
}

