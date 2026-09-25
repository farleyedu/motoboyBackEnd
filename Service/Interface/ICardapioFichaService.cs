using System;
using System.Threading.Tasks;
using APIBack.DTOs.Cardapio;

namespace APIBack.Service.Interface
{
    public interface ICardapioFichaService
    {
        /// <summary>Ficha de atendimento do estabelecimento: cardapio + regras + campos de atendimento.</summary>
        Task<FichaAtendimentoDto> GetAsync(Guid estabelecimentoId);

        /// <summary>Campos de atendimento de um produto (vazios quando nao ha cadastro).</summary>
        Task<ProdutoAtendimentoDto> GetAtendimentoAsync(Guid estabelecimentoId, Guid produtoId);

        /// <summary>Salva (substitui) os campos de atendimento de um produto.</summary>
        Task<ProdutoAtendimentoDto> SaveAtendimentoAsync(Guid estabelecimentoId, Guid produtoId, ProdutoAtendimentoDto data);
    }
}
