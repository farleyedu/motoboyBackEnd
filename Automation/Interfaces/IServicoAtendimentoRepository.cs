using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.Automation.Models;

namespace APIBack.Automation.Interfaces
{
    public interface IServicoAtendimentoRepository
    {
        Task<ServicoAtendimento?> ObterAbertoAsync(Guid idEstabelecimento, string telefoneE164);
        Task<ServicoAtendimento?> ObterPorConversaAsync(Guid idConversa);
        Task<IReadOnlyCollection<ServicoAtendimento>> ListarPorEstabelecimentoAsync(Guid idEstabelecimento, string? status, int limite);
        Task<Guid> CriarAsync(ServicoAtendimento atendimento);
        Task AtualizarAsync(ServicoAtendimento atendimento);
        Task AtualizarExtrasAsync(Guid id, IReadOnlyDictionary<string, object?> dadosExtras);
        Task AtualizarStatusAsync(Guid id, string status);
        Task ConcluirAsync(Guid id, string status);
    }
}
