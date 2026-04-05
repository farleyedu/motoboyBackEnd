using System;
using System.Collections.Generic;

namespace APIBack.Automation.Models
{
    public sealed class FaqCatalogItem
    {
        public Guid Id { get; set; }
        public string Pergunta { get; set; } = string.Empty;
        public string Resposta { get; set; } = string.Empty;
        public string Categoria { get; set; } = string.Empty;
        public IReadOnlyList<string> PalavrasChave { get; set; } = Array.Empty<string>();
    }

    public sealed record FaqCatalogMatch(FaqCatalogItem Item, int Score);
}
