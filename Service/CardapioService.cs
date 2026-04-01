using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using APIBack.DTOs.Cardapio;
using APIBack.DTOs.Common;
using APIBack.Model.Cardapio;
using APIBack.Repository.Interface;
using APIBack.Service.Interface;

namespace APIBack.Service
{
    public class CardapioService : ICardapioService
    {
        private readonly ICardapioRepository _repository;

        public CardapioService(ICardapioRepository repository)
        {
            _repository = repository;
        }

        public Task<bool> EstabelecimentoTemModuloAtivoAsync(Guid idEstabelecimento, string modulo)
            => _repository.EstabelecimentoTemModuloAtivoAsync(idEstabelecimento, modulo);

        public async Task<CardapioSnapshotDto> ObterSnapshotAsync(Guid idEstabelecimento)
        {
            var categorias = await ListarCategoriasAsync(idEstabelecimento, null, null, 1, 500);
            var grupos = await ListarGruposAsync(idEstabelecimento, null, null, 1, 500);
            var produtos = await ListarProdutosAsync(idEstabelecimento, null, null, null, null, null, 1, 1000);

            return new CardapioSnapshotDto
            {
                Categorias = categorias.Itens.ToList(),
                Grupos = grupos.Itens.ToList(),
                Produtos = produtos.Itens.ToList()
            };
        }

        public async Task<PagedResultDto<CardapioCategoriaDto>> ListarCategoriasAsync(
            Guid idEstabelecimento,
            string? busca,
            bool? ativo,
            int page,
            int pageSize)
        {
            var result = await _repository.ListarCategoriasAsync(
                idEstabelecimento,
                ValidationUtils.TrimToNull(busca),
                ativo,
                ValidationUtils.NormalizePage(page),
                ValidationUtils.NormalizePageSize(pageSize));

            return new PagedResultDto<CardapioCategoriaDto>
            {
                Itens = result.Itens.Select(MapCategoria).ToArray(),
                Total = result.Total
            };
        }

        public async Task<CardapioCategoriaDto?> ObterCategoriaPorIdAsync(Guid idEstabelecimento, Guid id)
        {
            var entity = await _repository.ObterCategoriaPorIdAsync(idEstabelecimento, id);
            return entity == null ? null : MapCategoria(entity);
        }

        public async Task<CardapioCategoriaDto> CriarCategoriaAsync(Guid idEstabelecimento, SalvarCardapioCategoriaRequest request)
        {
            var entity = BuildCategoriaEntity(idEstabelecimento, request);
            entity.Id = await _repository.CriarCategoriaAsync(entity);
            return MapCategoria(entity);
        }

        public async Task<CardapioCategoriaDto> AtualizarCategoriaAsync(Guid idEstabelecimento, Guid id, SalvarCardapioCategoriaRequest request)
        {
            var current = await _repository.ObterCategoriaPorIdAsync(idEstabelecimento, id)
                ?? throw new KeyNotFoundException("Categoria nao encontrada.");

            var entity = BuildCategoriaEntity(idEstabelecimento, request);
            entity.Id = id;
            entity.CreatedAt = current.CreatedAt;

            var updated = await _repository.AtualizarCategoriaAsync(entity);
            if (!updated)
            {
                throw new KeyNotFoundException("Categoria nao encontrada.");
            }

            return MapCategoria(entity);
        }

        public Task<bool> AtualizarCategoriaStatusAsync(Guid idEstabelecimento, Guid id, bool ativo)
            => _repository.AtualizarCategoriaStatusAsync(idEstabelecimento, id, ativo);

        public async Task<bool> ExcluirCategoriaAsync(Guid idEstabelecimento, Guid id)
        {
            if (await _repository.CategoriaTemProdutosAsync(idEstabelecimento, id))
            {
                throw new InvalidOperationException("Nao e possivel excluir uma categoria que possui produtos vinculados.");
            }

            return await _repository.ExcluirCategoriaAsync(idEstabelecimento, id);
        }

        public async Task<PagedResultDto<CardapioGrupoAdicionalDto>> ListarGruposAsync(
            Guid idEstabelecimento,
            string? busca,
            bool? ativo,
            int page,
            int pageSize)
        {
            var result = await _repository.ListarGruposAsync(
                idEstabelecimento,
                ValidationUtils.TrimToNull(busca),
                ativo,
                ValidationUtils.NormalizePage(page),
                ValidationUtils.NormalizePageSize(pageSize));

            return new PagedResultDto<CardapioGrupoAdicionalDto>
            {
                Itens = result.Itens.Select(MapGrupo).ToArray(),
                Total = result.Total
            };
        }

        public async Task<CardapioGrupoAdicionalDto?> ObterGrupoPorIdAsync(Guid idEstabelecimento, Guid id)
        {
            var entity = await _repository.ObterGrupoPorIdAsync(idEstabelecimento, id);
            return entity == null ? null : MapGrupo(entity);
        }

        public async Task<CardapioGrupoAdicionalDto> CriarGrupoAsync(Guid idEstabelecimento, SalvarCardapioGrupoAdicionalRequest request)
        {
            var entity = BuildGrupoEntity(idEstabelecimento, request, null);
            entity.Id = await _repository.CriarGrupoAsync(entity);
            return MapGrupo(entity);
        }

        public async Task<CardapioGrupoAdicionalDto> AtualizarGrupoAsync(Guid idEstabelecimento, Guid id, SalvarCardapioGrupoAdicionalRequest request)
        {
            var current = await _repository.ObterGrupoPorIdAsync(idEstabelecimento, id)
                ?? throw new KeyNotFoundException("Grupo nao encontrado.");

            var entity = BuildGrupoEntity(idEstabelecimento, request, current);
            entity.Id = id;
            entity.CreatedAt = current.CreatedAt;

            var updated = await _repository.AtualizarGrupoAsync(entity);
            if (!updated)
            {
                throw new KeyNotFoundException("Grupo nao encontrado.");
            }

            return MapGrupo(entity);
        }

        public Task<bool> AtualizarGrupoStatusAsync(Guid idEstabelecimento, Guid id, bool ativo)
            => _repository.AtualizarGrupoStatusAsync(idEstabelecimento, id, ativo);

        public async Task<bool> ExcluirGrupoAsync(Guid idEstabelecimento, Guid id)
        {
            if (await _repository.GrupoTemProdutosAsync(idEstabelecimento, id))
            {
                throw new InvalidOperationException("Nao e possivel excluir um grupo adicional vinculado a produtos.");
            }

            return await _repository.ExcluirGrupoAsync(idEstabelecimento, id);
        }

        public async Task<PagedResultDto<CardapioProdutoDto>> ListarProdutosAsync(
            Guid idEstabelecimento,
            string? busca,
            bool? ativo,
            bool? destaque,
            bool? disponivel,
            Guid? categoriaId,
            int page,
            int pageSize)
        {
            var result = await _repository.ListarProdutosAsync(
                idEstabelecimento,
                ValidationUtils.TrimToNull(busca),
                ativo,
                destaque,
                disponivel,
                categoriaId,
                ValidationUtils.NormalizePage(page),
                ValidationUtils.NormalizePageSize(pageSize, 20, 200));

            return new PagedResultDto<CardapioProdutoDto>
            {
                Itens = result.Itens.Select(MapProduto).ToArray(),
                Total = result.Total
            };
        }

        public async Task<CardapioProdutoDto?> ObterProdutoPorIdAsync(Guid idEstabelecimento, Guid id)
        {
            var entity = await _repository.ObterProdutoPorIdAsync(idEstabelecimento, id);
            return entity == null ? null : MapProduto(entity);
        }

        public async Task<CardapioProdutoDto> CriarProdutoAsync(Guid idEstabelecimento, SalvarCardapioProdutoRequest request)
        {
            var entity = await BuildProdutoEntityAsync(idEstabelecimento, request);
            entity.Id = await _repository.CriarProdutoAsync(entity);
            return MapProduto(entity);
        }

        public async Task<CardapioProdutoDto> AtualizarProdutoAsync(Guid idEstabelecimento, Guid id, SalvarCardapioProdutoRequest request)
        {
            var current = await _repository.ObterProdutoPorIdAsync(idEstabelecimento, id)
                ?? throw new KeyNotFoundException("Produto nao encontrado.");

            var entity = await BuildProdutoEntityAsync(idEstabelecimento, request);
            entity.Id = id;
            entity.CreatedAt = current.CreatedAt;

            var updated = await _repository.AtualizarProdutoAsync(entity);
            if (!updated)
            {
                throw new KeyNotFoundException("Produto nao encontrado.");
            }

            return MapProduto(entity);
        }

        public Task<bool> AtualizarProdutoStatusAsync(Guid idEstabelecimento, Guid id, bool ativo)
            => _repository.AtualizarProdutoStatusAsync(idEstabelecimento, id, ativo);

        public Task<bool> AtualizarProdutoDisponibilidadeAsync(Guid idEstabelecimento, Guid id, bool disponivel)
            => _repository.AtualizarProdutoDisponibilidadeAsync(idEstabelecimento, id, disponivel);

        public Task<bool> ExcluirProdutoAsync(Guid idEstabelecimento, Guid id)
            => _repository.ExcluirProdutoAsync(idEstabelecimento, id);

        private static CardapioCategoria BuildCategoriaEntity(Guid idEstabelecimento, SalvarCardapioCategoriaRequest request)
        {
            var errors = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var nome = ValidationUtils.TrimToNull(request.Nome);
            var slug = ValidationUtils.TrimToNull(request.Slug);
            var descricao = ValidationUtils.TrimToNull(request.Descricao);
            var imagemUrl = ValidationUtils.TrimToNull(request.ImagemUrl);

            if (string.IsNullOrWhiteSpace(nome) || nome.Length < 2 || nome.Length > 120)
            {
                ValidationUtils.AddError(errors, "nome", "Nome da categoria deve ter entre 2 e 120 caracteres.");
            }

            if (!string.IsNullOrWhiteSpace(slug) && slug.Length > 160)
            {
                ValidationUtils.AddError(errors, "slug", "Slug da categoria deve ter no maximo 160 caracteres.");
            }

            if (!string.IsNullOrWhiteSpace(descricao) && descricao.Length > 500)
            {
                ValidationUtils.AddError(errors, "descricao", "Descricao da categoria deve ter no maximo 500 caracteres.");
            }

            if (!string.IsNullOrWhiteSpace(imagemUrl) && imagemUrl.Length > 1000)
            {
                ValidationUtils.AddError(errors, "imagemUrl", "Imagem da categoria deve ter no maximo 1000 caracteres.");
            }

            if (request.Ordem <= 0 || request.Ordem > 9999)
            {
                ValidationUtils.AddError(errors, "ordem", "Ordem da categoria deve ser maior que zero e menor ou igual a 9999.");
            }

            ValidationUtils.ThrowIfAny(errors);

            return new CardapioCategoria
            {
                IdEstabelecimento = idEstabelecimento,
                Nome = nome!,
                Slug = slug ?? string.Empty,
                Descricao = descricao,
                ImagemUrl = imagemUrl,
                Ordem = request.Ordem,
                Ativo = request.Ativo
            };
        }

        private static CardapioGrupoAdicional BuildGrupoEntity(
            Guid idEstabelecimento,
            SalvarCardapioGrupoAdicionalRequest request,
            CardapioGrupoAdicional? current)
        {
            var errors = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var nome = ValidationUtils.TrimToNull(request.Nome);
            var descricao = ValidationUtils.TrimToNull(request.Descricao);
            var itensRequest = request.Itens ?? new List<SalvarCardapioGrupoAdicionalItemRequest>();

            if (string.IsNullOrWhiteSpace(nome) || nome.Length < 2 || nome.Length > 120)
            {
                ValidationUtils.AddError(errors, "nome", "Nome do grupo deve ter entre 2 e 120 caracteres.");
            }

            if (!string.IsNullOrWhiteSpace(descricao) && descricao.Length > 500)
            {
                ValidationUtils.AddError(errors, "descricao", "Descricao do grupo deve ter no maximo 500 caracteres.");
            }

            if (request.Ordem <= 0 || request.Ordem > 9999)
            {
                ValidationUtils.AddError(errors, "ordem", "Ordem do grupo deve ser maior que zero e menor ou igual a 9999.");
            }

            if (request.MinSelecionados < 0)
            {
                ValidationUtils.AddError(errors, "minSelecionados", "Quantidade minima deve ser maior ou igual a zero.");
            }

            if (request.MaxSelecionados <= 0)
            {
                ValidationUtils.AddError(errors, "maxSelecionados", "Quantidade maxima deve ser maior que zero.");
            }

            if (request.MinSelecionados > request.MaxSelecionados)
            {
                ValidationUtils.AddError(errors, "minSelecionados", "Quantidade minima nao pode ser maior que a maxima.");
            }

            if (itensRequest.Count == 0)
            {
                ValidationUtils.AddError(errors, "itens", "Grupo adicional precisa ter ao menos um item.");
            }

            var currentIds = current?.Itens.Select(x => x.Id).ToHashSet() ?? new HashSet<Guid>();
            var requestIds = new HashSet<Guid>();
            var itens = new List<CardapioGrupoAdicionalItem>();

            for (var index = 0; index < itensRequest.Count; index++)
            {
                var itemRequest = itensRequest[index];
                var itemId = itemRequest.Id.HasValue && itemRequest.Id.Value != Guid.Empty
                    ? itemRequest.Id.Value
                    : Guid.NewGuid();
                var itemNome = ValidationUtils.TrimToNull(itemRequest.Nome);
                var itemDescricao = ValidationUtils.TrimToNull(itemRequest.Descricao);
                var fieldPrefix = $"itens[{index}]";

                if (!requestIds.Add(itemId))
                {
                    ValidationUtils.AddError(errors, $"{fieldPrefix}.id", "Item adicional repetido no grupo.");
                }

                if (current != null && itemRequest.Id.HasValue && itemRequest.Id.Value != Guid.Empty && !currentIds.Contains(itemRequest.Id.Value))
                {
                    ValidationUtils.AddError(errors, $"{fieldPrefix}.id", "Item adicional informado nao pertence ao grupo.");
                }

                if (string.IsNullOrWhiteSpace(itemNome) || itemNome.Length < 1 || itemNome.Length > 120)
                {
                    ValidationUtils.AddError(errors, $"{fieldPrefix}.nome", "Nome do item deve ter entre 1 e 120 caracteres.");
                }

                if (!string.IsNullOrWhiteSpace(itemDescricao) && itemDescricao.Length > 500)
                {
                    ValidationUtils.AddError(errors, $"{fieldPrefix}.descricao", "Descricao do item deve ter no maximo 500 caracteres.");
                }

                if (itemRequest.Preco < 0)
                {
                    ValidationUtils.AddError(errors, $"{fieldPrefix}.preco", "Preco do item deve ser maior ou igual a zero.");
                }

                if (itemRequest.Ordem <= 0 || itemRequest.Ordem > 9999)
                {
                    ValidationUtils.AddError(errors, $"{fieldPrefix}.ordem", "Ordem do item deve ser maior que zero e menor ou igual a 9999.");
                }

                itens.Add(new CardapioGrupoAdicionalItem
                {
                    Id = itemId,
                    Nome = itemNome ?? string.Empty,
                    Descricao = itemDescricao,
                    Preco = itemRequest.Preco,
                    Ordem = itemRequest.Ordem,
                    Ativo = itemRequest.Ativo
                });
            }

            if (request.MaxSelecionados > itens.Count && itens.Count > 0)
            {
                ValidationUtils.AddError(errors, "maxSelecionados", "Quantidade maxima nao pode ser maior que o total de itens do grupo.");
            }

            ValidationUtils.ThrowIfAny(errors);

            return new CardapioGrupoAdicional
            {
                IdEstabelecimento = idEstabelecimento,
                Nome = nome!,
                Descricao = descricao,
                MinSelecionados = request.MinSelecionados,
                MaxSelecionados = request.MaxSelecionados,
                Ordem = request.Ordem,
                Ativo = request.Ativo,
                Itens = itens
            };
        }

        private async Task<CardapioProduto> BuildProdutoEntityAsync(Guid idEstabelecimento, SalvarCardapioProdutoRequest request)
        {
            var errors = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var nome = ValidationUtils.TrimToNull(request.Nome);
            var slug = ValidationUtils.TrimToNull(request.Slug);
            var descricao = ValidationUtils.TrimToNull(request.Descricao);
            var descricaoCurta = ValidationUtils.TrimToNull(request.DescricaoCurta);
            var imagemUrl = ValidationUtils.TrimToNull(request.ImagemUrl);
            var grupoIds = (request.GrupoIds ?? new List<Guid>())
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToList();

            if (request.CategoriaId == Guid.Empty)
            {
                ValidationUtils.AddError(errors, "categoriaId", "Categoria do produto e obrigatoria.");
            }

            if (string.IsNullOrWhiteSpace(nome) || nome.Length < 2 || nome.Length > 160)
            {
                ValidationUtils.AddError(errors, "nome", "Nome do produto deve ter entre 2 e 160 caracteres.");
            }

            if (!string.IsNullOrWhiteSpace(slug) && slug.Length > 220)
            {
                ValidationUtils.AddError(errors, "slug", "Slug do produto deve ter no maximo 220 caracteres.");
            }

            if (!string.IsNullOrWhiteSpace(descricao) && descricao.Length > 5000)
            {
                ValidationUtils.AddError(errors, "descricao", "Descricao do produto deve ter no maximo 5000 caracteres.");
            }

            if (!string.IsNullOrWhiteSpace(descricaoCurta) && descricaoCurta.Length > 280)
            {
                ValidationUtils.AddError(errors, "descricaoCurta", "Descricao curta do produto deve ter no maximo 280 caracteres.");
            }

            if (!string.IsNullOrWhiteSpace(imagemUrl) && imagemUrl.Length > 1000)
            {
                ValidationUtils.AddError(errors, "imagemUrl", "Imagem do produto deve ter no maximo 1000 caracteres.");
            }

            if (request.PrecoBase < 0)
            {
                ValidationUtils.AddError(errors, "precoBase", "Preco base do produto deve ser maior ou igual a zero.");
            }

            if (request.Ordem <= 0 || request.Ordem > 9999)
            {
                ValidationUtils.AddError(errors, "ordem", "Ordem do produto deve ser maior que zero e menor ou igual a 9999.");
            }

            var categoria = request.CategoriaId == Guid.Empty
                ? null
                : await _repository.ObterCategoriaPorIdAsync(idEstabelecimento, request.CategoriaId);
            if (request.CategoriaId != Guid.Empty && categoria == null)
            {
                ValidationUtils.AddError(errors, "categoriaId", "Categoria informada nao foi encontrada.");
            }

            var grupos = await _repository.ListarGruposPorIdsAsync(idEstabelecimento, grupoIds);
            if (grupoIds.Count != grupos.Count)
            {
                ValidationUtils.AddError(errors, "grupoIds", "Um ou mais grupos adicionais nao foram encontrados.");
            }

            ValidationUtils.ThrowIfAny(errors);

            return new CardapioProduto
            {
                IdEstabelecimento = idEstabelecimento,
                CategoriaId = request.CategoriaId,
                CategoriaNome = categoria?.Nome ?? string.Empty,
                Nome = nome!,
                Slug = slug ?? string.Empty,
                Descricao = descricao,
                DescricaoCurta = descricaoCurta,
                PrecoBase = request.PrecoBase,
                ImagemUrl = imagemUrl,
                Ordem = request.Ordem,
                Ativo = request.Ativo,
                Destaque = request.Destaque,
                Disponivel = request.Disponivel,
                PublicoWeb = request.PublicoWeb,
                GrupoIds = grupoIds
            };
        }

        private static CardapioCategoriaDto MapCategoria(CardapioCategoria entity)
        {
            return new CardapioCategoriaDto
            {
                Id = entity.Id,
                EstabelecimentoId = entity.IdEstabelecimento,
                Nome = entity.Nome,
                Slug = entity.Slug,
                Descricao = entity.Descricao,
                ImagemUrl = entity.ImagemUrl,
                Ordem = entity.Ordem,
                Ativo = entity.Ativo,
                CreatedAt = entity.CreatedAt,
                UpdatedAt = entity.UpdatedAt
            };
        }

        private static CardapioGrupoAdicionalDto MapGrupo(CardapioGrupoAdicional entity)
        {
            return new CardapioGrupoAdicionalDto
            {
                Id = entity.Id,
                EstabelecimentoId = entity.IdEstabelecimento,
                Nome = entity.Nome,
                Descricao = entity.Descricao,
                MinSelecionados = entity.MinSelecionados,
                MaxSelecionados = entity.MaxSelecionados,
                Ordem = entity.Ordem,
                Ativo = entity.Ativo,
                CreatedAt = entity.CreatedAt,
                UpdatedAt = entity.UpdatedAt,
                Itens = entity.Itens
                    .OrderBy(x => x.Ordem)
                    .ThenBy(x => x.Nome)
                    .Select(item => new CardapioGrupoAdicionalItemDto
                    {
                        Id = item.Id,
                        Nome = item.Nome,
                        Descricao = item.Descricao,
                        Preco = item.Preco,
                        Ordem = item.Ordem,
                        Ativo = item.Ativo
                    })
                    .ToList()
            };
        }

        private static CardapioProdutoDto MapProduto(CardapioProduto entity)
        {
            return new CardapioProdutoDto
            {
                Id = entity.Id,
                EstabelecimentoId = entity.IdEstabelecimento,
                CategoriaId = entity.CategoriaId,
                CategoriaNome = entity.CategoriaNome,
                Nome = entity.Nome,
                Slug = entity.Slug,
                Descricao = entity.Descricao,
                DescricaoCurta = entity.DescricaoCurta,
                PrecoBase = entity.PrecoBase,
                ImagemUrl = entity.ImagemUrl,
                Ordem = entity.Ordem,
                Ativo = entity.Ativo,
                Destaque = entity.Destaque,
                Disponivel = entity.Disponivel,
                PublicoWeb = entity.PublicoWeb,
                CreatedAt = entity.CreatedAt,
                UpdatedAt = entity.UpdatedAt,
                GrupoIds = entity.GrupoIds.ToList()
            };
        }
    }
}
