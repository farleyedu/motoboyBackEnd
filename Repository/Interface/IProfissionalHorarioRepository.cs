using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.Model;

namespace APIBack.Repository.Interface
{
    public interface IProfissionalHorarioRepository
    {
        Task<long> AdicionarAsync(ProfissionalHorario entity);
        Task<ProfissionalHorario?> BuscarPorIdAsync(long id);
        Task<IEnumerable<ProfissionalHorario>> BuscarTodosAsync();
        Task<int> AtualizarAsync(ProfissionalHorario entity);
        Task<int> ExcluirAsync(long id);
    }
}
