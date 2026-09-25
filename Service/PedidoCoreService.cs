using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using APIBack.DTOs.Delivery;
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

        public PedidoCoreService(
            IPedidoQueueRepository queue,
            IRestaurantSettingsRepository restaurant,
            ICardapioRepository cardapio)
        {
            _queue = queue;
            _restaurant = restaurant;
            _cardapio = cardapio;
        }

        public async Task<CreatedPedidoDto> CreateAsync(Guid estabelecimentoId, int actorUserId, CreatePedidoRequest request, string? idempotencyKey)
        {
            var order = await BuildAsync(estabelecimentoId, request, idempotencyKey);
            return await _queue.CreatePedidoAsync(estabelecimentoId, actorUserId, order);
        }

        public async Task<CreatedPedidoDto> UpdateAsync(Guid estabelecimentoId, int actorUserId, int pedidoId, CreatePedidoRequest request)
        {
            if (pedidoId <= 0)
            {
                throw new DeliveryDomainException(422, "INVALID_REQUEST", "pedidoId invalido.");
            }
            // Editar nao cria: a chave de idempotencia nao se aplica.
            var order = (await BuildAsync(estabelecimentoId, request, idempotencyKey: null)) with { OrigemRef = null };
            return await _queue.UpdatePedidoAsync(estabelecimentoId, actorUserId, pedidoId, order);
        }

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

            // Sem previsao informada vale o prazo padrao do estabelecimento (mesma regra do pedido manual).
            if (!request.PrevisaoMinutos.HasValue)
            {
                var delivery = await _queue.GetSettingsAsync(estabelecimentoId);
                if (delivery != null)
                {
                    request.PrevisaoMinutos = delivery.DefaultDeliveryMinutes;
                }
            }

            var manual = ManualOrderRules.Validate(request);
            var hasItems = request.Itens is { Count: > 0 };

            if (!hasItems)
            {
                if (rules.EnforceStoreRules)
                {
                    throw new DeliveryDomainException(422, "INVALID_ORDER_ITEMS",
                        "Informe os itens do pedido: esta origem calcula o preco a partir do cardapio.");
                }
                // Formato antigo (items em texto + valor digitado): mantido como esta; so avisos.
                var legacy = OrderCoreRules.Evaluate(restaurant, manual.Value, manual.Latitude, manual.Longitude, rules, feeOverride: null);
                return manual with
                {
                    Origem = origin,
                    OrigemRef = origemRef,
                    ConversaId = request.ConversaId,
                    Rascunho = request.Rascunho,
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
            var evaluation = OrderCoreRules.Evaluate(restaurant, priced.Subtotal, manual.Latitude, manual.Longitude, rules, request.TaxaEntrega);
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
                Rascunho = request.Rascunho,
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
