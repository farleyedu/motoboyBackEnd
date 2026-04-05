using System;
using System.Collections.Generic;

namespace APIBack.DTOs.Configuracoes
{
    public class EstabelecimentoFaqDto
    {
        public Guid Id { get; set; }
        public Guid EstabelecimentoId { get; set; }
        public string Pergunta { get; set; } = string.Empty;
        public string Resposta { get; set; } = string.Empty;
        public string Categoria { get; set; } = string.Empty;
        public int Ordem { get; set; }
        public bool Ativo { get; set; }
        public List<string> PalavrasChave { get; set; } = new();
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime? DeletedAt { get; set; }
    }

    public class SalvarEstabelecimentoFaqRequest
    {
        public string Pergunta { get; set; } = string.Empty;
        public string Resposta { get; set; } = string.Empty;
        public string Categoria { get; set; } = string.Empty;
        public int Ordem { get; set; } = 1;
        public bool Ativo { get; set; } = true;
        public List<string>? PalavrasChave { get; set; }
    }
}
