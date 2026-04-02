using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.DTOs.Configuracoes;

namespace APIBack.Service.Interface
{
    public interface IConfiguracaoCarroService
    {
        Task<IReadOnlyCollection<CarroEstabelecimentoDto>> ListarPorEstabelecimentoAsync(Guid idEstabelecimento);
    }
}
