using APIBack.Automation.Models;
using System;
using System.Threading.Tasks;

namespace APIBack.Automation.Interfaces
{
    public interface IClienteRepository
    {
        Task<Guid> GarantirClienteAsync(string telefoneE164, Guid idEstabelecimento);
        Task<string?> ObterTelefoneClienteAsync(Guid idCliente, Guid idEstabelecimento);
        Task<Cliente?> ObterPorIdAsync(Guid idCliente);
    }
}
