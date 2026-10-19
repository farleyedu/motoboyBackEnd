using System;
using System.Threading.Tasks;
using APIBack.DTOs.Inteligencia;

namespace APIBack.Service.Interface
{
    public interface IIngredienteService
    {
        Task<IngredienteListaDto> ListAsync(Guid estabelecimentoId, IngredienteFiltro filtro);
        Task<IngredienteDto> GetAsync(Guid estabelecimentoId, Guid id);
        Task<IngredienteSalvoDto> CreateAsync(Guid estabelecimentoId, int userId, IngredienteRequest request);
        Task<IngredienteSalvoDto> UpdateAsync(Guid estabelecimentoId, Guid id, IngredienteRequest request);
        Task<IngredienteDto> AddCustoAsync(Guid estabelecimentoId, int userId, Guid id, CustoRequest request);
        Task<IngredienteCustosDto> ListCustosAsync(Guid estabelecimentoId, Guid id, int page, int pageSize);
        Task<IngredienteDto> SetAtivoAsync(Guid estabelecimentoId, Guid id, bool ativo);
        Task<IngredienteUsosDto> UsosAsync(Guid estabelecimentoId, Guid id);
    }
}
