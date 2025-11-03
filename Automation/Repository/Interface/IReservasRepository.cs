using System;
using System.Collections.Generic;

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
    }
}

