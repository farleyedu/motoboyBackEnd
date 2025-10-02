using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.Model;

namespace APIBack.Repository.Interface
{
    public interface IEstabelecimentoServicoRepository
    {
        Task<long> AdicionarAsync(EstabelecimentoServico entity);
        Task<EstabelecimentoServico?> BuscarPorIdAsync(long id);
        Task<IEnumerable<EstabelecimentoServico>> BuscarTodosAsync();
        Task<int> AtualizarAsync(EstabelecimentoServico entity);
        Task<int> ExcluirAsync(long id);
    }
}
