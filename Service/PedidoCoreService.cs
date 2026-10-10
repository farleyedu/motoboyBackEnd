using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using APIBack.DTOs.Delivery;
using APIBack.DTOs.Clientes;
using APIBack.Model.Cardapio;
using APIBack.Model.Delivery;
using APIBack.Repository.Interface;
using APIBack.Service.Interface;

namespace APIBack.Service
{
    public sealed class PedidoCoreService : IPedidoCoreService
    {
        private const int MaxOrigemRefLength = 120;
        // Texto da coluna legada pedido.items: guarda acentos e "+" legiveis (nao vai para HTML).
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        private readonly IPedidoQueueRepository _queue;
        private readonly IRestaurantSettingsRepository _restaurant;
        private readonly ICardapioRepository _cardapio;
        private readonly IDeliveryZonaRepository _zonas;
        private readonly IHorarioOperacaoRepository _horarios;
        private readonly IAtendenteConfirmacaoSender _confirmacaoAtendente;
        private readonly IClienteCadastroRepository _clientes;
        private readonly IClienteEnderecoRepository _enderecos;

        public PedidoCoreService(
            IPedidoQueueRepository queue,
            IRestaurantSettingsRepository restaurant,
            ICardapioRepository cardapio,
            IDeliveryZonaRepository zonas,
            IHorarioOperacaoRepository horarios,
            IAtendenteConfirmacaoSender confirmacaoAtendente,
            IClienteCadastroRepository clientes,
            IClienteEnderecoRepository enderecos)
        {
            _queue = queue;
            _restaurant = restaurant;
            _cardapio = cardapio;
            _zonas = zonas;
            _horarios = horarios;
            _confirmacaoAtendente = confirmacaoAtendente;
            _clientes = clientes;
            _enderecos = enderecos;
        }

        public async Task<CreatedPedidoDto> CreateAsync(Guid estabelecimentoId, int actorUserId, CreatePedidoRequest request, string? idempotencyKey, bool autoAtribuir = true)
        {
            var order = await BuildAsync(estabelecimentoId, request, idempotencyKey);
            // Valida o endereço antes de criar o pedido: metadados inválidos não podem deixar um pedido criado com resposta de erro.
            if (order.ClienteId.HasValue && !string.IsNullOrWhiteSpace(order.Estado)) ClienteEnderecoRules.Validate(AddressInput(order, request));
            var created = await _queue.CreatePedidoAsync(estabelecimentoId, actorUserId, order);
            if (!created.JaExistia) await SaveAddressAsync(estabelecimentoId, order, request);
            var isPending = string.Equals(created.Status, "pendente", StringComparison.OrdinalIgnoreCase);
            if (autoAtribuir && !created.JaExistia && isPending)
            {
                await TryAutoAssignAsync(estabelecimentoId, actorUserId, created.Id);
            }
            if (!created.JaExistia && isPending && order.Origem == PedidoOrigem.Atendente)
            {
                // Pedido feito pelo atendente (telefone/balcao): confirma com o cliente mostrando os itens,
                // sempre, mesmo que ele tenha desligado os avisos automaticos de rastreio (nao e a mesma coisa:
                // esta existe pra pegar erro de digitacao, nao pra rastrear entrega). Nunca lanca.
                await _confirmacaoAtendente.TrySendAsync(estabelecimentoId, created.Id);
            }
            return created;
        }

        /// <summary>
        /// Autoatribuicao (Fase 2 de Configuracoes): so age quando ha EXATAMENTE um motoboy disponivel
        /// (FindSingleAvailableMotoboyAsync nunca escolhe entre varios). E uma conveniencia: qualquer
        /// falha aqui e ignorada, o pedido ja foi criado e continua na fila normal para o atendente
        /// atribuir manualmente.
        /// </summary>
        private async Task TryAutoAssignAsync(Guid estabelecimentoId, int actorUserId, int pedidoId)
        {
            try
            {
                var delivery = await _queue.GetSettingsAsync(estabelecimentoId);
                if (delivery is not { AutoatribuirMotoboy: true })
                {
                    return;
                }
                var motoboyId = await _queue.FindSingleAvailableMotoboyAsync(estabelecimentoId);
                if (motoboyId.HasValue)
                {
                    await _queue.AssignAsync(estabelecimentoId, actorUserId, motoboyId.Value, pedidoId);
                }
            }
            catch (DeliveryDomainException)
            {
                // Autoatribuicao e conveniencia; nunca derruba a criacao do pedido, que ja aconteceu.
            }
        }

        public async Task<CreatedPedidoDto> UpdateAsync(Guid estabelecimentoId, int actorUserId, int pedidoId, CreatePedidoRequest request)
        {
            if (pedidoId <= 0)
            {
                throw new DeliveryDomainException(422, "INVALID_REQUEST", "pedidoId invalido.");
            }
            // Editar nao cria: a chave de idempotencia nao se aplica.
            var order = (await BuildAsync(estabelecimentoId, request, idempotencyKey: null)) with { OrigemRef = null };
            var updated = await _queue.UpdatePedidoAsync(estabelecimentoId, actorUserId, pedidoId, order);
            await SaveAddressAsync(estabelecimentoId, order, request);
            return updated;
        }

        private async Task SaveAddressAsync(Guid est, ManualOrder order, CreatePedidoRequest request)
        {
            if (!order.ClienteId.HasValue || string.IsNullOrWhiteSpace(order.Estado)) return;
            await _enderecos.SaveAsync(est, order.ClienteId.Value, null, AddressInput(order, request));
        }

        private static ClienteEnderecoRequest AddressInput(ManualOrder order, CreatePedidoRequest request) => new()
        {
            Logradouro = order.Rua, Numero = order.Numero, Complemento = request.Complemento,
            Bairro = order.Bairro, Cidade = order.Cidade, Uf = order.Estado, Cep = order.Cep,
            Latitude = order.Latitude, Longitude = order.Longitude, Principal = request.EnderecoPrincipal,
            Apelido = request.ApelidoEndereco
        };

        public async Task<CreatedPedidoDto> ConfirmAsync(Guid estabelecimentoId, int actorUserId, int pedidoId)
        {
            if (pedidoId <= 0)
            {
                throw new DeliveryDomainException(422, "INVALID_REQUEST", "pedidoId invalido.");
            }
            var settings = await _queue.GetSettingsAsync(estabelecimentoId);
            return await _queue.ConfirmPedidoAsync(estabelecimentoId, actorUserId, pedidoId,
                settings?.DefaultDeliveryMinutes ?? ManualOrderRules.DefaultPrevisaoMinutos);
        }

        /// <summary>Valida, precifica e aplica as regras do estabelecimento; devolve o pedido pronto para gravar.</summary>
        internal async Task<ManualOrder> BuildAsync(Guid estabelecimentoId, CreatePedidoRequest? request, string? idempotencyKey)
        {
            if (request == null)
            {
                throw new DeliveryDomainException(400, "INVALID_REQUEST", "Corpo da requisicao obrigatorio.");
            }

            var origin = PedidoOrigem.Normalize(request.Origem)
                ?? throw new DeliveryDomainException(422, "INVALID_ORIGIN",
                    $"Origem invalida. Use: {string.Join(", ", PedidoOrigem.All)}.");
            var rules = PedidoOrigem.Rules(origin);
            var origemRef = CleanRef(request.OrigemRef ?? idempotencyKey);

            var restaurant = await _restaurant.GetAsync(estabelecimentoId)
                ?? throw new DeliveryDomainException(404, "ESTABELECIMENTO_NOT_FOUND", "Estabelecimento nao encontrado.");
            var delivery = await _queue.GetSettingsAsync(estabelecimentoId);

            // Sem previsao informada vale o prazo padrao do estabelecimento (mesma regra do pedido manual).
            if (!request.PrevisaoMinutos.HasValue && delivery != null)
            {
                request.PrevisaoMinutos = delivery.DefaultDeliveryMinutes;
            }

            // So nas origens estritas (cliente/IA, sem atendente controlando): a loja pode recusar fora
            // do horario e decidir se o pedido entra direto como Pendente ou precisa de confirmacao.
            bool? forcarRascunho = null;
            if (rules.EnforceStoreRules && delivery != null)
            {
                if (delivery.BloquearPedidosForaHorario
                    && !await _horarios.EstaAbertoAgoraAsync(estabelecimentoId, DateTimeOffset.UtcNow, "America/Sao_Paulo"))
                {
                    throw new DeliveryDomainException(409, "STORE_CLOSED", "O estabelecimento esta fechado no momento.");
                }
                if (!delivery.AutoConfirmarPedidos)
                {
                    forcarRascunho = true;
                }
            }

            var manual = ManualOrderRules.Validate(request);
            // Fase 3c (D14): todo pedido, de qualquer origem, fica ligado a um cliente de verdade
            // (acha pelo telefone ou cria um novo); nunca mais nome/telefone soltos no pedido.
            var clienteId = await _clientes.ResolverOuCriarAsync(estabelecimentoId, manual.TelefoneCliente, manual.NomeCliente);
            var hasItems = request.Itens is { Count: > 0 };
            var zonas = await _zonas.ListAtivasOrdenadasAsync(estabelecimentoId);

            if (!hasItems)
            {
                if (rules.EnforceStoreRules)
                {
                    throw new DeliveryDomainException(422, "INVALID_ORDER_ITEMS",
                        "Informe os itens do pedido: esta origem calcula o preco a partir do cardapio.");
                }
                // Formato antigo (items em texto + valor digitado): mantido como esta; so avisos.
                var legacy = OrderCoreRules.Evaluate(restaurant, manual.Value, manual.Latitude, manual.Longitude, rules, feeOverride: null, zonas);
                return manual with
                {
                    Origem = origin,
                    OrigemRef = origemRef,
                    ConversaId = request.ConversaId,
                    ClienteId = clienteId,
                    Rascunho = forcarRascunho ?? request.Rascunho,
                    Avisos = legacy.Warnings
                };
            }

            if (request.TaxaEntrega.HasValue && !rules.AllowFeeOverride)
            {
                throw new DeliveryDomainException(422, "INVALID_ORDER",
                    "A taxa de entrega e calculada pelo servidor nesta origem.");
            }

            var productIds = request.Itens!
                .Where(item => item?.ProdutoId is { } id && id != Guid.Empty)
                .Select(item => item!.ProdutoId!.Value)
                .Distinct()
                .ToList();
            var products = productIds.Count == 0
                ? new Dictionary<Guid, CardapioProduto>()
                : (await _cardapio.ListarProdutosPublicosPorIdsAsync(estabelecimentoId, productIds, exigirPublicoWeb: false))
                    .ToDictionary(product => product.Id);

            var priced = PedidoPricing.Price(request.Itens, products, rules.AllowFreeLines);
            var evaluation = OrderCoreRules.Evaluate(restaurant, priced.Subtotal, manual.Latitude, manual.Longitude, rules, request.TaxaEntrega, zonas);
            var total = decimal.Round(priced.Subtotal + evaluation.DeliveryFee, 2);
            if (total > OrderCoreRules.MaxMoney)
            {
                throw new DeliveryDomainException(422, "INVALID_ORDER", "Valor do pedido invalido.");
            }

            return manual with
            {
                Origem = origin,
                OrigemRef = origemRef,
                ConversaId = request.ConversaId,
                ClienteId = clienteId,
                Rascunho = forcarRascunho ?? request.Rascunho,
                Lines = priced.Lines,
                Subtotal = priced.Subtotal,
                TaxaEntrega = evaluation.DeliveryFee,
                Value = total,
                Items = LegacyItemsJson(priced.Lines),
                TipoPagamento = OrderCoreRules.NormalizePayment(manual.TipoPagamento),
                Avisos = evaluation.Warnings
            };
        }

        /// <summary>
        /// Texto para a coluna legada pedido.items (o painel e o app do motoboy ainda a leem):
        /// [{"nome","quantidade","preco"}], com os adicionais no nome e no preco unitario.
        /// </summary>
        internal static string LegacyItemsJson(IReadOnlyList<PricedLine> lines) =>
            JsonSerializer.Serialize(lines.Select(line => new
            {
                nome = line.Adicionais.Count == 0
                    ? line.Nome
                    : line.Nome + " + " + string.Join(", ", line.Adicionais.Select(a => a.Nome)),
                quantidade = line.Quantidade,
                preco = line.PrecoUnitario + line.Adicionais.Sum(a => a.Preco)
            }), Json);

        private static string? CleanRef(string? value)
        {
            var trimmed = value?.Trim();
            if (string.IsNullOrEmpty(trimmed)) return null;
            if (trimmed.Length > MaxOrigemRefLength)
            {
                throw new DeliveryDomainException(422, "INVALID_REQUEST", $"origemRef excede {MaxOrigemRefLength} caracteres.");
            }
            return trimmed;
        }
    }
}
