using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.Model;

namespace APIBack.Repository.Interface
{
    public interface IReservaRepository
    {
        Task<long> AdicionarAsync(Reserva entity);
        Task<Reserva?> BuscarPorIdAsync(long id);
        Task<IEnumerable<Reserva>> BuscarTodosAsync();
        Task<int> AtualizarAsync(Reserva entity);
        Task<int> ExcluirAsync(long id);
        Task<int> CancelarReservaAsync(long id);
        Task<bool> BuscarDisponibilidadeAsync(long idEstabelecimento, DateTime dataReserva, TimeSpan horaInicio, TimeSpan? horaFim, long? idProfissional = null);
    }
}
