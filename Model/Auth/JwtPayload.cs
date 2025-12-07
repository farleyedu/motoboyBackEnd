using System;
using System.Collections.Generic;

namespace APIBack.Model.Auth
{
    public class JwtPayload
    {
        public int UserId { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public bool IsSuperAdmin { get; set; }
        public Guid? EstabelecimentoId { get; set; }
        public string? EstabelecimentoNome { get; set; }
        public string? TipoEstabelecimento { get; set; }
        public string? TipoAcesso { get; set; }
        public Guid? VinculoId { get; set; }
        public Dictionary<string, List<string>> Permissoes { get; set; } = new();
        public DateTime ExpiresAt { get; set; }
        public DateTime IssuedAt { get; set; }
    }
}

