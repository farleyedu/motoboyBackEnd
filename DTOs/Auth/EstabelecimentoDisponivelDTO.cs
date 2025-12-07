using System;

namespace APIBack.DTOs.Auth
{
    public class EstabelecimentoDisponivelDTO
    {
        public Guid Id { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string Tipo { get; set; } = string.Empty;
        public string TipoAcesso { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public bool IsAtual { get; set; }
    }
}

