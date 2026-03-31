using System;
using System.Threading.Tasks;
using APIBack.DTOs.Agendamentos;

namespace APIBack.Service.Interface
{
    public interface IEstabelecimentoAgendamentoConfigService
    {
        Task<EstabelecimentoAgendamentoConfigDto> ObterAsync(Guid idEstabelecimento);
        Task<EstabelecimentoAgendamentoConfigDto> SalvarAsync(Guid idEstabelecimento, SalvarEstabelecimentoAgendamentoConfigRequest request);
    }
}
