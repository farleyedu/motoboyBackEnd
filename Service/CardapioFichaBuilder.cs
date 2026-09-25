using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using APIBack.DTOs.Cardapio;
using APIBack.DTOs.Delivery;
using APIBack.Model.Cardapio;

namespace APIBack.Service
{
    /// <summary>Monta a ficha de atendimento. Pura: recebe tudo carregado e nao toca no banco.</summary>
    public static class CardapioFichaBuilder
    {
        private static readonly JsonSerializerOptions HashJson = new(JsonSerializerDefaults.Web);

        public static FichaAtendimentoDto Build(
            RestaurantSettingsDto restaurant,
            IEnumerable<CardapioCategoria> categorias,
            IEnumerable<CardapioProduto> produtos,
            IReadOnlyDictionary<Guid, ProdutoAtendimentoDto> atendimento,
            DateTimeOffset now)
        {
            var porCategoria = produtos
                .GroupBy(produto => produto.CategoriaId)
                .ToDictionary(group => group.Key, group => group.ToList());

            var ficha = new FichaAtendimentoDto
            {
                GeradoEm = now,
                Estabelecimento = new FichaEstabelecimentoDto
                {
                    Id = restaurant.EstabelecimentoId,
                    Nome = restaurant.NomeFantasia,
                    AceitaPedidos = restaurant.AceitaPedidos,
                    PedidoMinimo = restaurant.PedidoMinimo ?? 0m,
                    TaxaEntregaFixa = restaurant.TaxaEntregaFixa ?? 0m,
                    TaxaEntregaPorKm = restaurant.TaxaEntregaPorKm ?? 0m,
                    RaioEntregaKm = restaurant.RaioEntregaKm,
                    TempoPreparoMin = restaurant.TempoPreparoMin
                }
            };

            foreach (var categoria in categorias.OrderBy(c => c.Ordem).ThenBy(c => c.Nome, StringComparer.OrdinalIgnoreCase))
            {
                if (!porCategoria.TryGetValue(categoria.Id, out var itens) || itens.Count == 0) continue;

                ficha.Categorias.Add(new FichaCategoriaDto
                {
                    Id = categoria.Id,
                    Nome = categoria.Nome,
                    Produtos = itens
                        .OrderBy(p => p.Ordem)
                        .ThenBy(p => p.Nome, StringComparer.OrdinalIgnoreCase)
                        .Select(produto => MapProduto(produto, atendimento))
                        .ToList()
                });
            }

            ficha.Versao = ComputeVersion(ficha);
            return ficha;
        }

        /// <summary>Hash do conteudo (sem versao nem horario de geracao): estavel enquanto o cardapio nao muda.</summary>
        public static string ComputeVersion(FichaAtendimentoDto ficha)
        {
            var content = new { ficha.Estabelecimento, ficha.Categorias };
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(content, HashJson)));
            return Convert.ToHexString(bytes).ToLowerInvariant()[..16];
        }

        private static FichaProdutoDto MapProduto(CardapioProduto produto, IReadOnlyDictionary<Guid, ProdutoAtendimentoDto> atendimento)
        {
            atendimento.TryGetValue(produto.Id, out var extra);
            return new FichaProdutoDto
            {
                Id = produto.Id,
                Nome = produto.Nome,
                Descricao = produto.Descricao,
                Preco = produto.PrecoBase,
                PrecoDe = produto.PrecoDe,
                Disponivel = produto.Disponivel,
                Apelidos = extra?.Apelidos?.ToList() ?? new List<string>(),
                Instrucoes = extra?.Instrucoes,
                Restricoes = extra?.Restricoes,
                TempoExtraPreparoMin = extra?.TempoExtraPreparoMin,
                Grupos = produto.Grupos
                    .OrderBy(g => g.Ordem)
                    .ThenBy(g => g.Nome, StringComparer.OrdinalIgnoreCase)
                    .Select(grupo => new FichaGrupoDto
                    {
                        Id = grupo.Id,
                        Nome = grupo.Nome,
                        Tipo = grupo.Tipo,
                        Min = grupo.MinSelecionados,
                        Max = grupo.MaxSelecionados,
                        Itens = grupo.Itens
                            .OrderBy(i => i.Ordem)
                            .ThenBy(i => i.Nome, StringComparer.OrdinalIgnoreCase)
                            .Select(item => new FichaItemDto { Id = item.Id, Nome = item.Nome, Preco = item.Preco })
                            .ToList()
                    })
                    .ToList()
            };
        }

        /// <summary>Normaliza os campos de atendimento antes de gravar (aparar, limitar, sem repetidos).</summary>
        public static ProdutoAtendimentoDto Normalize(ProdutoAtendimentoDto? input)
        {
            if (input == null)
            {
                throw new DeliveryDomainException(400, "INVALID_REQUEST", "Corpo da requisicao obrigatorio.");
            }

            var apelidos = (input.Apelidos ?? new List<string>())
                .Select(a => a?.Trim() ?? string.Empty)
                .Where(a => a.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (apelidos.Count > 20)
            {
                throw new DeliveryDomainException(422, "INVALID_REQUEST", "Informe no maximo 20 apelidos.");
            }
            if (apelidos.Any(a => a.Length > 60))
            {
                throw new DeliveryDomainException(422, "INVALID_REQUEST", "Cada apelido aceita no maximo 60 caracteres.");
            }

            var instrucoes = Clean(input.Instrucoes, 1000, "instrucoes");
            var restricoes = Clean(input.Restricoes, 500, "restricoes");
            if (input.TempoExtraPreparoMin is < 0 or > 240)
            {
                throw new DeliveryDomainException(422, "INVALID_REQUEST", "O tempo extra de preparo deve ficar entre 0 e 240 minutos.");
            }

            return new ProdutoAtendimentoDto
            {
                Apelidos = apelidos,
                Instrucoes = instrucoes,
                Restricoes = restricoes,
                TempoExtraPreparoMin = input.TempoExtraPreparoMin
            };
        }

        private static string? Clean(string? value, int max, string field)
        {
            var trimmed = value?.Trim();
            if (string.IsNullOrEmpty(trimmed)) return null;
            if (trimmed.Length > max)
            {
                throw new DeliveryDomainException(422, "INVALID_REQUEST", $"O campo {field} aceita no maximo {max} caracteres.");
            }
            return trimmed;
        }
    }
}
