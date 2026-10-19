using System;
using System.Threading.Tasks;
using APIBack.DTOs.Inteligencia;
using APIBack.Repository.Interface;
using APIBack.Service.Interface;

namespace APIBack.Service
{
    public sealed class IngredienteService : IIngredienteService
    {
        private readonly IIngredienteRepository _repository;

        public IngredienteService(IIngredienteRepository repository)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        }

        public Task<IngredienteListaDto> ListAsync(Guid estabelecimentoId, IngredienteFiltro filtro) =>
            _repository.ListAsync(estabelecimentoId, IngredienteRules.NormalizeFilter(filtro.Filtro), filtro.Q, filtro.Page, filtro.PageSize);

        public async Task<IngredienteDto> GetAsync(Guid estabelecimentoId, Guid id) =>
            await _repository.GetAsync(estabelecimentoId, id) ?? throw NotFound();

        public async Task<IngredienteSalvoDto> CreateAsync(Guid estabelecimentoId, int userId, IngredienteRequest request)
        {
            var input = IngredienteRules.Validate(request, edicao: false);
            var custo = request.Custo == null ? null : IngredienteRules.Calculate(input.Unidade, request.Custo);
            var avisos = IngredienteRules.SimilarNames(input.Nome, await _repository.NamesAsync(estabelecimentoId, null));
            var id = await _repository.CreateAsync(estabelecimentoId, userId, input, custo);
            return new IngredienteSalvoDto { Ingrediente = await GetAsync(estabelecimentoId, id), Avisos = avisos };
        }

        public async Task<IngredienteSalvoDto> UpdateAsync(Guid estabelecimentoId, Guid id, IngredienteRequest request)
        {
            var input = IngredienteRules.Validate(request, edicao: true);
            var current = await GetAsync(estabelecimentoId, id);
            // Trocar a unidade base mudaria o sentido das receitas e do estoque: no primeiro ciclo cria-se outro ingrediente.
            if (!string.Equals(current.Unidade, input.Unidade, StringComparison.Ordinal))
                throw new DeliveryDomainException(422, "INGREDIENTE_UNIDADE_FIXA", "A unidade base não muda depois de criada. Crie um novo ingrediente na unidade certa.", new { campo = "unidade" });
            if (!await _repository.UpdateAsync(estabelecimentoId, id, input)) throw NotFound();
            var avisos = IngredienteRules.SimilarNames(input.Nome, await _repository.NamesAsync(estabelecimentoId, id));
            return new IngredienteSalvoDto { Ingrediente = await GetAsync(estabelecimentoId, id), Avisos = avisos };
        }

        public async Task<IngredienteDto> AddCustoAsync(Guid estabelecimentoId, int userId, Guid id, CustoRequest request)
        {
            var current = await GetAsync(estabelecimentoId, id);
            if (!current.Ativo) throw new DeliveryDomainException(409, "INGREDIENTE_INATIVO", "Reative o ingrediente antes de informar um novo custo.");
            var custo = IngredienteRules.Calculate(current.Unidade, request);
            if (await _repository.AddCustoAsync(estabelecimentoId, userId, id, custo) == null) throw NotFound();
            return await GetAsync(estabelecimentoId, id);
        }

        public async Task<IngredienteCustosDto> ListCustosAsync(Guid estabelecimentoId, Guid id, int page, int pageSize) =>
            await _repository.ListCustosAsync(estabelecimentoId, id, page, pageSize) ?? throw NotFound();

        public async Task<IngredienteDto> SetAtivoAsync(Guid estabelecimentoId, Guid id, bool ativo)
        {
            if (!await _repository.SetAtivoAsync(estabelecimentoId, id, ativo)) throw NotFound();
            return await GetAsync(estabelecimentoId, id);
        }

        public async Task<IngredienteUsosDto> UsosAsync(Guid estabelecimentoId, Guid id)
        {
            await GetAsync(estabelecimentoId, id);
            // Receitas ainda não existem (etapa I4): a tela mostra isso em vez de uma lista vazia que pareça "não usado".
            return new IngredienteUsosDto { ReceitasDisponiveis = false };
        }

        private static DeliveryDomainException NotFound() => new(404, "INGREDIENTE_NAO_ENCONTRADO", "Ingrediente não encontrado.");
    }
}
