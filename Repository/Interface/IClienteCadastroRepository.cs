using System;
using System.Threading.Tasks;
using APIBack.DTOs.Clientes;
using APIBack.Service;

namespace APIBack.Repository.Interface
{
    public interface IClienteCadastroRepository
    {
        Task<ClienteListaDto> ListAsync(Guid estabelecimentoId, string? q, bool incluirInativos, int page, int pageSize);
        Task<ClienteDto?> GetAsync(Guid estabelecimentoId, Guid clienteId);
        /// <summary>Levanta DeliveryDomainException 409 CLIENT_PHONE_TAKEN quando o telefone ja e de outro cliente ativo.</summary>
        Task<ClienteDto> CreateAsync(Guid estabelecimentoId, int actorUserId, ClienteInput input);
        Task<ClienteDto?> UpdateAsync(Guid estabelecimentoId, Guid clienteId, ClienteInput input);
        /// <summary>Exclusao logica; false quando nao existe (ou ja estava inativo).</summary>
        Task<bool> DeactivateAsync(Guid estabelecimentoId, Guid clienteId);
    }
}
