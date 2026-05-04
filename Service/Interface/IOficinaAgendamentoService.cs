using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.DTOs.Agendamentos;

namespace APIBack.Service.Interface
{
    public interface IOficinaAgendamentoService
    {
        Task<IReadOnlyCollection<OficinaSlotDto>> BuscarSlotsAsync(Guid idEstabelecimento, Guid? idServico, DateTime data, int duracaoMinutos, long? idProfissional = null, int limite = 6);
        Task<OficinaAgendamentoDto> CriarAsync(Guid idEstabelecimento, Guid idCliente, string telefoneE164, CriarOficinaAgendamentoRequest request);
        Task<IReadOnlyCollection<OficinaAgendamentoDto>> ListarAtivosPorClienteAsync(Guid idEstabelecimento, Guid idCliente, string? telefoneE164);
        Task<IReadOnlyCollection<OficinaAgendamentoDto>> ListarPorPeriodoAsync(Guid idEstabelecimento, DateTime dataInicio, DateTime dataFim, string? status, long? idProfissional, Guid? idServico);
        Task<OficinaAgendamentoDto> RemarcarAsync(Guid idEstabelecimento, Guid id, RemarcarOficinaAgendamentoRequest request);
        Task<OficinaAgendamentoDto> CancelarAsync(Guid idEstabelecimento, Guid id, string? motivo);
        Task<OficinaAgendamentoDto?> ObterPorCodigoAsync(Guid idEstabelecimento, string codigo);
    }
}
