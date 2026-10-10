using APIBack.DTOs.Clientes;

namespace APIBack.Repository.Interface;

public interface IClienteEnderecoRepository
{
    Task<IReadOnlyList<ClienteEnderecoDto>> ListAsync(Guid estabelecimentoId, Guid clienteId);
    Task<ClienteEnderecoDto> SaveAsync(Guid estabelecimentoId, Guid clienteId, Guid? id, ClienteEnderecoRequest request);
    Task DeleteAsync(Guid estabelecimentoId, Guid clienteId, Guid id);
    Task CreateSessionAsync(Guid estabelecimentoId, Guid clienteId, string hash, DateTimeOffset expiraEm);
    Task<ClienteSessao?> GetSessionAsync(Guid estabelecimentoId, string hash);
    Task RevokeSessionAsync(Guid estabelecimentoId, string hash);
}
