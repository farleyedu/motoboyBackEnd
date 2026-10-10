using APIBack.DTOs.Clientes;

namespace APIBack.Service.Interface;

public interface IClienteAcessoService
{
    Task<ClienteSessaoDto> AuthenticateAsync(Guid estabelecimentoId, string telefone);
    Task<ClienteSessao> RequireAsync(Guid estabelecimentoId, string? token, string? telefone = null);
    Task<ClienteSessaoDto> ProfileAsync(Guid estabelecimentoId, string? token);
    Task LogoutAsync(Guid estabelecimentoId, string? token);
}
