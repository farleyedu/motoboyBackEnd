using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.Automation.Models;

namespace APIBack.Automation.Interfaces
{
    public interface IOficinaAtendimentoRepository
    {
        Task<OficinaAtendimento?> ObterAbertoAsync(Guid idEstabelecimento, string telefoneE164);
        Task<OficinaAtendimento?> ObterPorConversaAsync(Guid idConversa);
        Task<Guid> CriarAsync(OficinaAtendimento atendimento);
        Task AtualizarAsync(OficinaAtendimento atendimento);
        Task AtualizarExtrasAsync(Guid id, IReadOnlyDictionary<string, object?> dadosExtras);
        Task AtualizarStatusAsync(Guid id, string status);
        Task ConcluirAsync(Guid id, string status);
    }
}
