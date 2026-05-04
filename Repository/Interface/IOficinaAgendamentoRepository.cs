using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.Model;

namespace APIBack.Repository.Interface
{
    public interface IOficinaAgendamentoRepository
    {
        Task<Guid> CriarAsync(OficinaAgendamento agendamento);
        Task<OficinaAgendamento?> ObterPorIdAsync(Guid idEstabelecimento, Guid id);
        Task<OficinaAgendamento?> ObterPorCodigoAsync(Guid idEstabelecimento, string codigo);
        Task<IReadOnlyCollection<OficinaAgendamento>> ListarAtivosPorClienteAsync(Guid idEstabelecimento, Guid idCliente, string? telefoneE164);
        Task<IReadOnlyCollection<OficinaAgendamento>> ListarPorConversaAsync(Guid idConversa);
        Task<IReadOnlyCollection<OficinaAgendamento>> ListarPorPeriodoAsync(Guid idEstabelecimento, DateTime dataInicio, DateTime dataFim, string? status, long? idProfissional, Guid? idServico);
        Task<int> ContarConflitosAsync(Guid idEstabelecimento, DateTime data, TimeSpan horaInicio, TimeSpan horaFim, long? idProfissional, Guid? ignorarId = null);
        Task AtualizarAsync(OficinaAgendamento agendamento);
        Task AtualizarStatusAsync(Guid idEstabelecimento, Guid id, string status, DateTime? dataCancelamento = null);
        Task<string> GerarCodigoUnicoAsync(Guid idEstabelecimento);
    }
}
