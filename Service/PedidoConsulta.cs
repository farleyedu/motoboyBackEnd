using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using APIBack.DTOs.Delivery;
using APIBack.Model.Delivery;
using APIBack.Model.Enum;

namespace APIBack.Service
{
    /// <summary>Filtros da lista de pedidos ja validados e normalizados.</summary>
    public sealed class PedidoFiltro
    {
        public int[] Status { get; init; } = Array.Empty<int>();
        public string[] Origem { get; init; } = Array.Empty<string>();
        public string? De { get; init; }
        public string? Ate { get; init; }
        /// <summary>Busca por numero (quando so digitos curtos) ou por texto.</summary>
        public int? BuscaId { get; init; }
        public string? BuscaDigitos { get; init; }
        public string? BuscaLike { get; init; }
        public Guid? ConversaId { get; init; }
        public int Page { get; init; } = 1;
        public int PageSize { get; init; } = 30;
        public int Offset => (Page - 1) * PageSize;

        public const int MaxPageSize = 100;

        private static readonly Regex IsoDate = new(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.Compiled);

        private static readonly IReadOnlyDictionary<string, int> StatusKeys = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["pendente"] = (int)StatusPedido.Pendente,
            ["em_rota"] = (int)StatusPedido.EmRota,
            ["concluido"] = (int)StatusPedido.Concluido,
            ["cancelado"] = (int)StatusPedido.Cancelado,
            ["atribuido"] = (int)StatusPedido.Atribuido,
            ["rascunho"] = (int)StatusPedido.Rascunho,
        };

        public static PedidoFiltro From(PedidoFiltroRequest? request)
        {
            request ??= new PedidoFiltroRequest();

            var status = Split(request.Status).Select(key =>
                StatusKeys.TryGetValue(key, out var value)
                    ? value
                    : throw new DeliveryDomainException(422, "INVALID_REQUEST", $"Status invalido: {key}.")).Distinct().ToArray();

            var origem = Split(request.Origem).Select(key =>
                PedidoOrigem.Normalize(key) is { } normalized && !string.IsNullOrWhiteSpace(key)
                    ? normalized
                    : throw new DeliveryDomainException(422, "INVALID_REQUEST", $"Origem invalida: {key}.")).Distinct().ToArray();

            var de = Date(request.De, "de");
            var ate = Date(request.Ate, "ate");
            if (de != null && ate != null && string.CompareOrdinal(de, ate) > 0)
            {
                throw new DeliveryDomainException(422, "INVALID_REQUEST", "A data inicial nao pode ser depois da final.");
            }

            var busca = request.Busca?.Trim();
            int? buscaId = null;
            string? digitos = null;
            string? like = null;
            if (!string.IsNullOrEmpty(busca))
            {
                if (busca.Length > 100) throw new DeliveryDomainException(422, "INVALID_REQUEST", "A busca aceita no maximo 100 caracteres.");
                var onlyDigits = new string(busca.TrimStart('#').Where(char.IsDigit).ToArray());
                var isNumber = busca.TrimStart('#').All(char.IsDigit) && onlyDigits.Length > 0;
                if (isNumber)
                {
                    digitos = onlyDigits;
                    if (onlyDigits.Length <= 9 && int.TryParse(onlyDigits, NumberStyles.None, CultureInfo.InvariantCulture, out var id)) buscaId = id;
                }
                like = "%" + busca.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            }

            Guid? conversaId = null;
            if (!string.IsNullOrWhiteSpace(request.ConversaId))
            {
                if (!Guid.TryParse(request.ConversaId, out var parsed))
                {
                    throw new DeliveryDomainException(422, "INVALID_REQUEST", "conversaId invalido.");
                }
                conversaId = parsed;
            }

            var pageSize = request.PageSize <= 0 ? 30 : Math.Min(request.PageSize, MaxPageSize);
            return new PedidoFiltro
            {
                Status = status,
                Origem = origem,
                De = de,
                Ate = ate,
                BuscaId = buscaId,
                BuscaDigitos = digitos,
                BuscaLike = like,
                ConversaId = conversaId,
                Page = Math.Max(1, request.Page),
                PageSize = pageSize
            };
        }

        private static IEnumerable<string> Split(string? value) =>
            (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        private static string? Date(string? value, string field)
        {
            var trimmed = value?.Trim();
            if (string.IsNullOrEmpty(trimmed)) return null;
            if (!IsoDate.IsMatch(trimmed) || !DateOnly.TryParseExact(trimmed, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            {
                throw new DeliveryDomainException(422, "INVALID_REQUEST", $"Data invalida em '{field}'. Use AAAA-MM-DD.");
            }
            return trimmed;
        }
    }

    /// <summary>
    /// Le a coluna legada pedido.items (texto livre ou JSON) e devolve linhas de pedido. Usada quando
    /// o pedido nao tem itens estruturados (pedido_item), ou seja, pedidos antigos e iFood.
    /// </summary>
    public static class LegacyItemsParser
    {
        private static readonly string[] NameKeys = { "nome", "name", "descricao", "description", "produto", "title", "item" };
        private static readonly string[] QuantityKeys = { "quantidade", "quantity", "qtd", "qty" };
        private static readonly string[] PriceKeys = { "preco", "price", "valor", "value", "total" };
        private static readonly Regex Line = new(@"^(\d+)\s*[xX]?\s+(.+)$", RegexOptions.Compiled);

        public static List<PedidoItemDto> Parse(string? raw)
        {
            var items = new List<PedidoItemDto>();
            if (string.IsNullOrWhiteSpace(raw)) return items;

            try
            {
                using var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var element in doc.RootElement.EnumerateArray())
                    {
                        var item = FromElement(element);
                        if (item != null) items.Add(item);
                    }
                    return items;
                }
            }
            catch (JsonException)
            {
                // texto livre do banco legado: cai no parse por linha abaixo
            }

            foreach (var part in raw.Split(new[] { '\n', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var match = Line.Match(part);
                items.Add(match.Success
                    ? new PedidoItemDto { Quantidade = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), Nome = match.Groups[2].Value.Trim() }
                    : new PedidoItemDto { Quantidade = 1, Nome = part });
            }
            return items;
        }

        public static int CountUnits(string? raw) => Parse(raw).Sum(item => Math.Max(1, item.Quantidade));

        private static PedidoItemDto? FromElement(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                var text = element.GetString()?.Trim();
                return string.IsNullOrEmpty(text) ? null : new PedidoItemDto { Nome = text };
            }
            if (element.ValueKind != JsonValueKind.Object) return null;

            var name = Pick(element, NameKeys);
            if (name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetString())) return null;
            var quantity = ToNumber(Pick(element, QuantityKeys));
            var price = ToNumber(Pick(element, PriceKeys));
            var qty = quantity is > 0 ? (int)Math.Min(quantity.Value, 1000m) : 1;
            return new PedidoItemDto
            {
                Nome = name.GetString()!.Trim(),
                Quantidade = qty,
                PrecoUnitario = price,
                Total = price.HasValue ? decimal.Round(price.Value * qty, 2) : null
            };
        }

        private static JsonElement Pick(JsonElement element, string[] keys)
        {
            foreach (var key in keys)
            {
                foreach (var property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase)) return property.Value;
                }
            }
            return default;
        }

        private static decimal? ToNumber(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)) return number;
            if (value.ValueKind == JsonValueKind.String
                && decimal.TryParse(value.GetString()?.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)) return parsed;
            return null;
        }
    }
}
