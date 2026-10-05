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

        /// <summary>
        /// Acha o cliente ativo pelo telefone ou cria um novo (so com telefone/nome); null quando o telefone
        /// e invalido/vazio. Preenche o nome so se o cliente existente ainda nao tiver um. Usado pelo nucleo
        /// de pedido para ligar TODO pedido (de qualquer origem) a um cliente (fundacao de CRM, Fase 3c).
        /// </summary>
        Task<Guid?> ResolverOuCriarAsync(Guid estabelecimentoId, string? telefoneBruto, string? nome);

        /// <summary>Edicao inline do nome (chat do modo comando); null quando o cliente nao existe.</summary>
        Task<ClienteDto?> UpdateNomeAsync(Guid estabelecimentoId, Guid clienteId, string nome);
    }
}
