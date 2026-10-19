using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.DTOs.Inteligencia;
using APIBack.Service;

namespace APIBack.Repository.Interface
{
    /// <summary>Ingredientes (insumos) e histórico de custo. Toda consulta é restrita à loja.</summary>
    public interface IIngredienteRepository
    {
        Task<IngredienteListaDto> ListAsync(Guid estabelecimentoId, string filtro, string? busca, int page, int pageSize);
        Task<IngredienteDto?> GetAsync(Guid estabelecimentoId, Guid id);
        /// <summary>Nomes de todos os ingredientes da loja (ativos ou não), para avisar de nomes parecidos.</summary>
        Task<IReadOnlyList<string>> NamesAsync(Guid estabelecimentoId, Guid? exceto);
        /// <summary>Devolve o já existente quando a chave de idempotência se repete.</summary>
        Task<Guid> CreateAsync(Guid estabelecimentoId, int userId, IngredienteInput input, CustoCalculado? custo);
        /// <summary>Falso = não existe nesta loja. Versão diferente da gravada lança 409.</summary>
        Task<bool> UpdateAsync(Guid estabelecimentoId, Guid id, IngredienteInput input);
        /// <summary>Nulo = ingrediente não existe nesta loja.</summary>
        Task<Guid?> AddCustoAsync(Guid estabelecimentoId, int userId, Guid id, CustoCalculado custo);
        Task<IngredienteCustosDto?> ListCustosAsync(Guid estabelecimentoId, Guid id, int page, int pageSize);
        Task<bool> SetAtivoAsync(Guid estabelecimentoId, Guid id, bool ativo);
    }
}
