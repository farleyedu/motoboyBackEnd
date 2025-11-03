using System;
using System.Collections.Generic;

namespace APIBack.Service.Interface
{
    public interface IReservasService
    {
        /// <summary>
        /// Obtém reservas de restaurante por mês/ano/estabelecimento
        /// </summary>
        IEnumerable<dynamic> GetReservasRestaurante(int month, int year, Guid estabelecimentoId);

        /// <summary>
        /// Obtém reservas de barbearia + lista de barbeiros ativos
        /// </summary>
        object GetReservasBarbearia(int month, int year, Guid estabelecimentoId);
    }
}

