using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.Model;

namespace APIBack.Repository.Interface
{
    public interface IProfissionalRepository
    {
        Task<long> AdicionarAsync(Profissional entity);
        Task<Profissional?> BuscarPorIdAsync(long id);
        Task<IEnumerable<Profissional>> BuscarTodosAsync();
        Task<int> AtualizarAsync(Profissional entity);
        Task<int> ExcluirAsync(long id);
    }
}
