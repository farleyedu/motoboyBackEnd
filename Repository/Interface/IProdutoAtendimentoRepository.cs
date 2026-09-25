using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.DTOs.Cardapio;

namespace APIBack.Repository.Interface
{
    public interface IProdutoAtendimentoRepository
    {
        /// <summary>Todos os produtos do estabelecimento que tem atendimento cadastrado. Vazio se a migration nao rodou.</summary>
        Task<IReadOnlyDictionary<Guid, ProdutoAtendimentoDto>> ListAsync(Guid estabelecimentoId);

        /// <summary>Null quando nao ha cadastro (ou a migration nao rodou).</summary>
        Task<ProdutoAtendimentoDto?> GetAsync(Guid estabelecimentoId, Guid produtoId);

        /// <summary>Cria ou substitui. Null quando o produto nao existe neste estabelecimento.</summary>
        Task<ProdutoAtendimentoDto?> UpsertAsync(Guid estabelecimentoId, Guid produtoId, ProdutoAtendimentoDto data);
    }
}
