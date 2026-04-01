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
    public class CardapioContractService : ICardapioContractService
    {
        private const string TipoAdicionalGlobal = "adicional_global";
        private readonly ICardapioRepository _repository;

        public CardapioContractService(ICardapioRepository repository)
        {
            _repository = repository;
        }

        public Task<bool> EstabelecimentoTemModuloAtivoAsync(Guid idEstabelecimento, string modulo)
            => _repository.EstabelecimentoTemModuloAtivoAsync(idEstabelecimento, modulo);

        public async Task<PagedResultDto<CardapioCategoriaContractDto>> ListarCategoriasAsync(
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

            return new PagedResultDto<CardapioCategoriaContractDto>
            {
                Itens = result.Itens.Select(MapCategoria).ToArray(),
                Total = result.Total
            };
        }

        public async Task<CardapioCategoriaContractDto?> ObterCategoriaPorIdAsync(Guid idEstabelecimento, Guid categoriaId)
        {
            var entity = await _repository.ObterCategoriaPorIdAsync(idEstabelecimento, categoriaId);
            return entity == null ? null : MapCategoria(entity);
        }

        public async Task<CardapioCategoriaContractDto> CriarCategoriaAsync(Guid idEstabelecimento, SalvarCardapioCategoriaContractRequest request)
        {
            var entity = BuildCategoriaEntity(idEstabelecimento, request);
            entity.Id = await _repository.CriarCategoriaAsync(entity);
            return await ObterCategoriaObrigatoriaAsync(idEstabelecimento, entity.Id, "Categoria nao encontrada apos criacao.");
        }

        public async Task<CardapioCategoriaContractDto> AtualizarCategoriaAsync(Guid idEstabelecimento, Guid categoriaId, SalvarCardapioCategoriaContractRequest request)
        {
            _ = await _repository.ObterCategoriaPorIdAsync(idEstabelecimento, categoriaId)
                ?? throw new KeyNotFoundException("Categoria nao encontrada.");

            var entity = BuildCategoriaEntity(idEstabelecimento, request);
            entity.Id = categoriaId;

            var updated = await _repository.AtualizarCategoriaAsync(entity);
            if (!updated)
            {
                throw new KeyNotFoundException("Categoria nao encontrada.");
            }

            return await ObterCategoriaObrigatoriaAsync(idEstabelecimento, categoriaId, "Categoria nao encontrada apos atualizacao.");
        }

        public Task<bool> AtualizarCategoriaStatusAsync(Guid idEstabelecimento, Guid categoriaId, bool ativo)
            => _repository.AtualizarCategoriaStatusAsync(idEstabelecimento, categoriaId, ativo);

        public async Task<bool> ExcluirCategoriaAsync(Guid idEstabelecimento, Guid categoriaId)
        {
            if (await _repository.CategoriaTemProdutosAsync(idEstabelecimento, categoriaId))
            {
                throw new InvalidOperationException("Nao e possivel excluir uma categoria que possui produtos vinculados.");
            }

            return await _repository.ExcluirCategoriaAsync(idEstabelecimento, categoriaId);
        }

        public async Task<PagedResultDto<CardapioAdicionalDto>> ListarAdicionaisAsync(
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
                ValidationUtils.NormalizePageSize(pageSize),
                TipoAdicionalGlobal);

            return new PagedResultDto<CardapioAdicionalDto>
            {
                Itens = result.Itens.Select(MapAdicional).ToArray(),
                Total = result.Total
            };
        }

        public async Task<CardapioAdicionalDto?> ObterAdicionalPorIdAsync(Guid idEstabelecimento, Guid adicionalId)
        {
            var entity = await _repository.ObterGrupoPorIdAsync(idEstabelecimento, adicionalId, TipoAdicionalGlobal);
            return entity == null ? null : MapAdicional(entity);
        }

        public async Task<CardapioAdicionalDto> CriarAdicionalAsync(Guid idEstabelecimento, SalvarCardapioAdicionalRequest request)
        {
            var entity = BuildAdicionalEntity(idEstabelecimento, request, null);
            entity.Id = await _repository.CriarGrupoAsync(entity);
            return await ObterAdicionalObrigatorioAsync(idEstabelecimento, entity.Id, "Adicional nao encontrado apos criacao.");
        }

        public async Task<CardapioAdicionalDto> AtualizarAdicionalAsync(Guid idEstabelecimento, Guid adicionalId, SalvarCardapioAdicionalRequest request)
        {
            var current = await _repository.ObterGrupoPorIdAsync(idEstabelecimento, adicionalId, TipoAdicionalGlobal)
                ?? throw new KeyNotFoundException("Adicional nao encontrado.");

            var entity = BuildAdicionalEntity(idEstabelecimento, request, current);
            entity.Id = adicionalId;

            var updated = await _repository.AtualizarGrupoAsync(entity);
            if (!updated)
            {
                throw new KeyNotFoundException("Adicional nao encontrado.");
            }

            return await ObterAdicionalObrigatorioAsync(idEstabelecimento, adicionalId, "Adicional nao encontrado apos atualizacao.");
        }

        public Task<bool> AtualizarAdicionalStatusAsync(Guid idEstabelecimento, Guid adicionalId, bool ativo)
            => _repository.AtualizarGrupoStatusAsync(idEstabelecimento, adicionalId, ativo);

        public async Task<bool> ExcluirAdicionalAsync(Guid idEstabelecimento, Guid adicionalId)
        {
            if (await _repository.GrupoTemProdutosAsync(idEstabelecimento, adicionalId))
            {
                throw new InvalidOperationException("Nao e possivel excluir um adicional vinculado a produtos.");
            }

            return await _repository.ExcluirGrupoAsync(idEstabelecimento, adicionalId);
        }

        public async Task<PagedResultDto<CardapioProdutoContractDto>> ListarProdutosAsync(
            Guid idEstabelecimento,
            string? busca,
            bool? ativo,
            Guid? categoriaId,
            int page,
            int pageSize)
        {
            var result = await _repository.ListarProdutosAsync(
                idEstabelecimento,
                ValidationUtils.TrimToNull(busca),
                ativo,
                null,
                null,
                categoriaId,
                ValidationUtils.NormalizePage(page),
                ValidationUtils.NormalizePageSize(pageSize, 20, 200));

            return new PagedResultDto<CardapioProdutoContractDto>
            {
                Itens = result.Itens.Select(MapProdutoInterno).ToArray(),
                Total = result.Total
            };
        }

        public async Task<CardapioProdutoContractDto?> ObterProdutoPorIdAsync(Guid idEstabelecimento, Guid produtoId)
        {
            var entity = await _repository.ObterProdutoPorIdAsync(idEstabelecimento, produtoId);
            return entity == null ? null : MapProdutoInterno(entity);
        }

        public async Task<CardapioProdutoContractDto> CriarProdutoAsync(Guid idEstabelecimento, SalvarCardapioProdutoContractRequest request)
        {
            var entity = await BuildProdutoEntityAsync(idEstabelecimento, request);
            entity.Id = await _repository.CriarProdutoAsync(entity);
            return await ObterProdutoObrigatorioAsync(idEstabelecimento, entity.Id, "Produto nao encontrado apos criacao.");
        }

        public async Task<CardapioProdutoContractDto> AtualizarProdutoAsync(Guid idEstabelecimento, Guid produtoId, SalvarCardapioProdutoContractRequest request)
        {
            _ = await _repository.ObterProdutoPorIdAsync(idEstabelecimento, produtoId)
                ?? throw new KeyNotFoundException("Produto nao encontrado.");

            var entity = await BuildProdutoEntityAsync(idEstabelecimento, request);
            entity.Id = produtoId;

            var updated = await _repository.AtualizarProdutoAsync(entity);
            if (!updated)
            {
                throw new KeyNotFoundException("Produto nao encontrado.");
            }

            return await ObterProdutoObrigatorioAsync(idEstabelecimento, produtoId, "Produto nao encontrado apos atualizacao.");
        }

        public Task<bool> AtualizarProdutoStatusAsync(Guid idEstabelecimento, Guid produtoId, bool ativo)
            => _repository.AtualizarProdutoStatusAsync(idEstabelecimento, produtoId, ativo);

        public Task<bool> AtualizarProdutoDisponibilidadeAsync(Guid idEstabelecimento, Guid produtoId, bool disponivel)
            => _repository.AtualizarProdutoDisponibilidadeAsync(idEstabelecimento, produtoId, disponivel);

        public Task<bool> ExcluirProdutoAsync(Guid idEstabelecimento, Guid produtoId)
            => _repository.ExcluirProdutoAsync(idEstabelecimento, produtoId);

        public async Task<CardapioWebConfigDto> ObterWebConfigAsync(Guid idEstabelecimento)
        {
            var estabelecimento = await ObterEstabelecimentoObrigatorioAsync(idEstabelecimento);
            var config = await _repository.ObterWebConfigAsync(idEstabelecimento);
            return MapWebConfig(estabelecimento, config);
        }

        public async Task<CardapioWebConfigDto> SalvarWebConfigAsync(Guid idEstabelecimento, SalvarCardapioWebConfigRequest request)
        {
            var estabelecimento = await ObterEstabelecimentoObrigatorioAsync(idEstabelecimento);
            var current = await _repository.ObterWebConfigAsync(idEstabelecimento);
            var entity = BuildWebConfigEntity(idEstabelecimento, request, current);
            var saved = await _repository.UpsertWebConfigAsync(entity);
            return MapWebConfig(estabelecimento, saved);
        }

        public async Task<CardapioWebConfigDto> AtualizarPublicacaoAsync(Guid idEstabelecimento, bool publicado)
        {
            var estabelecimento = await ObterEstabelecimentoObrigatorioAsync(idEstabelecimento);
            var current = await _repository.ObterWebConfigAsync(idEstabelecimento)
                ?? new CardapioWebConfig
                {
                    IdEstabelecimento = idEstabelecimento,
                    NomePublico = estabelecimento.NomePublico ?? estabelecimento.NomeFantasia,
                    Emoji = estabelecimento.Emoji,
                    LogoUrl = estabelecimento.UrlLogo,
                    BannerUrl = estabelecimento.BannerUrl,
                    Rating = estabelecimento.Rating,
                    ReviewCount = estabelecimento.ReviewCount,
                    DeliveryTimeLabel = estabelecimento.DeliveryTimeLabel,
                    DeliveryFeeValue = estabelecimento.DeliveryFeeValue ?? estabelecimento.TaxaEntregaFixa,
                    DeliveryFeeLabel = estabelecimento.DeliveryFeeLabel,
                    ServiceFeeValue = estabelecimento.ServiceFeeValue,
                    AceitaEntrega = estabelecimento.AceitaEntrega,
                    AceitaRetirada = estabelecimento.AceitaRetirada
                };

            current.Publicado = publicado;
            var saved = await _repository.UpsertWebConfigAsync(current);
            return MapWebConfig(estabelecimento, saved);
        }

        public async Task<CardapioPublicoSnapshotDto> ObterSnapshotPublicoAsync(Guid idEstabelecimento)
        {
            var estabelecimento = await ObterEstabelecimentoObrigatorioAsync(idEstabelecimento);
            EnsureCardapioWebDisponivel(estabelecimento);

            var categorias = await _repository.ListarCategoriasAsync(idEstabelecimento, null, true, 1, 500);
            var produtos = await _repository.ListarProdutosPublicosAsync(idEstabelecimento, null);
            var categoriaIdsAtivas = categorias.Itens.Select(item => item.Id).ToHashSet();
            var ordemCategoria = categorias.Itens.ToDictionary(item => item.Id, item => item.Ordem);

            return new CardapioPublicoSnapshotDto
            {
                EstabelecimentoId = estabelecimento.Id,
                NomePublico = estabelecimento.NomePublico ?? estabelecimento.NomeFantasia,
                Emoji = estabelecimento.Emoji,
                LogoUrl = estabelecimento.UrlLogo,
                BannerUrl = estabelecimento.BannerUrl,
                Rating = estabelecimento.Rating,
                ReviewCount = estabelecimento.ReviewCount,
                DeliveryTimeLabel = estabelecimento.DeliveryTimeLabel,
                DeliveryFeeValue = estabelecimento.DeliveryFeeValue ?? estabelecimento.TaxaEntregaFixa,
                DeliveryFeeLabel = ResolveDeliveryFeeLabel(estabelecimento.DeliveryFeeLabel, estabelecimento.DeliveryFeeValue ?? estabelecimento.TaxaEntregaFixa),
                ServiceFeeValue = estabelecimento.ServiceFeeValue,
                AceitaEntrega = estabelecimento.AceitaEntrega,
                AceitaRetirada = estabelecimento.AceitaRetirada,
                Categorias = categorias.Itens
                    .OrderBy(item => item.Ordem)
                    .ThenBy(item => item.Nome)
                    .Select(MapCategoriaPublica)
                    .ToList(),
                Produtos = produtos
                    .Where(item => categoriaIdsAtivas.Contains(item.CategoriaId))
                    .OrderBy(item => ordemCategoria.TryGetValue(item.CategoriaId, out var ordem) ? ordem : int.MaxValue)
                    .ThenBy(item => item.Ordem)
                    .ThenBy(item => item.Nome)
                    .Select(MapProdutoPublico)
                    .ToList()
            };
        }

        private async Task<CardapioEstabelecimentoPublico> ObterEstabelecimentoObrigatorioAsync(Guid idEstabelecimento)
        {
            var estabelecimento = await _repository.ObterEstabelecimentoPublicoAsync(idEstabelecimento, null);
            return estabelecimento ?? throw new KeyNotFoundException("Estabelecimento nao encontrado.");
        }

        private async Task<CardapioCategoriaContractDto> ObterCategoriaObrigatoriaAsync(Guid idEstabelecimento, Guid categoriaId, string message)
        {
            var entity = await _repository.ObterCategoriaPorIdAsync(idEstabelecimento, categoriaId);
            return entity == null ? throw new KeyNotFoundException(message) : MapCategoria(entity);
        }

        private async Task<CardapioAdicionalDto> ObterAdicionalObrigatorioAsync(Guid idEstabelecimento, Guid adicionalId, string message)
        {
            var entity = await _repository.ObterGrupoPorIdAsync(idEstabelecimento, adicionalId, TipoAdicionalGlobal);
            return entity == null ? throw new KeyNotFoundException(message) : MapAdicional(entity);
        }

        private async Task<CardapioProdutoContractDto> ObterProdutoObrigatorioAsync(Guid idEstabelecimento, Guid produtoId, string message)
        {
            var entity = await _repository.ObterProdutoPorIdAsync(idEstabelecimento, produtoId);
            return entity == null ? throw new KeyNotFoundException(message) : MapProdutoInterno(entity);
        }

        private static CardapioCategoria BuildCategoriaEntity(Guid idEstabelecimento, SalvarCardapioCategoriaContractRequest request)
        {
            var errors = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var nome = ValidationUtils.TrimToNull(request.Nome);
            var slug = ValidationUtils.TrimToNull(request.Slug);
            var emoji = ValidationUtils.TrimToNull(request.Emoji);
            var descricao = ValidationUtils.TrimToNull(request.Descricao);

            if (string.IsNullOrWhiteSpace(nome) || nome.Length < 2 || nome.Length > 120)
            {
                ValidationUtils.AddError(errors, "nome", "Nome da categoria deve ter entre 2 e 120 caracteres.");
            }

            if (!string.IsNullOrWhiteSpace(slug) && slug.Length > 160)
            {
                ValidationUtils.AddError(errors, "slug", "Slug da categoria deve ter no maximo 160 caracteres.");
            }

            if (!string.IsNullOrWhiteSpace(emoji) && emoji.Length > 32)
            {
                ValidationUtils.AddError(errors, "emoji", "Emoji da categoria deve ter no maximo 32 caracteres.");
            }

            if (!string.IsNullOrWhiteSpace(descricao) && descricao.Length > 500)
            {
                ValidationUtils.AddError(errors, "descricao", "Descricao da categoria deve ter no maximo 500 caracteres.");
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
                Emoji = emoji,
                Descricao = descricao,
                Ordem = request.Ordem,
                Ativo = request.Ativo
            };
        }

        private static CardapioGrupoAdicional BuildAdicionalEntity(
            Guid idEstabelecimento,
            SalvarCardapioAdicionalRequest request,
            CardapioGrupoAdicional? current)
        {
            var errors = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var nome = ValidationUtils.TrimToNull(request.Nome);
            var descricao = ValidationUtils.TrimToNull(request.Descricao);

            if (string.IsNullOrWhiteSpace(nome) || nome.Length < 2 || nome.Length > 120)
            {
                ValidationUtils.AddError(errors, "nome", "Nome do adicional deve ter entre 2 e 120 caracteres.");
            }

            if (!string.IsNullOrWhiteSpace(descricao) && descricao.Length > 500)
            {
                ValidationUtils.AddError(errors, "descricao", "Descricao do adicional deve ter no maximo 500 caracteres.");
            }

            if (request.Preco < 0)
            {
                ValidationUtils.AddError(errors, "preco", "Preco do adicional deve ser maior ou igual a zero.");
            }

            if (request.Ordem <= 0 || request.Ordem > 9999)
            {
                ValidationUtils.AddError(errors, "ordem", "Ordem do adicional deve ser maior que zero e menor ou igual a 9999.");
            }

            ValidationUtils.ThrowIfAny(errors);

            var itemAtual = current?.Itens
                .OrderBy(item => item.Ordem)
                .ThenBy(item => item.Nome)
                .FirstOrDefault();

            return new CardapioGrupoAdicional
            {
                IdEstabelecimento = idEstabelecimento,
                Nome = nome!,
                Tipo = TipoAdicionalGlobal,
                Descricao = descricao,
                MinSelecionados = 0,
                MaxSelecionados = 1,
                Ordem = request.Ordem,
                Ativo = request.Ativo,
                Itens = new List<CardapioGrupoAdicionalItem>
                {
                    new()
                    {
                        Id = itemAtual?.Id ?? Guid.NewGuid(),
                        Nome = nome!,
                        Descricao = descricao,
                        Preco = request.Preco,
                        Ordem = request.Ordem,
                        Ativo = request.Ativo
                    }
                }
            };
        }

        private async Task<CardapioProduto> BuildProdutoEntityAsync(Guid idEstabelecimento, SalvarCardapioProdutoContractRequest request)
        {
            var errors = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var nome = ValidationUtils.TrimToNull(request.Nome);
            var descricao = ValidationUtils.TrimToNull(request.Descricao);
            var badgeDesconto = ValidationUtils.TrimToNull(request.BadgeDesconto);
            var emoji = ValidationUtils.TrimToNull(request.Emoji);
            var imageUrl = ValidationUtils.TrimToNull(request.ImageUrl);
            var extrasTitulo = ValidationUtils.TrimToNull(request.ExtrasConfig?.Titulo);
            var extrasSubtitulo = ValidationUtils.TrimToNull(request.ExtrasConfig?.Subtitulo);
            var adicionais = request.Adicionais ?? new List<CardapioProdutoAdicionalLinkDto>();
            var adicionalIds = adicionais
                .Where(item => item.AdicionalId != Guid.Empty)
                .Select(item => item.AdicionalId)
                .Distinct()
                .ToArray();

            if (request.CategoriaId == Guid.Empty)
            {
                ValidationUtils.AddError(errors, "categoriaId", "Categoria do produto e obrigatoria.");
            }

            if (string.IsNullOrWhiteSpace(nome) || nome.Length < 2 || nome.Length > 160)
            {
                ValidationUtils.AddError(errors, "nome", "Nome do produto deve ter entre 2 e 160 caracteres.");
            }

            if (!string.IsNullOrWhiteSpace(descricao) && descricao.Length > 5000)
            {
                ValidationUtils.AddError(errors, "descricao", "Descricao do produto deve ter no maximo 5000 caracteres.");
            }

            if (request.Preco < 0)
            {
                ValidationUtils.AddError(errors, "preco", "Preco do produto deve ser maior ou igual a zero.");
            }

            if (request.PrecoDe.HasValue && request.PrecoDe.Value < 0)
            {
                ValidationUtils.AddError(errors, "precoDe", "Preco de referencia deve ser maior ou igual a zero.");
            }

            if (!string.IsNullOrWhiteSpace(badgeDesconto) && badgeDesconto.Length > 80)
            {
                ValidationUtils.AddError(errors, "badgeDesconto", "Badge de desconto deve ter no maximo 80 caracteres.");
            }

            if (!string.IsNullOrWhiteSpace(emoji) && emoji.Length > 32)
            {
                ValidationUtils.AddError(errors, "emoji", "Emoji do produto deve ter no maximo 32 caracteres.");
            }

            if (!string.IsNullOrWhiteSpace(imageUrl) && imageUrl.Length > 1000)
            {
                ValidationUtils.AddError(errors, "imageUrl", "URL da imagem deve ter no maximo 1000 caracteres.");
            }

            if (!string.IsNullOrWhiteSpace(extrasTitulo) && extrasTitulo.Length > 160)
            {
                ValidationUtils.AddError(errors, "extrasConfig.titulo", "Titulo dos extras deve ter no maximo 160 caracteres.");
            }

            if (!string.IsNullOrWhiteSpace(extrasSubtitulo) && extrasSubtitulo.Length > 280)
            {
                ValidationUtils.AddError(errors, "extrasConfig.subtitulo", "Subtitulo dos extras deve ter no maximo 280 caracteres.");
            }

            if (request.Ordem <= 0 || request.Ordem > 9999)
            {
                ValidationUtils.AddError(errors, "ordem", "Ordem do produto deve ser maior que zero e menor ou igual a 9999.");
            }

            for (var index = 0; index < adicionais.Count; index++)
            {
                if (adicionais[index].PrecoOverride.HasValue)
                {
                    ValidationUtils.AddError(errors, $"adicionais[{index}].precoOverride", "Preco override ainda nao e suportado para adicionais globais.");
                }
            }

            if (request.OptionGroups != null && request.OptionGroups.Count > 0)
            {
                ValidationUtils.AddError(errors, "optionGroups", "Option groups ainda nao sao suportados nesta fase.");
            }

            var categoria = request.CategoriaId == Guid.Empty
                ? null
                : await _repository.ObterCategoriaPorIdAsync(idEstabelecimento, request.CategoriaId);
            if (request.CategoriaId != Guid.Empty && categoria == null)
            {
                ValidationUtils.AddError(errors, "categoriaId", "Categoria informada nao foi encontrada.");
            }

            var adicionaisValidos = await _repository.ListarGruposPorIdsAsync(idEstabelecimento, adicionalIds, TipoAdicionalGlobal);
            if (adicionalIds.Length != adicionaisValidos.Count)
            {
                ValidationUtils.AddError(errors, "adicionais", "Um ou mais adicionais nao foram encontrados.");
            }

            ValidationUtils.ThrowIfAny(errors);

            return new CardapioProduto
            {
                IdEstabelecimento = idEstabelecimento,
                CategoriaId = request.CategoriaId,
                CategoriaNome = categoria?.Nome ?? string.Empty,
                Nome = nome!,
                Slug = string.Empty,
                Emoji = emoji,
                Descricao = descricao,
                DescricaoCurta = null,
                PrecoBase = request.Preco,
                PrecoDe = request.PrecoDe,
                BadgeDesconto = badgeDesconto,
                IsClub = request.IsClub,
                ImagemUrl = imageUrl,
                EcoFriendly = request.EcoFriendly,
                ExtrasTitulo = extrasTitulo,
                ExtrasSubtitulo = extrasSubtitulo,
                Ordem = request.Ordem,
                Ativo = request.Ativo,
                Destaque = false,
                Disponivel = request.Disponivel,
                PublicoWeb = request.PublicadoWeb,
                GrupoIds = adicionaisValidos.Select(item => item.Id).ToList()
            };
        }

        private static CardapioWebConfig BuildWebConfigEntity(
            Guid idEstabelecimento,
            SalvarCardapioWebConfigRequest request,
            CardapioWebConfig? current)
        {
            var errors = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var nomePublico = ValidationUtils.TrimToNull(request.NomePublico);
            var emoji = ValidationUtils.TrimToNull(request.Emoji);
            var logoUrl = ValidationUtils.TrimToNull(request.LogoUrl);
            var bannerUrl = ValidationUtils.TrimToNull(request.BannerUrl);
            var deliveryTimeLabel = ValidationUtils.TrimToNull(request.DeliveryTimeLabel);
            var deliveryFeeLabel = ValidationUtils.TrimToNull(request.DeliveryFeeLabel);

            if (!string.IsNullOrWhiteSpace(nomePublico) && nomePublico.Length > 160)
            {
                ValidationUtils.AddError(errors, "nomePublico", "Nome publico deve ter no maximo 160 caracteres.");
            }

            if (!string.IsNullOrWhiteSpace(emoji) && emoji.Length > 32)
            {
                ValidationUtils.AddError(errors, "emoji", "Emoji deve ter no maximo 32 caracteres.");
            }

            if (!string.IsNullOrWhiteSpace(logoUrl) && logoUrl.Length > 1000)
            {
                ValidationUtils.AddError(errors, "logoUrl", "Logo URL deve ter no maximo 1000 caracteres.");
            }

            if (!string.IsNullOrWhiteSpace(bannerUrl) && bannerUrl.Length > 1000)
            {
                ValidationUtils.AddError(errors, "bannerUrl", "Banner URL deve ter no maximo 1000 caracteres.");
            }

            if (!string.IsNullOrWhiteSpace(deliveryTimeLabel) && deliveryTimeLabel.Length > 120)
            {
                ValidationUtils.AddError(errors, "deliveryTimeLabel", "Label de prazo deve ter no maximo 120 caracteres.");
            }

            if (!string.IsNullOrWhiteSpace(deliveryFeeLabel) && deliveryFeeLabel.Length > 120)
            {
                ValidationUtils.AddError(errors, "deliveryFeeLabel", "Label de taxa deve ter no maximo 120 caracteres.");
            }

            if (request.Rating.HasValue && (request.Rating.Value < 0 || request.Rating.Value > 5))
            {
                ValidationUtils.AddError(errors, "rating", "Rating deve estar entre 0 e 5.");
            }

            if (request.ReviewCount.HasValue && request.ReviewCount.Value < 0)
            {
                ValidationUtils.AddError(errors, "reviewCount", "Quantidade de reviews deve ser maior ou igual a zero.");
            }

            if (request.DeliveryFeeValue < 0)
            {
                ValidationUtils.AddError(errors, "deliveryFeeValue", "Taxa de entrega deve ser maior ou igual a zero.");
            }

            if (request.ServiceFeeValue < 0)
            {
                ValidationUtils.AddError(errors, "serviceFeeValue", "Taxa de servico deve ser maior ou igual a zero.");
            }

            ValidationUtils.ThrowIfAny(errors);

            return new CardapioWebConfig
            {
                IdEstabelecimento = idEstabelecimento,
                NomePublico = nomePublico,
                Emoji = emoji,
                LogoUrl = logoUrl,
                BannerUrl = bannerUrl,
                Rating = request.Rating,
                ReviewCount = request.ReviewCount,
                DeliveryTimeLabel = deliveryTimeLabel,
                DeliveryFeeValue = request.DeliveryFeeValue,
                DeliveryFeeLabel = deliveryFeeLabel,
                ServiceFeeValue = request.ServiceFeeValue,
                AceitaEntrega = request.AceitaEntrega,
                AceitaRetirada = request.AceitaRetirada,
                Publicado = current?.Publicado ?? false
            };
        }

        private static void EnsureCardapioWebDisponivel(CardapioEstabelecimentoPublico estabelecimento)
        {
            var modulos = (estabelecimento.ModulosAtivosRaw ?? Array.Empty<string>())
                .Select(ValidationUtils.NormalizeToken)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (!modulos.Contains("cardapio") || !modulos.Contains("cardapioweb"))
            {
                throw new KeyNotFoundException("Cardapio nao disponivel para este estabelecimento.");
            }

            if (!estabelecimento.Publicado)
            {
                throw new KeyNotFoundException("Cardapio ainda nao foi publicado.");
            }
        }

        private static CardapioCategoriaContractDto MapCategoria(CardapioCategoria entity)
        {
            return new CardapioCategoriaContractDto
            {
                Id = entity.Id,
                Slug = entity.Slug,
                Nome = entity.Nome,
                Emoji = entity.Emoji,
                Descricao = entity.Descricao,
                Ordem = entity.Ordem,
                Ativo = entity.Ativo
            };
        }

        private static CardapioPublicoCategoriaContractDto MapCategoriaPublica(CardapioCategoria entity)
        {
            return new CardapioPublicoCategoriaContractDto
            {
                Id = entity.Id,
                Slug = entity.Slug,
                Nome = entity.Nome,
                Emoji = entity.Emoji,
                Descricao = entity.Descricao,
                Ordem = entity.Ordem,
                Ativo = entity.Ativo
            };
        }

        private static CardapioAdicionalDto MapAdicional(CardapioGrupoAdicional entity)
        {
            var item = entity.Itens
                .OrderBy(x => x.Ordem)
                .ThenBy(x => x.Nome)
                .FirstOrDefault();

            return new CardapioAdicionalDto
            {
                Id = entity.Id,
                Nome = entity.Nome,
                Descricao = entity.Descricao ?? item?.Descricao,
                Preco = item?.Preco ?? 0,
                Ativo = entity.Ativo,
                Ordem = entity.Ordem
            };
        }

        private static CardapioProdutoContractDto MapProdutoInterno(CardapioProduto entity)
        {
            return new CardapioProdutoContractDto
            {
                Id = entity.Id,
                CategoriaId = entity.CategoriaId,
                Nome = entity.Nome,
                Descricao = entity.Descricao,
                Preco = entity.PrecoBase,
                PrecoDe = entity.PrecoDe,
                BadgeDesconto = entity.BadgeDesconto,
                IsClub = entity.IsClub,
                Emoji = entity.Emoji,
                ImageUrl = entity.ImagemUrl,
                EcoFriendly = entity.EcoFriendly,
                Ativo = entity.Ativo,
                Ordem = entity.Ordem,
                Disponivel = entity.Disponivel,
                PublicadoWeb = entity.PublicoWeb,
                Adicionais = entity.GrupoIds
                    .Distinct()
                    .Select(grupoId => new CardapioProdutoAdicionalLinkDto
                    {
                        AdicionalId = grupoId,
                        PrecoOverride = null
                    })
                    .ToList(),
                ExtrasConfig = new CardapioExtrasConfigDto
                {
                    Titulo = entity.ExtrasTitulo,
                    Subtitulo = entity.ExtrasSubtitulo
                },
                OptionGroups = new List<CardapioOptionGroupDto>()
            };
        }

        private static CardapioPublicoProdutoContractDto MapProdutoPublico(CardapioProduto entity)
        {
            return new CardapioPublicoProdutoContractDto
            {
                Id = entity.Id,
                CategoriaId = entity.CategoriaId,
                Nome = entity.Nome,
                Descricao = entity.Descricao,
                Preco = entity.PrecoBase,
                PrecoDe = entity.PrecoDe,
                BadgeDesconto = entity.BadgeDesconto,
                IsClub = entity.IsClub,
                Emoji = entity.Emoji,
                ImageUrl = entity.ImagemUrl,
                EcoFriendly = entity.EcoFriendly,
                Ativo = entity.Ativo,
                Ordem = entity.Ordem,
                Adicionais = MapAdicionaisPublicos(entity),
                ExtrasConfig = new CardapioExtrasConfigDto
                {
                    Titulo = entity.ExtrasTitulo,
                    Subtitulo = entity.ExtrasSubtitulo
                },
                OptionGroups = new List<CardapioOptionGroupDto>()
            };
        }

        private static List<CardapioPublicoAdicionalDto> MapAdicionaisPublicos(CardapioProduto entity)
        {
            var itens = new List<CardapioPublicoAdicionalDto>();

            foreach (var grupo in entity.Grupos
                .Where(grupo => string.Equals(grupo.Tipo, TipoAdicionalGlobal, StringComparison.OrdinalIgnoreCase))
                .OrderBy(grupo => grupo.Ordem)
                .ThenBy(grupo => grupo.Nome))
            {
                var item = grupo.Itens
                    .Where(x => x.Ativo)
                    .OrderBy(x => x.Ordem)
                    .ThenBy(x => x.Nome)
                    .FirstOrDefault();

                if (item == null)
                {
                    continue;
                }

                itens.Add(new CardapioPublicoAdicionalDto
                {
                    Id = $"{entity.Id:N}:{grupo.Id:N}",
                    AdicionalId = grupo.Id,
                    Nome = item.Nome,
                    Descricao = item.Descricao ?? grupo.Descricao,
                    Preco = item.Preco
                });
            }

            return itens;
        }

        private static CardapioWebConfigDto MapWebConfig(CardapioEstabelecimentoPublico estabelecimento, CardapioWebConfig? config)
        {
            var deliveryFeeValue = config?.DeliveryFeeValue ?? estabelecimento.DeliveryFeeValue ?? estabelecimento.TaxaEntregaFixa;

            return new CardapioWebConfigDto
            {
                EstabelecimentoId = estabelecimento.Id,
                NomePublico = config?.NomePublico ?? estabelecimento.NomePublico ?? estabelecimento.NomeFantasia,
                Emoji = config?.Emoji ?? estabelecimento.Emoji,
                LogoUrl = config?.LogoUrl ?? estabelecimento.UrlLogo,
                BannerUrl = config?.BannerUrl ?? estabelecimento.BannerUrl,
                Rating = config?.Rating ?? estabelecimento.Rating,
                ReviewCount = config?.ReviewCount ?? estabelecimento.ReviewCount,
                DeliveryTimeLabel = config?.DeliveryTimeLabel ?? estabelecimento.DeliveryTimeLabel,
                DeliveryFeeValue = deliveryFeeValue,
                DeliveryFeeLabel = ResolveDeliveryFeeLabel(config?.DeliveryFeeLabel ?? estabelecimento.DeliveryFeeLabel, deliveryFeeValue),
                ServiceFeeValue = config?.ServiceFeeValue ?? estabelecimento.ServiceFeeValue,
                AceitaEntrega = config?.AceitaEntrega ?? estabelecimento.AceitaEntrega,
                AceitaRetirada = config?.AceitaRetirada ?? estabelecimento.AceitaRetirada,
                Publicado = config?.Publicado ?? estabelecimento.Publicado
            };
        }

        private static string? ResolveDeliveryFeeLabel(string? currentLabel, decimal deliveryFeeValue)
        {
            var normalized = ValidationUtils.TrimToNull(currentLabel);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                return normalized;
            }

            return deliveryFeeValue <= 0 ? "Gratis" : null;
        }
    }
}
