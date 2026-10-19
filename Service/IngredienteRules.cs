using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using APIBack.DTOs.Inteligencia;

namespace APIBack.Service
{
    /// <summary>Ingrediente já validado e normalizado, pronto para gravar.</summary>
    public sealed record IngredienteInput(
        string Nome, string Unidade, string? Categoria, string? Observacao, string? FotoUrl, string? FotoCredito,
        int? Versao, string? ChaveIdempotencia);

    /// <summary>Custo já convertido para a unidade base (g, ml ou un).</summary>
    public sealed record CustoCalculado(
        decimal QuantidadeCompra, string UnidadeCompra, decimal ValorCompra, decimal CustoBase, bool Gratuito,
        string? Motivo, string? ChaveIdempotencia);

    /// <summary>
    /// Regras de Ingredientes (docs/gestao/03-ingredientes.md): conversão de compra para custo base, validação,
    /// variação de preço e nomes parecidos. Pura, sem banco.
    /// </summary>
    public static class IngredienteRules
    {
        public const int PrecoAlteradoDias = 30;
        public const int CasasDoCusto = 8;
        private const decimal LimiteQuantidade = 1_000_000_000m;
        private const decimal LimiteValor = 1_000_000_000m;

        private static readonly Dictionary<string, (string Base, decimal Fator)> Compra = new(StringComparer.Ordinal)
        {
            ["g"] = ("g", 1m), ["kg"] = ("g", 1000m),
            ["ml"] = ("ml", 1m), ["l"] = ("ml", 1000m),
            ["un"] = ("un", 1m),
        };

        public static bool UnidadeBaseValida(string? unidade) => unidade is "g" or "ml" or "un";

        public static IngredienteInput Validate(IngredienteRequest? request, bool edicao)
        {
            if (request == null) throw Invalid("INGREDIENTE_INVALIDO", "Informe os dados do ingrediente.");
            var nome = Clean(request.Nome, 120);
            if (nome == null) throw Invalid("INGREDIENTE_NOME", "Informe o nome do ingrediente.", "nome");
            var unidade = (request.Unidade ?? string.Empty).Trim().ToLowerInvariant();
            if (!UnidadeBaseValida(unidade)) throw Invalid("INGREDIENTE_UNIDADE", "A unidade base precisa ser g, ml ou un.", "unidade");
            if (edicao && (request.Versao is null || request.Versao <= 0))
                throw Invalid("INGREDIENTE_VERSAO", "Recarregue o ingrediente antes de salvar.", "versao");
            var foto = Clean(request.FotoUrl, 500);
            if (foto != null && !(Uri.TryCreate(foto, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"))
                throw Invalid("INGREDIENTE_FOTO", "A foto precisa ser um endereço http ou https.", "fotoUrl");
            return new IngredienteInput(nome, unidade, Clean(request.Categoria, 60), Clean(request.Observacao, 500),
                foto, Clean(request.FotoCredito, 300), request.Versao, Clean(request.ChaveIdempotencia, 80));
        }

        /// <summary>"Compro 2 kg por R$ 60" de um ingrediente em gramas dá R$ 0,03 por g.</summary>
        public static CustoCalculado Calculate(string unidadeBase, CustoRequest? request)
        {
            if (request == null) throw Invalid("CUSTO_INVALIDO", "Informe o preço de compra.");
            var unidade = (request.UnidadeCompra ?? string.Empty).Trim().ToLowerInvariant();
            if (!Compra.TryGetValue(unidade, out var conversao) || conversao.Base != unidadeBase)
            {
                var aceitas = string.Join(" ou ", Compra.Where(item => item.Value.Base == unidadeBase).Select(item => item.Key));
                throw Invalid("CUSTO_UNIDADE", $"Este ingrediente é medido em {unidadeBase}; informe a compra em {aceitas}.", "unidadeCompra");
            }
            if (request.QuantidadeCompra <= 0 || request.QuantidadeCompra > LimiteQuantidade)
                throw Invalid("CUSTO_QUANTIDADE", "A quantidade comprada precisa ser maior que zero.", "quantidadeCompra");
            if (request.ValorCompra < 0 || request.ValorCompra > LimiteValor)
                throw Invalid("CUSTO_VALOR", "O valor pago não pode ser negativo.", "valorCompra");
            var motivo = Clean(request.Motivo, 300);
            if (request.ValorCompra == 0)
            {
                // Zero nunca é "não sei": só vale como gratuito, com motivo, para não contaminar as receitas.
                if (!request.Gratuito) throw Invalid("CUSTO_ZERO", "Valor zero precisa ser confirmado como gratuito. Se não sabe o preço, deixe o ingrediente sem custo.", "valorCompra");
                if (motivo == null) throw Invalid("CUSTO_MOTIVO", "Explique por que o ingrediente é gratuito.", "motivo");
            }
            else if (request.Gratuito) throw Invalid("CUSTO_GRATUITO", "Ingrediente gratuito não tem valor pago.", "gratuito");
            var custoBase = decimal.Round(request.ValorCompra / (request.QuantidadeCompra * conversao.Fator), CasasDoCusto, MidpointRounding.ToEven);
            if (request.ValorCompra > 0 && custoBase == 0)
                throw Invalid("CUSTO_VALOR", "O valor é pequeno demais para essa quantidade.", "valorCompra");
            return new CustoCalculado(request.QuantidadeCompra, unidade, request.ValorCompra, custoBase, request.ValorCompra == 0, motivo, Clean(request.ChaveIdempotencia, 80));
        }

        /// <summary>Nulo quando não há preço anterior ou ele é igual ao atual; sem percentual quando o anterior era zero.</summary>
        public static VariacaoDto? Variation(decimal atual, decimal? anterior, DateTimeOffset? anteriorDesde)
        {
            if (anterior is null || anteriorDesde is null || anterior == atual) return null;
            var diferenca = atual - anterior.Value;
            return new VariacaoDto
            {
                Anterior = anterior.Value, Atual = atual, Diferenca = diferenca, AnteriorDesde = anteriorDesde.Value,
                Percentual = anterior.Value == 0 ? null : decimal.Round(diferenca / anterior.Value * 100m, 2, MidpointRounding.AwayFromZero),
            };
        }

        public static bool PrecoAlterado(VariacaoDto? variacao, DateTimeOffset vigenciaAtual, DateTimeOffset agora) =>
            variacao != null && agora - vigenciaAtual <= TimeSpan.FromDays(PrecoAlteradoDias);

        /// <summary>Minúsculas, sem acento e sem espaço sobrando: base para achar nomes parecidos.</summary>
        public static string NormalizeName(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var decomposed = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(decomposed.Length);
            foreach (var ch in decomposed)
            {
                if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.NonSpacingMark) builder.Append(ch);
            }
            return string.Join(' ', builder.ToString().Normalize(NormalizationForm.FormC).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }

        /// <summary>Nomes já cadastrados que se confundem com o novo: iguais, ou um contido no outro (4+ letras).</summary>
        public static List<string> SimilarNames(string nome, IEnumerable<string> existing)
        {
            var wanted = NormalizeName(nome);
            if (wanted.Length == 0) return new List<string>();
            return existing.Where(name =>
            {
                var other = NormalizeName(name);
                return other == wanted
                    || (Math.Min(other.Length, wanted.Length) >= 4 && (other.Contains(wanted, StringComparison.Ordinal) || wanted.Contains(other, StringComparison.Ordinal)));
            }).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static string NormalizeFilter(string? filtro) => filtro?.Trim().ToLowerInvariant() switch
        {
            "sem_custo" => "sem_custo",
            "preco_alterado" => "preco_alterado",
            "inativos" => "inativos",
            _ => "todos",
        };

        private static string? Clean(string? value, int max)
        {
            var text = value?.Trim();
            if (string.IsNullOrEmpty(text)) return null;
            if (text.Length > max) throw Invalid("TEXTO_LONGO", $"Use no máximo {max} caracteres.");
            return text;
        }

        private static DeliveryDomainException Invalid(string code, string message, string? field = null) =>
            new(422, code, message, field == null ? null : new { campo = field });
    }
}
