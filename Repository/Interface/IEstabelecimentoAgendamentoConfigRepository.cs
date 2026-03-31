using System;
using System.Threading.Tasks;
using APIBack.Model;

namespace APIBack.Repository.Interface
{
    public interface IEstabelecimentoAgendamentoConfigRepository
    {
        Task<EstabelecimentoAgendamentoConfig?> ObterAsync(Guid idEstabelecimento);
        Task SalvarAsync(EstabelecimentoAgendamentoConfig entity);
    }
}
