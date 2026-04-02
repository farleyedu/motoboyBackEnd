using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.Model;

namespace APIBack.Repository.Interface
{
    public interface IConfiguracaoCarroRepository
    {
        Task<IReadOnlyCollection<EstabelecimentoCarro>> ListarPorEstabelecimentoAsync(Guid idEstabelecimento);
    }
}
