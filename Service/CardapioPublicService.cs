using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using APIBack.DTOs.Cardapio;
using APIBack.Model.Cardapio;
using APIBack.Repository.Interface;
using APIBack.Service.Interface;

namespace APIBack.Service
{
    public class CardapioPublicService : ICardapioPublicService
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        private readonly ICardapioRepository _repository;

        public CardapioPublicService(ICardapioRepository repository)
        {
            _repository = repository;
        }

        public async Task<CardapioPublicoCatalogoDto> ObterCatalogoAsync(Guid? idEstabelecimento, string? estabelecimentoSlug, string? busca)
        {
            var estabelecimento = await ResolverEstabelecimentoAsync(idEstabelecimento, estabelecimentoSlug);
            var categorias = await _repository.ListarCategoriasAsync(estabelecimento.Id, null, true, 1, 500);
            var produtos = await _repository.ListarProdutosPublicosAsync(estabelecimento.Id, busca);
            var produtosPorCategoria = produtos
                .GroupBy(x => x.CategoriaId)
                .ToDictionary(group => group.Key, group => group.OrderBy(item => item.Destaque ? 0 : 1).ThenBy(item => item.Ordem).ThenBy(item => item.Nome).ToList());

            var response = new CardapioPublicoCatalogoDto
            {
                Estabelecimento = MapEstabelecimento(estabelecimento)
            };

            foreach (var categoria in categorias.Itens.OrderBy(x => x.Ordem).ThenBy(x => x.Nome))
            {
                if (!produtosPorCategoria.TryGetValue(categoria.Id, out var produtosDaCategoria) || produtosDaCategoria.Count == 0)
                {
                    continue;
                }

                response.Categorias.Add(new CardapioPublicoCategoriaDto
                {
                    Id = categoria.Id,
                    Nome = categoria.Nome,
                    Slug = categoria.Slug,
                    Descricao = categoria.Descricao,
                    ImagemUrl = categoria.ImagemUrl,
                    Ordem = categoria.Ordem,
                    Produtos = produtosDaCategoria.Select(MapProdutoPublico).ToList()
                });
            }

            return response;
        }

        public async Task<CardapioPublicoProdutoDto?> ObterProdutoAsync(Guid? idEstabelecimento, string? estabelecimentoSlug, string slug)
        {
            if (string.IsNullOrWhiteSpace(slug))
            {
                throw BuildValidationException("slug", "Slug do produto e obrigatorio.");
            }

            var estabelecimento = await ResolverEstabelecimentoAsync(idEstabelecimento, estabelecimentoSlug);
            var produto = await _repository.ObterProdutoPublicoPorSlugAsync(estabelecimento.Id, slug.Trim());
            return produto == null ? null : MapProdutoPublico(produto);
        }

        public async Task<CardapioCotacaoDto> CalcularCotacaoAsync(CalcularCardapioPedidoPublicoRequest request)
        {
            var errors = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var tipoEntrega = NormalizeTipoEntrega(request.TipoEntrega, errors);
            var itensRequest = request.Itens ?? new List<CardapioPedidoPublicoItemRequest>();

            if (itensRequest.Count == 0)
            {
                ValidationUtils.AddError(errors, "itens", "Informe ao menos um item no pedido.");
            }

            var estabelecimento = await ResolverEstabelecimentoAsync(request.EstabelecimentoId, request.EstabelecimentoSlug);
            var produtosIds = itensRequest
                .Where(x => x.ProdutoId != Guid.Empty)
                .Select(x => x.ProdutoId)
                .Distinct()
                .ToArray();

            var produtos = await _repository.ListarProdutosPublicosPorIdsAsync(estabelecimento.Id, produtosIds);
            var produtosPorId = produtos.ToDictionary(x => x.Id);
            var cotacaoItens = new List<CardapioCotacaoItemDto>();
            decimal subtotalProdutos = 0;
            decimal subtotalAdicionais = 0;

            for (var index = 0; index < itensRequest.Count; index++)
            {
                var itemRequest = itensRequest[index];
                var fieldPrefix = $"itens[{index}]";

                if (itemRequest.ProdutoId == Guid.Empty)
                {
                    ValidationUtils.AddError(errors, $"{fieldPrefix}.produtoId", "Produto do item e obrigatorio.");
                    continue;
                }

                if (itemRequest.Quantidade <= 0 || itemRequest.Quantidade > 100)
                {
                    ValidationUtils.AddError(errors, $"{fieldPrefix}.quantidade", "Quantidade do item deve ser entre 1 e 100.");
                    continue;
                }

                if (!produtosPorId.TryGetValue(itemRequest.ProdutoId, out var produto))
                {
                    ValidationUtils.AddError(errors, $"{fieldPrefix}.produtoId", "Produto nao encontrado ou indisponivel para venda.");
                    continue;
                }

                var selectedIds = (itemRequest.AdicionalItemIds ?? new List<Guid>())
                    .Where(id => id != Guid.Empty)
                    .Distinct()
                    .ToList();

                var adicionaisSelecionados = new List<CardapioCotacaoAdicionalDto>();
                var adicionaisPorGrupo = new Dictionary<Guid, int>();
                var itensGrupo = produto.Grupos
                    .SelectMany(grupo => grupo.Itens.Select(item => new { Grupo = grupo, Item = item }))
                    .ToDictionary(x => x.Item.Id, x => x);

                foreach (var adicionalId in selectedIds)
                {
                    if (!itensGrupo.TryGetValue(adicionalId, out var itemGrupo))
                    {
                        ValidationUtils.AddError(errors, $"{fieldPrefix}.adicionalItemIds", "Um adicional informado nao pertence ao produto.");
                        continue;
                    }

                    adicionaisSelecionados.Add(new CardapioCotacaoAdicionalDto
                    {
                        Id = itemGrupo.Item.Id,
                        Nome = itemGrupo.Item.Nome,
                        Preco = itemGrupo.Item.Preco
                    });

                    adicionaisPorGrupo[itemGrupo.Grupo.Id] = adicionaisPorGrupo.TryGetValue(itemGrupo.Grupo.Id, out var count)
                        ? count + 1
                        : 1;
                }

                foreach (var grupo in produto.Grupos)
                {
                    var selecionadosNoGrupo = adicionaisPorGrupo.TryGetValue(grupo.Id, out var count) ? count : 0;
                    if (selecionadosNoGrupo < grupo.MinSelecionados)
                    {
                        ValidationUtils.AddError(errors, $"{fieldPrefix}.adicionalItemIds", $"Selecione pelo menos {grupo.MinSelecionados} item(ns) em '{grupo.Nome}'.");
                    }

                    if (selecionadosNoGrupo > grupo.MaxSelecionados)
                    {
                        ValidationUtils.AddError(errors, $"{fieldPrefix}.adicionalItemIds", $"Selecione no maximo {grupo.MaxSelecionados} item(ns) em '{grupo.Nome}'.");
                    }
                }

                var totalProduto = itemRequest.Quantidade * produto.PrecoBase;
                var totalAdicionais = itemRequest.Quantidade * adicionaisSelecionados.Sum(x => x.Preco);

                subtotalProdutos += totalProduto;
                subtotalAdicionais += totalAdicionais;

                cotacaoItens.Add(new CardapioCotacaoItemDto
                {
                    ProdutoId = produto.Id,
                    ProdutoNome = produto.Nome,
                    Quantidade = itemRequest.Quantidade,
                    PrecoUnitario = produto.PrecoBase,
                    TotalProduto = totalProduto,
                    TotalAdicionais = totalAdicionais,
                    TotalItem = totalProduto + totalAdicionais,
                    Observacao = ValidationUtils.TrimToNull(itemRequest.Observacao),
                    AdicionaisSelecionados = adicionaisSelecionados
                });
            }

            ValidationUtils.ThrowIfAny(errors);

            var basePedido = subtotalProdutos + subtotalAdicionais;
            var taxaEntrega = tipoEntrega == "entrega" ? estabelecimento.TaxaEntregaFixa : 0;

            return new CardapioCotacaoDto
            {
                EstabelecimentoId = estabelecimento.Id,
                EstabelecimentoNome = estabelecimento.NomeFantasia,
                TipoEntrega = tipoEntrega,
                AceitaPedidos = estabelecimento.AceitaPedidos,
                PedidoMinimo = estabelecimento.PedidoMinimo,
                PedidoMinimoAtingido = basePedido >= estabelecimento.PedidoMinimo,
                SubtotalProdutos = subtotalProdutos,
                SubtotalAdicionais = subtotalAdicionais,
                TaxaEntrega = taxaEntrega,
                Total = basePedido + taxaEntrega,
                TempoPreparoMin = estabelecimento.TempoPreparoMin,
                Itens = cotacaoItens
            };
        }

        public async Task<CardapioPedidoPublicoCriadoDto> CriarPedidoAsync(CriarCardapioPedidoPublicoRequest request)
        {
            var estabelecimento = await ResolverEstabelecimentoAsync(request.EstabelecimentoId, request.EstabelecimentoSlug);
            var errors = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var nomeCliente = ValidationUtils.TrimToNull(request.Cliente?.Nome);
            var telefone = ValidationUtils.TrimToNull(request.Cliente?.Telefone);
            var email = ValidationUtils.TrimToNull(request.Cliente?.Email);
            var formaPagamento = ValidationUtils.TrimToNull(request.FormaPagamento);
            var observacoes = ValidationUtils.TrimToNull(request.Observacoes);

            if (string.IsNullOrWhiteSpace(nomeCliente) || nomeCliente.Length > 160)
            {
                ValidationUtils.AddError(errors, "cliente.nome", "Nome do cliente e obrigatorio e deve ter no maximo 160 caracteres.");
            }

            if (string.IsNullOrWhiteSpace(telefone) || telefone.Length > 40)
            {
                ValidationUtils.AddError(errors, "cliente.telefone", "Telefone do cliente e obrigatorio e deve ter no maximo 40 caracteres.");
            }

            if (!string.IsNullOrWhiteSpace(email) && email.Length > 320)
            {
                ValidationUtils.AddError(errors, "cliente.email", "Email do cliente deve ter no maximo 320 caracteres.");
            }

            if (!string.IsNullOrWhiteSpace(formaPagamento) && formaPagamento.Length > 120)
            {
                ValidationUtils.AddError(errors, "formaPagamento", "Forma de pagamento deve ter no maximo 120 caracteres.");
            }

            if (!string.IsNullOrWhiteSpace(observacoes) && observacoes.Length > 2000)
            {
                ValidationUtils.AddError(errors, "observacoes", "Observacoes do pedido devem ter no maximo 2000 caracteres.");
            }

            var cotacao = await CalcularCotacaoAsync(request);

            if (!estabelecimento.AceitaPedidos)
            {
                throw new InvalidOperationException("O estabelecimento nao esta aceitando pedidos no momento.");
            }

            if (!cotacao.PedidoMinimoAtingido)
            {
                throw new InvalidOperationException("Pedido minimo ainda nao foi atingido.");
            }

            if (cotacao.TipoEntrega == "entrega")
            {
                var endereco = request.EnderecoEntrega;
                if (endereco == null)
                {
                    ValidationUtils.AddError(errors, "enderecoEntrega", "Endereco de entrega e obrigatorio para pedidos de entrega.");
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(ValidationUtils.TrimToNull(endereco.Logradouro)))
                    {
                        ValidationUtils.AddError(errors, "enderecoEntrega.logradouro", "Logradouro e obrigatorio.");
                    }

                    if (string.IsNullOrWhiteSpace(ValidationUtils.TrimToNull(endereco.Numero)))
                    {
                        ValidationUtils.AddError(errors, "enderecoEntrega.numero", "Numero e obrigatorio.");
                    }

                    if (string.IsNullOrWhiteSpace(ValidationUtils.TrimToNull(endereco.Bairro)))
                    {
                        ValidationUtils.AddError(errors, "enderecoEntrega.bairro", "Bairro e obrigatorio.");
                    }

                    if (string.IsNullOrWhiteSpace(ValidationUtils.TrimToNull(endereco.Cidade)))
                    {
                        ValidationUtils.AddError(errors, "enderecoEntrega.cidade", "Cidade e obrigatoria.");
                    }

                    if (string.IsNullOrWhiteSpace(ValidationUtils.TrimToNull(endereco.Uf)))
                    {
                        ValidationUtils.AddError(errors, "enderecoEntrega.uf", "UF e obrigatoria.");
                    }
                }
            }

            ValidationUtils.ThrowIfAny(errors);

            var codigo = GerarCodigoPedido();
            var entity = new CardapioPedidoPublico
            {
                IdEstabelecimento = estabelecimento.Id,
                Codigo = codigo,
                Status = "pendente",
                TipoEntrega = cotacao.TipoEntrega,
                NomeCliente = nomeCliente!,
                TelefoneCliente = telefone!,
                EmailCliente = email,
                FormaPagamento = formaPagamento,
                Observacoes = observacoes,
                SubtotalProdutos = cotacao.SubtotalProdutos,
                SubtotalAdicionais = cotacao.SubtotalAdicionais,
                TaxaEntrega = cotacao.TaxaEntrega,
                Total = cotacao.Total,
                ItensJson = JsonSerializer.Serialize(new
                {
                    solicitado = request.Itens,
                    cotacao = cotacao.Itens
                }, JsonOptions),
                EnderecoEntregaJson = request.EnderecoEntrega == null
                    ? null
                    : JsonSerializer.Serialize(request.EnderecoEntrega, JsonOptions),
                StatusPagamento = "pendente"
            };

            entity.Id = await _repository.CriarPedidoPublicoAsync(entity);

            return new CardapioPedidoPublicoCriadoDto
            {
                Id = entity.Id,
                Codigo = entity.Codigo,
                Status = entity.Status,
                StatusPagamento = entity.StatusPagamento,
                FormaPagamento = entity.FormaPagamento ?? string.Empty,
                CreatedAt = entity.CreatedAt,
                Resumo = cotacao
            };
        }

        private async Task<CardapioEstabelecimentoPublico> ResolverEstabelecimentoAsync(Guid? idEstabelecimento, string? estabelecimentoSlug)
        {
            var errors = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            if ((!idEstabelecimento.HasValue || idEstabelecimento.Value == Guid.Empty)
                && string.IsNullOrWhiteSpace(ValidationUtils.TrimToNull(estabelecimentoSlug)))
            {
                ValidationUtils.AddError(errors, "estabelecimento", "Informe estabelecimentoId ou estabelecimentoSlug.");
                ValidationUtils.ThrowIfAny(errors);
            }

            var estabelecimento = await _repository.ObterEstabelecimentoPublicoAsync(
                idEstabelecimento.HasValue && idEstabelecimento.Value != Guid.Empty ? idEstabelecimento : null,
                ValidationUtils.TrimToNull(estabelecimentoSlug));

            if (estabelecimento == null)
            {
                throw new KeyNotFoundException("Estabelecimento nao encontrado.");
            }

            var modulos = (estabelecimento.ModulosAtivosRaw ?? Array.Empty<string>())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(ValidationUtils.NormalizeToken)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (!modulos.Contains("cardapio") || !modulos.Contains("cardapioweb"))
            {
                throw new KeyNotFoundException("Cardapio nao disponivel para este estabelecimento.");
            }

            if (!estabelecimento.Publicado)
            {
                throw new KeyNotFoundException("Cardapio ainda nao foi publicado.");
            }

            return estabelecimento;
        }

        private static CardapioPublicoEstabelecimentoDto MapEstabelecimento(CardapioEstabelecimentoPublico entity)
        {
            return new CardapioPublicoEstabelecimentoDto
            {
                Id = entity.Id,
                Nome = entity.NomeFantasia,
                Slug = entity.Slug,
                UrlLogo = entity.UrlLogo,
                AceitaPedidos = entity.AceitaPedidos,
                PedidoMinimo = entity.PedidoMinimo,
                TaxaEntregaFixa = entity.TaxaEntregaFixa,
                TempoPreparoMin = entity.TempoPreparoMin
            };
        }

        private static CardapioPublicoProdutoDto MapProdutoPublico(CardapioProduto entity)
        {
            return new CardapioPublicoProdutoDto
            {
                Id = entity.Id,
                CategoriaId = entity.CategoriaId,
                Nome = entity.Nome,
                Slug = entity.Slug,
                Descricao = entity.Descricao,
                DescricaoCurta = entity.DescricaoCurta,
                PrecoBase = entity.PrecoBase,
                ImagemUrl = entity.ImagemUrl,
                Ordem = entity.Ordem,
                Destaque = entity.Destaque,
                Grupos = entity.Grupos
                    .OrderBy(x => x.Ordem)
                    .ThenBy(x => x.Nome)
                    .Select(grupo => new CardapioPublicoGrupoDto
                    {
                        Id = grupo.Id,
                        Nome = grupo.Nome,
                        Descricao = grupo.Descricao,
                        MinSelecionados = grupo.MinSelecionados,
                        MaxSelecionados = grupo.MaxSelecionados,
                        Ordem = grupo.Ordem,
                        Itens = grupo.Itens
                            .OrderBy(x => x.Ordem)
                            .ThenBy(x => x.Nome)
                            .Select(item => new CardapioPublicoGrupoItemDto
                            {
                                Id = item.Id,
                                Nome = item.Nome,
                                Descricao = item.Descricao,
                                Preco = item.Preco,
                                Ordem = item.Ordem
                            })
                            .ToList()
                    })
                    .ToList()
            };
        }

        private static string NormalizeTipoEntrega(string? value, Dictionary<string, List<string>> errors)
        {
            var normalized = ValidationUtils.NormalizeToken(value);
            return normalized switch
            {
                "" => "retirada",
                "retirada" => "retirada",
                "balcao" => "retirada",
                "pickup" => "retirada",
                "entrega" => "entrega",
                "delivery" => "entrega",
                _ => AddTipoEntregaError(errors)
            };
        }

        private static string AddTipoEntregaError(Dictionary<string, List<string>> errors)
        {
            ValidationUtils.AddError(errors, "tipoEntrega", "Tipo de entrega invalido. Use 'retirada' ou 'entrega'.");
            return "retirada";
        }

        private static RequestValidationException BuildValidationException(string field, string message)
        {
            return new RequestValidationException(
                "Dados invalidos.",
                new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    [field] = new List<string> { message }
                });
        }

        private static string GerarCodigoPedido()
            => $"CDP-{Guid.NewGuid():N}"[..12].ToUpperInvariant();
    }
}
