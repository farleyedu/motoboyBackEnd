using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using APIBack.Automation.Models;
using APIBack.Service.Interface;
using Microsoft.Extensions.Caching.Memory;

namespace APIBack.Automation.Services
{
    public class FaqCatalogProvider
    {
        private static readonly MemoryCacheEntryOptions CacheOptions = new()
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60),
            Priority = CacheItemPriority.Normal
        };

        private static readonly Regex TokenRegex = new(@"[a-z0-9]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
        {
            "a", "ao", "aos", "as", "com", "da", "das", "de", "do", "dos", "e", "em", "na", "nas",
            "no", "nos", "o", "os", "ou", "para", "pra", "por", "qual", "que", "quero", "saber",
            "se", "sobre", "tem", "vcs", "voce", "voces",
            "servico", "servicos", "servico", "servicos"
        };

        private readonly IEstabelecimentoFaqService _faqService;
        private readonly IMemoryCache _cache;

        public FaqCatalogProvider(
            IEstabelecimentoFaqService faqService,
            IMemoryCache cache)
        {
            _faqService = faqService;
            _cache = cache;
        }

        public async Task<IReadOnlyList<FaqCatalogItem>> ObterFaqAtivaAsync(Guid idEstabelecimento)
        {
            var cacheKey = $"faq:catalogo:{idEstabelecimento}";
            if (_cache.TryGetValue(cacheKey, out IReadOnlyList<FaqCatalogItem>? cached) && cached != null)
            {
                return cached;
            }

            var itens = await _faqService.ListarTodosAsync(idEstabelecimento);
            var ativos = itens
                .Where(item => item.Ativo)
                .OrderBy(item => item.Categoria)
                .ThenBy(item => item.Ordem)
                .ThenBy(item => item.Pergunta)
                .Select(item => new FaqCatalogItem
                {
                    Id = item.Id,
                    Pergunta = item.Pergunta,
                    Resposta = item.Resposta,
                    Categoria = item.Categoria,
                    PalavrasChave = item.PalavrasChave
                })
                .ToArray();

            _cache.Set(cacheKey, ativos, CacheOptions);
            return ativos;
        }

        public async Task<FaqCatalogMatch?> MatchStrongAsync(Guid idEstabelecimento, string mensagemTexto)
        {
            if (string.IsNullOrWhiteSpace(mensagemTexto))
            {
                return null;
            }

            var normalizado = Normalize(mensagemTexto);
            if (string.IsNullOrWhiteSpace(normalizado) || normalizado.Length < 4 || Regex.IsMatch(normalizado, @"^\d+$"))
            {
                return null;
            }

            var faq = await ObterFaqAtivaAsync(idEstabelecimento);
            var tokensMensagem = Tokenize(normalizado);
            FaqCatalogMatch? melhor = null;

            foreach (var item in faq)
            {
                var score = CalcularScore(item, normalizado, tokensMensagem);
                if (score < 10)
                {
                    continue;
                }

                if (melhor == null || score > melhor.Score)
                {
                    melhor = new FaqCatalogMatch(item, score);
                }
            }

            return melhor;
        }

        public async Task<string?> BuildCompactPromptAsync(Guid idEstabelecimento)
        {
            var faq = await ObterFaqAtivaAsync(idEstabelecimento);
            if (faq.Count == 0)
            {
                return null;
            }

            var builder = new StringBuilder();
            builder.AppendLine("FAQ disponivel no bot:");

            foreach (var item in faq.Take(20))
            {
                var palavras = item.PalavrasChave
                    .Where(keyword => !string.IsNullOrWhiteSpace(keyword))
                    .Take(4)
                    .ToArray();

                builder.Append("- categoria=");
                builder.Append(string.IsNullOrWhiteSpace(item.Categoria) ? "geral" : item.Categoria.Trim());
                builder.Append(" | pergunta=");
                builder.Append(item.Pergunta.Trim());

                if (palavras.Length > 0)
                {
                    builder.Append(" | palavras-chave=");
                    builder.Append(string.Join(", ", palavras));
                }

                builder.AppendLine();
            }

            return builder.ToString().TrimEnd();
        }

        private static int CalcularScore(FaqCatalogItem item, string mensagemNormalizada, HashSet<string> tokensMensagem)
        {
            var score = 0;
            var pergunta = Normalize(item.Pergunta);
            var categoria = Normalize(item.Categoria);

            if (!string.IsNullOrWhiteSpace(pergunta) && mensagemNormalizada.Contains(pergunta, StringComparison.Ordinal))
            {
                score += 10;
            }

            if (!string.IsNullOrWhiteSpace(categoria) && mensagemNormalizada.Contains(categoria, StringComparison.Ordinal))
            {
                score += 3;
            }

            foreach (var palavra in item.PalavrasChave.Where(item => !string.IsNullOrWhiteSpace(item)))
            {
                var normalizada = Normalize(palavra);
                if (normalizada.Length < 3)
                {
                    continue;
                }

                if (mensagemNormalizada.Contains(normalizada, StringComparison.Ordinal))
                {
                    score += 6;
                }
            }

            var tokensPergunta = Tokenize(pergunta);
            var intersecao = tokensPergunta.Count(tokensMensagem.Contains);
            if (intersecao >= 2)
            {
                score += intersecao * 2;
            }

            return score;
        }

        private static string Normalize(string? texto)
        {
            if (string.IsNullOrWhiteSpace(texto))
            {
                return string.Empty;
            }

            var formD = texto.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(formD.Length);
            foreach (var ch in formD)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                {
                    builder.Append(ch);
                }
            }

            return Regex.Replace(builder.ToString().Normalize(NormalizationForm.FormC), @"\s+", " ").Trim();
        }

        private static HashSet<string> Tokenize(string texto)
        {
            return TokenRegex.Matches(texto)
                .Select(match => match.Value)
                .Where(item => !StopWords.Contains(item))
                .ToHashSet(StringComparer.Ordinal);
        }
    }
}
