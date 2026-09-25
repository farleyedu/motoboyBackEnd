using System;
using System.Collections.Generic;
using System.Linq;
using APIBack.DTOs.Delivery;
using APIBack.Model.Cardapio;

namespace APIBack.Service
{
    /// <summary>Adicional escolhido, copiado do cardapio no momento do pedido.</summary>
    public sealed class PricedAddon
    {
        public Guid Id { get; init; }
        public string Nome { get; init; } = string.Empty;
        public decimal Preco { get; init; }
    }

    /// <summary>Linha do pedido ja precificada. Nome e preco sao copias (congelados).</summary>
    public sealed class PricedLine
    {
        public Guid? ProdutoId { get; init; }
        public string Nome { get; init; } = string.Empty;
        public int Quantidade { get; init; }
        public decimal PrecoUnitario { get; init; }
        public string? Observacao { get; init; }
        public IReadOnlyList<PricedAddon> Adicionais { get; init; } = Array.Empty<PricedAddon>();

        public decimal Total => decimal.Round(Quantidade * (PrecoUnitario + Adicionais.Sum(a => a.Preco)), 2);
    }

    public sealed class PricedOrder
    {
        public IReadOnlyList<PricedLine> Lines { get; init; } = Array.Empty<PricedLine>();
        public decimal Subtotal => decimal.Round(Lines.Sum(l => l.Total), 2);
    }

    /// <summary>
    /// Preco do pedido a partir do CARDAPIO (D4: a fonte da verdade do preco e sempre o cardapio).
    /// Regra pura: recebe os produtos ja carregados do estabelecimento e devolve as linhas com
    /// nome e preco copiados. Mesma logica de adicionais (minimo/maximo por grupo) do cardapio web.
    /// </summary>
    public static class PedidoPricing
    {
        public const int MaxLines = 100;
        public const int MaxQuantity = 100;

        public static PricedOrder Price(
            IReadOnlyList<PedidoItemRequest>? items,
            IReadOnlyDictionary<Guid, CardapioProduto> products,
            bool allowFreeLines)
        {
            if (items == null || items.Count == 0)
            {
                throw Invalid("Informe ao menos um item no pedido.");
            }
            if (items.Count > MaxLines)
            {
                throw Invalid($"O pedido aceita no maximo {MaxLines} itens.");
            }

            var errors = new List<string>();
            var lines = new List<PricedLine>(items.Count);
            for (var index = 0; index < items.Count; index++)
            {
                var item = items[index];
                var label = $"itens[{index}]";
                if (item == null)
                {
                    errors.Add($"{label}: item invalido.");
                    continue;
                }
                if (item.Quantidade is < 1 or > MaxQuantity)
                {
                    errors.Add($"{label}: a quantidade deve ficar entre 1 e {MaxQuantity}.");
                    continue;
                }
                var observacao = Trim(item.Observacao, 300, label + ".observacao", errors);

                if (item.ProdutoId.HasValue && item.ProdutoId.Value != Guid.Empty)
                {
                    if (!products.TryGetValue(item.ProdutoId.Value, out var product))
                    {
                        errors.Add($"{label}: produto nao encontrado ou indisponivel para venda.");
                        continue;
                    }
                    var addons = PriceAddons(item, product, label, errors);
                    if (addons == null) continue;
                    lines.Add(new PricedLine
                    {
                        ProdutoId = product.Id,
                        Nome = product.Nome,
                        Quantidade = item.Quantidade,
                        PrecoUnitario = decimal.Round(product.PrecoBase, 2),
                        Observacao = observacao,
                        Adicionais = addons
                    });
                    continue;
                }

                // Linha avulsa (sem produto do cardapio): so nas origens que permitem.
                if (!allowFreeLines)
                {
                    errors.Add($"{label}: informe o produto do cardapio (linha avulsa nao e permitida nesta origem).");
                    continue;
                }
                var nome = Trim(item.Nome, 200, label + ".nome", errors);
                if (nome == null)
                {
                    errors.Add($"{label}: informe o nome do item.");
                    continue;
                }
                if (item.PrecoUnitario is null or < 0 or > 100_000)
                {
                    errors.Add($"{label}: informe um preco entre 0 e 100000.");
                    continue;
                }
                lines.Add(new PricedLine
                {
                    ProdutoId = null,
                    Nome = nome,
                    Quantidade = item.Quantidade,
                    PrecoUnitario = decimal.Round(item.PrecoUnitario.Value, 2),
                    Observacao = observacao
                });
            }

            if (errors.Count > 0)
            {
                throw new DeliveryDomainException(422, "INVALID_ORDER_ITEMS", errors[0], errors);
            }
            return new PricedOrder { Lines = lines };
        }

        private static List<PricedAddon>? PriceAddons(PedidoItemRequest item, CardapioProduto product, string label, List<string> errors)
        {
            var selected = (item.AdicionalItemIds ?? new List<Guid>())
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToList();
            var byId = product.Grupos
                .SelectMany(group => group.Itens.Select(addon => (Group: group, Addon: addon)))
                .GroupBy(x => x.Addon.Id)
                .ToDictionary(g => g.Key, g => g.First());

            var addons = new List<PricedAddon>();
            var perGroup = new Dictionary<Guid, int>();
            var ok = true;
            foreach (var id in selected)
            {
                if (!byId.TryGetValue(id, out var found))
                {
                    errors.Add($"{label}: um adicional informado nao pertence ao produto.");
                    ok = false;
                    continue;
                }
                addons.Add(new PricedAddon { Id = found.Addon.Id, Nome = found.Addon.Nome, Preco = decimal.Round(found.Addon.Preco, 2) });
                perGroup[found.Group.Id] = perGroup.TryGetValue(found.Group.Id, out var n) ? n + 1 : 1;
            }

            foreach (var group in product.Grupos)
            {
                var count = perGroup.TryGetValue(group.Id, out var n) ? n : 0;
                if (count < group.MinSelecionados)
                {
                    errors.Add($"{label}: selecione pelo menos {group.MinSelecionados} item(ns) em '{group.Nome}'.");
                    ok = false;
                }
                if (count > group.MaxSelecionados)
                {
                    errors.Add($"{label}: selecione no maximo {group.MaxSelecionados} item(ns) em '{group.Nome}'.");
                    ok = false;
                }
            }
            return ok ? addons : null;
        }

        private static string? Trim(string? value, int max, string field, List<string> errors)
        {
            var trimmed = value?.Trim();
            if (string.IsNullOrEmpty(trimmed)) return null;
            if (trimmed.Length > max)
            {
                errors.Add($"{field}: excede {max} caracteres.");
                return null;
            }
            return trimmed;
        }

        private static DeliveryDomainException Invalid(string message) =>
            new(422, "INVALID_ORDER_ITEMS", message);
    }
}
