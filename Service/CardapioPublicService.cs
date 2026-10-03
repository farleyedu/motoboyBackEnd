using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using APIBack.Automation.Interfaces;
using APIBack.DTOs.Cardapio;
using Microsoft.Extensions.Logging;
using APIBack.Model.Cardapio;
using APIBack.Repository.Interface;
using APIBack.Service.Interface;

namespace APIBack.Service
{
    public class CardapioPublicService : ICardapioPublicService
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        private readonly ICardapioRepository _repository;
        private readonly ILocalizacaoService _localizacao;
        private readonly IWabaPhoneRepository _waba;
        private readonly ICardapioPedidoWebService _confirmacao;
        private readonly IPedidosAbertosService _pedidosAbertos;
        private readonly ILogger<CardapioPublicService> _logger;

        public CardapioPublicService(
            ICardapioRepository repository,
            ILocalizacaoService localizacao,
            IWabaPhoneRepository waba,
            ICardapioPedidoWebService confirmacao,
            IPedidosAbertosService pedidosAbertos,
            ILogger<CardapioPublicService> logger)
        {
            _repository = repository;
            _localizacao = localizacao;
            _waba = waba;
            _confirmacao = confirmacao;
            _pedidosAbertos = pedidosAbertos;
            _logger = logger;
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
                Estabelecimento = MapEstabelecimento(estabelecimento, await _pedidosAbertos.AvaliarAsync(estabelecimento.Id, estabelecimento.AceitaPedidos))
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
                    .SelectMany(grupo => grupo.Itens.Select(item => (Grupo: grupo, Item: item)))
                    .ToDictionary(x => x.Item.Id, x => x);
                var itensJaSelecionados = new HashSet<Guid>();

                foreach (var adicionalId in selectedIds)
                {
                    if (!itensGrupo.TryGetValue(adicionalId, out var itemGrupo)
                        && !TryResolverAdicionalGlobal(produto, adicionalId, out itemGrupo))
                    {
                        ValidationUtils.AddError(errors, $"{fieldPrefix}.adicionalItemIds", "Um adicional informado nao pertence ao produto.");
                        continue;
                    }

                    // O mesmo adicional mandado pelo id do item e pelo id do adicional conta uma vez so.
                    if (!itensJaSelecionados.Add(itemGrupo.Item.Id))
                    {
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

            var situacaoPedidos = await _pedidosAbertos.AvaliarAsync(estabelecimento.Id, estabelecimento.AceitaPedidos);
            var basePedido = subtotalProdutos + subtotalAdicionais;
            var taxaEntrega = tipoEntrega == "entrega" ? estabelecimento.TaxaEntregaFixa : 0;

            return new CardapioCotacaoDto
            {
                EstabelecimentoId = estabelecimento.Id,
                EstabelecimentoNome = estabelecimento.NomeFantasia,
                TipoEntrega = tipoEntrega,
                AceitaPedidos = situacaoPedidos.Aberto,
                MotivoFechado = situacaoPedidos.Motivo,
                AbreEm = situacaoPedidos.AbreEm,
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
            var telefoneE164 = PhoneKey.ToE164(telefone);
            var email = ValidationUtils.TrimToNull(request.Cliente?.Email);
            var formaPagamento = ValidationUtils.TrimToNull(request.FormaPagamento);
            var observacoes = ValidationUtils.TrimToNull(request.Observacoes);

            // Os limites abaixo sao os do pedido do delivery (ManualOrderRules): passar daqui so falharia no aceite.
            if (string.IsNullOrWhiteSpace(nomeCliente) || nomeCliente.Length > 150)
            {
                ValidationUtils.AddError(errors, "cliente.nome", "Nome do cliente e obrigatorio e deve ter no maximo 150 caracteres.");
            }

            if (string.IsNullOrWhiteSpace(telefone) || telefone.Length > 40)
            {
                ValidationUtils.AddError(errors, "cliente.telefone", "Telefone do cliente e obrigatorio e deve ter no maximo 40 caracteres.");
            }
            else if (telefoneE164 == null)
            {
                // O pedido so chega ao restaurante depois de uma conversa por este numero: precisa ser um WhatsApp com DDD.
                ValidationUtils.AddError(errors, "cliente.telefone", "Informe o WhatsApp com DDD, por exemplo (34) 99999-0000.");
            }

            if (!string.IsNullOrWhiteSpace(email) && email.Length > 320)
            {
                ValidationUtils.AddError(errors, "cliente.email", "Email do cliente deve ter no maximo 320 caracteres.");
            }

            if (!string.IsNullOrWhiteSpace(formaPagamento) && formaPagamento.Length > 50)
            {
                ValidationUtils.AddError(errors, "formaPagamento", "Forma de pagamento deve ter no maximo 50 caracteres.");
            }

            // 500: e o limite do pedido real do delivery, onde estas observacoes vao parar no aceite.
            if (!string.IsNullOrWhiteSpace(observacoes) && observacoes.Length > 500)
            {
                ValidationUtils.AddError(errors, "observacoes", "Observacoes do pedido devem ter no maximo 500 caracteres.");
            }

            var cotacao = await CalcularCotacaoAsync(request);

            if (!cotacao.AceitaPedidos)
            {
                // O servidor e quem decide: a tela so mostra o aviso. O carrinho do cliente continua la para quando a loja abrir.
                _logger.LogInformation("[cardapio] ev=pedido_recusado motivo={Motivo} loja={Loja}", cotacao.MotivoFechado, estabelecimento.Id);
                throw new DeliveryDomainException(409, "loja_fechada",
                    PedidosAbertosRules.Mensagem(new SituacaoPedidos(false, cotacao.MotivoFechado, cotacao.AbreEm)));
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
                    ValidarTexto(errors, "enderecoEntrega.logradouro", "Logradouro", endereco.Logradouro, 200, obrigatorio: true);
                    ValidarTexto(errors, "enderecoEntrega.numero", "Numero", endereco.Numero, 20, obrigatorio: true);
                    ValidarTexto(errors, "enderecoEntrega.complemento", "Complemento", endereco.Complemento, 100, obrigatorio: false);
                    ValidarTexto(errors, "enderecoEntrega.bairro", "Bairro", endereco.Bairro, 100, obrigatorio: true);
                    ValidarTexto(errors, "enderecoEntrega.cidade", "Cidade", endereco.Cidade, 100, obrigatorio: true);

                    var uf = ValidationUtils.TrimToNull(endereco.Uf);
                    if (string.IsNullOrWhiteSpace(uf))
                    {
                        ValidationUtils.AddError(errors, "enderecoEntrega.uf", "UF e obrigatoria.");
                    }
                    else if (uf.Length != 2 || !uf.All(char.IsLetter))
                    {
                        ValidationUtils.AddError(errors, "enderecoEntrega.uf", "UF deve ter 2 letras, por exemplo MG.");
                    }

                    var cep = ValidationUtils.TrimToNull(endereco.Cep);
                    if (cep != null && cep.Count(char.IsDigit) != 8)
                    {
                        ValidationUtils.AddError(errors, "enderecoEntrega.cep", "CEP deve ter 8 digitos.");
                    }
                }
            }

            ValidationUtils.ThrowIfAny(errors);

            // Sem um numero de WhatsApp que atenda o cardapio web nao ha como confirmar o pedido: melhor recusar aqui do que
            // deixar o cliente esperando.
            if (string.IsNullOrWhiteSpace(await _waba.ObterDisplayPhoneParaServicoAsync(estabelecimento.Id, "cardapio_web")))
            {
                throw new InvalidOperationException("Este restaurante ainda nao recebe pedidos pelo WhatsApp.");
            }

            var enderecoArmazenado = cotacao.TipoEntrega == "entrega"
                ? await LocalizarEnderecoDoPedidoAsync(request.EnderecoEntrega!, estabelecimento)
                : null;

            var codigo = GerarCodigoPedido();
            var entity = new CardapioPedidoPublico
            {
                IdEstabelecimento = estabelecimento.Id,
                Codigo = codigo,
                Status = "pendente",
                TipoEntrega = cotacao.TipoEntrega,
                NomeCliente = nomeCliente!,
                TelefoneCliente = telefoneE164!,
                EmailCliente = email,
                FormaPagamento = formaPagamento,
                Observacoes = observacoes,
                SubtotalProdutos = cotacao.SubtotalProdutos,
                SubtotalAdicionais = cotacao.SubtotalAdicionais,
                TaxaEntrega = cotacao.TaxaEntrega,
                Total = cotacao.Total,
                ItensJson = JsonSerializer.Serialize(new
                {
                    // O que o servidor entendeu (ids de ITEM de adicional ja resolvidos): e o que o aceite manda ao nucleo.
                    solicitado = cotacao.Itens.Select(item => new CardapioPedidoPublicoItemRequest
                    {
                        ProdutoId = item.ProdutoId,
                        Quantidade = item.Quantidade,
                        Observacao = item.Observacao,
                        AdicionalItemIds = item.AdicionaisSelecionados.Select(adicional => adicional.Id).ToList()
                    }).ToList(),
                    cotacao = cotacao.Itens
                }, JsonOptions),
                EnderecoEntregaJson = enderecoArmazenado == null
                    ? null
                    : JsonSerializer.Serialize(enderecoArmazenado, JsonOptions),
                StatusPagamento = "pendente"
            };

            entity.Id = await _repository.CriarPedidoPublicoAsync(entity);

            // Janela de 24h aberta: mandamos a mensagem e o pedido ja vai ao restaurante. Fechada: o cliente recebe um codigo.
            var confirmacao = await _confirmacao.IniciarConfirmacaoAsync(
                entity, string.IsNullOrWhiteSpace(estabelecimento.NomePublico) ? estabelecimento.NomeFantasia : estabelecimento.NomePublico!);

            return new CardapioPedidoPublicoCriadoDto
            {
                Id = entity.Id,
                Codigo = entity.Codigo,
                Status = confirmacao.Modo == "mensagem_enviada" ? CardapioPedidoStatus.AguardandoAceite : CardapioPedidoStatus.AguardandoCodigo,
                StatusPagamento = entity.StatusPagamento,
                FormaPagamento = entity.FormaPagamento ?? string.Empty,
                CreatedAt = entity.CreatedAt,
                Resumo = cotacao,
                Confirmacao = confirmacao
            };
        }

        private static void ValidarTexto(
            Dictionary<string, List<string>> errors, string campo, string rotulo, string? valor, int maximo, bool obrigatorio)
        {
            var texto = ValidationUtils.TrimToNull(valor);
            if (texto == null)
            {
                if (obrigatorio) ValidationUtils.AddError(errors, campo, $"{rotulo} e obrigatorio.");
                return;
            }

            if (texto.Length > maximo)
            {
                ValidationUtils.AddError(errors, campo, $"{rotulo} deve ter no maximo {maximo} caracteres.");
            }
        }

        private const string TipoAdicionalGlobal = "adicional_global";

        /// <summary>
        /// O cardapio web mostra cada adicional pelo id do ADICIONAL (o grupo de tipo adicional_global, ver o
        /// snapshot publico), mas o preco vive no item do grupo. Aceita o id do adicional e usa o primeiro item
        /// ativo, o mesmo que o snapshot mostra como preco.
        /// </summary>
        private static bool TryResolverAdicionalGlobal(
            CardapioProduto produto,
            Guid adicionalId,
            out (CardapioGrupoAdicional Grupo, CardapioGrupoAdicionalItem Item) resolvido)
        {
            resolvido = default;
            var grupo = produto.Grupos.FirstOrDefault(g =>
                g.Id == adicionalId && string.Equals(g.Tipo, TipoAdicionalGlobal, StringComparison.OrdinalIgnoreCase));
            var item = grupo?.Itens.Where(i => i.Ativo).OrderBy(i => i.Ordem).ThenBy(i => i.Nome).FirstOrDefault();
            if (grupo == null || item == null) return false;

            resolvido = (grupo, item);
            return true;
        }

        private static readonly string[] TiposDeVia =
        {
            "alameda", "avenida", "av", "travessa", "rodovia", "estrada", "praca", "praça", "viela", "beco", "via", "largo"
        };

        private static readonly string[] PalavrasDeNumero = { "numero", "número", "nº", "n°" };

        /// <summary>
        /// Deixa so o nome da via: "Rua Alameda Dos Mandarins" vira "Alameda Dos Mandarins" (tipo de via repetido) e
        /// "Alameda Dos Mandarins Número" perde o "Número" sobrando no fim (vem do preenchimento por CEP ou de quem digita).
        /// </summary>
        internal static string LimparLogradouro(string? logradouro)
        {
            var partes = (logradouro ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

            while (partes.Count > 1 && PalavrasDeNumero.Contains(partes[^1].TrimEnd('.', ',').ToLowerInvariant()))
            {
                partes.RemoveAt(partes.Count - 1);
            }

            if (partes.Count > 2
                && partes[0].Equals("rua", StringComparison.OrdinalIgnoreCase)
                && TiposDeVia.Contains(partes[1].TrimEnd('.').ToLowerInvariant()))
            {
                partes.RemoveAt(0);
            }

            return string.Join(' ', partes);
        }

        /// <summary>
        /// Buscas a tentar, da mais completa para a mais enxuta (o Google as vezes nao conhece o bairro digitado, mas
        /// acha a rua e o numero pelo CEP). So a primeira que devolver ponto exato vale.
        /// </summary>
        internal static IReadOnlyList<(string Endereco, string? Cep)> MontarConsultasEndereco(CardapioEnderecoArmazenado e)
        {
            var rua = LimparLogradouro(e.Logradouro);
            var cidadeUf = $"{e.Cidade} - {e.Uf}, Brasil";
            var numero = string.IsNullOrWhiteSpace(e.Numero) ? string.Empty : $", {e.Numero}";
            var cep = new string((e.Cep ?? string.Empty).Where(char.IsDigit).ToArray());
            var cepOuNulo = cep.Length == 8 ? cep : null;

            var consultas = new List<(string, string?)> { ($"{rua}{numero}, {e.Bairro}, {cidadeUf}", cepOuNulo) };
            if (!string.IsNullOrWhiteSpace(e.Bairro))
            {
                consultas.Add(($"{rua}{numero}, {cidadeUf}", cepOuNulo));
            }

            return consultas;
        }

        /// <summary>
        /// A coordenada e obrigatoria no pedido do delivery e nunca e inventada: vem da busca do endereco, feita aqui
        /// no servidor para o cliente nao poder mandar uma posicao qualquer. So vale ponto EXATO (o numero foi achado):
        /// rua, bairro ou centro do CEP nao servem para entregar. Sem ponto exato = erro de validacao.
        /// </summary>
        private async Task<CardapioEnderecoArmazenado> LocalizarEnderecoDoPedidoAsync(
            CriarCardapioPedidoPublicoEnderecoRequest endereco, CardapioEstabelecimentoPublico estabelecimento)
        {
            var armazenado = new CardapioEnderecoArmazenado
            {
                Logradouro = ValidationUtils.TrimToNull(endereco.Logradouro),
                Numero = ValidationUtils.TrimToNull(endereco.Numero),
                Complemento = ValidationUtils.TrimToNull(endereco.Complemento),
                Bairro = ValidationUtils.TrimToNull(endereco.Bairro),
                Cidade = ValidationUtils.TrimToNull(endereco.Cidade),
                Uf = ValidationUtils.TrimToNull(endereco.Uf)?.ToUpperInvariant(),
                Cep = ValidationUtils.TrimToNull(endereco.Cep),
                Referencia = ValidationUtils.TrimToNull(endereco.Referencia)
            };

            // O cliente confirmou o ponto no mapa (o que a busca achou, o pino arrastado ou o GPS): vale ele, sem gastar outra
            // busca. O servidor so confere se o ponto e plausivel.
            if (endereco.Latitude.HasValue || endereco.Longitude.HasValue)
            {
                var erro = PontoDeEntregaRules.Validar(
                    endereco.Latitude, endereco.Longitude, endereco.OrigemPonto,
                    estabelecimento.Latitude, estabelecimento.Longitude, estabelecimento.RaioEntregaKm);
                if (erro != null) throw BuildValidationException("enderecoEntrega", erro);

                armazenado.Latitude = endereco.Latitude!.Value;
                armazenado.Longitude = endereco.Longitude!.Value;
                armazenado.Precisao = endereco.OrigemPonto!.Trim().ToLowerInvariant();
                return armazenado;
            }

            GeocodeResultado? achado = null;
            var encontrouAlgo = false;
            foreach (var (consulta, cep) in MontarConsultasEndereco(armazenado))
            {
                GeocodeResultado? resultado = null;
                try
                {
                    resultado = await _localizacao.GeocodificarAsync(consulta, cep);
                }
                catch (Exception)
                {
                    // Falha do servico de mapas: tenta a proxima; sem ponto exato o pedido nao e criado.
                }

                if (resultado == null) continue;
                encontrouAlgo = true;
                if (resultado.Exata)
                {
                    achado = resultado;
                    break;
                }
            }

            if (achado == null
                || !TryParseCoordenada(achado.Latitude, out var latitude)
                || !TryParseCoordenada(achado.Longitude, out var longitude))
            {
                throw BuildValidationException("enderecoEntrega", encontrouAlgo
                    ? "Nao conseguimos confirmar o ponto exato desse endereco. Confira rua, numero e CEP."
                    : "Nao encontramos esse endereco no mapa. Confira rua, numero e cidade.");
            }

            armazenado.Latitude = latitude;
            armazenado.Longitude = longitude;
            armazenado.Precisao = "exata";
            return armazenado;
        }

        public async Task<CardapioLocalizacaoDto> LocalizarEnderecoAsync(LocalizarCardapioEnderecoRequest request)
        {
            var estabelecimento = await ResolverEstabelecimentoAsync(request.EstabelecimentoId, request.EstabelecimentoSlug);
            var armazenado = new CardapioEnderecoArmazenado
            {
                Logradouro = ValidationUtils.TrimToNull(request.Logradouro),
                Numero = ValidationUtils.TrimToNull(request.Numero),
                Bairro = ValidationUtils.TrimToNull(request.Bairro),
                Cidade = ValidationUtils.TrimToNull(request.Cidade),
                Uf = ValidationUtils.TrimToNull(request.Uf)?.ToUpperInvariant(),
                Cep = ValidationUtils.TrimToNull(request.Cep)
            };

            var resultado = new CardapioLocalizacaoDto
            {
                CentroLatitude = estabelecimento.Latitude,
                CentroLongitude = estabelecimento.Longitude
            };

            // O primeiro ponto exato vence; se nenhuma busca chegou ao numero, devolve o melhor ponto achado como ponto de partida
            // do pino (o cliente confirma ou arrasta).
            GeocodeResultado? aproximado = null;
            foreach (var (consulta, cep) in MontarConsultasEndereco(armazenado))
            {
                GeocodeResultado? achado = null;
                try
                {
                    achado = await _localizacao.GeocodificarAsync(consulta, cep);
                }
                catch (Exception)
                {
                    // Servico de mapas fora: tenta a proxima; no pior caso o cliente posiciona o pino.
                }

                if (achado == null) continue;
                if (achado.Exata)
                {
                    aproximado = achado;
                    resultado.Exata = true;
                    break;
                }

                aproximado ??= achado;
            }

            if (aproximado != null
                && TryParseCoordenada(aproximado.Latitude, out var latitude)
                && TryParseCoordenada(aproximado.Longitude, out var longitude))
            {
                resultado.Encontrado = true;
                resultado.Latitude = latitude;
                resultado.Longitude = longitude;
            }
            else
            {
                resultado.Exata = false;
            }

            return resultado;
        }

        public async Task<CardapioEnderecoDoPontoDto> ObterEnderecoDoPontoAsync(CardapioEnderecoDoPontoRequest request)
        {
            var estabelecimento = await ResolverEstabelecimentoAsync(request.EstabelecimentoId, request.EstabelecimentoSlug);
            var erro = PontoDeEntregaRules.Validar(
                request.Latitude, request.Longitude, PontoDeEntregaRules.Gps,
                estabelecimento.Latitude, estabelecimento.Longitude, estabelecimento.RaioEntregaKm);
            if (erro != null) throw BuildValidationException("latitude", erro);

            var endereco = await _localizacao.GeocodificarReversoAsync(request.Latitude, request.Longitude);
            return new CardapioEnderecoDoPontoDto
            {
                Logradouro = endereco?.Logradouro, Numero = endereco?.Numero, Bairro = endereco?.Bairro,
                Cidade = endereco?.Cidade, Uf = endereco?.Uf, Cep = endereco?.Cep
            };
        }

        private static bool TryParseCoordenada(string? texto, out double valor) =>
            double.TryParse((texto ?? string.Empty).Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out valor)
            && double.IsFinite(valor);

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

        private static CardapioPublicoEstabelecimentoDto MapEstabelecimento(CardapioEstabelecimentoPublico entity, SituacaoPedidos situacaoPedidos)
        {
            return new CardapioPublicoEstabelecimentoDto
            {
                Id = entity.Id,
                Nome = entity.NomeFantasia,
                Slug = entity.Slug,
                UrlLogo = entity.UrlLogo,
                AceitaPedidos = situacaoPedidos.Aberto,
                MotivoFechado = situacaoPedidos.Motivo,
                AbreEm = situacaoPedidos.AbreEm,
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
