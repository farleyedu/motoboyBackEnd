using System;
using System.Collections.Generic;

namespace APIBack.Model.Auth
{
    public class JwtPayload
    {
        public int? UserId { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public bool IsSuperAdmin { get; set; }
        public Guid? EmpresaId { get; set; }
        public string? EmpresaNome { get; set; }
        public string? TipoAcessoEmpresa { get; set; }
        public Guid? EmpresaVinculoId { get; set; }
        public Guid? EstabelecimentoId { get; set; }
        public string? EstabelecimentoNome { get; set; }
        public string? TipoEstabelecimento { get; set; }
        public List<string> EstabelecimentoModulosAtivos { get; set; } = new();
        public string? TipoAcesso { get; set; }
        public Guid? VinculoId { get; set; }
        public Dictionary<string, List<string>> Permissoes { get; set; } = new();
        public Guid? MotoboySessionId { get; set; }
        public int? MotoboyId { get; set; }
        public string? ClientType { get; set; }
        public string TokenUse { get; set; } = "identity";
        public long? SessionEpoch { get; set; }
        public string? Scope { get; set; }
        public DateTime ExpiresAt { get; set; }
        public DateTime IssuedAt { get; set; }
    }
}
