using System;

namespace APIBack.Automation.Models
{
    public class UsuarioTenant
    {
        public int Id { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public bool IsSuperAdmin { get; set; }
        public Guid? UltimoEstabelecimentoAcessado { get; set; }
        public DateTime? DeletedAt { get; set; }
        public bool IsAtivo => !DeletedAt.HasValue;
    }

    public class UsuarioEstabelecimentoVinculo
    {
        public Guid VinculoId { get; set; }
        public Guid EstabelecimentoId { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string TipoEstabelecimento { get; set; } = string.Empty;
        public string StatusVinculo { get; set; } = string.Empty;
        public string StatusEstabelecimento { get; set; } = string.Empty;
        public bool? EstabelecimentoAtivo { get; set; }
        public bool? VinculoAtivo { get; set; }
        public string? TipoAcesso { get; set; }
        public string? Plano { get; set; }
    }

    public class EstabelecimentoAtivoResumo
    {
        public Guid EstabelecimentoId { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string TipoEstabelecimento { get; set; } = string.Empty;
        public string StatusEstabelecimento { get; set; } = string.Empty;
        public bool? EstabelecimentoAtivo { get; set; }
        public string? Plano { get; set; }
    }

    public class EstabelecimentoDetalhe
    {
        public Guid Id { get; set; }
        public Guid EmpresaId { get; set; }
        public string EmpresaNome { get; set; } = string.Empty;
        public string Nome { get; set; } = string.Empty;
        public string TipoEstabelecimento { get; set; } = string.Empty;
        public string? Plano { get; set; }
        public string Status { get; set; } = string.Empty;
        public bool? Ativo { get; set; }
    }

    public class UsuarioEstabelecimentoAcesso
    {
        public Guid Id { get; set; }
        public Guid EstabelecimentoId { get; set; }
        public int UsuarioId { get; set; }
        public string Status { get; set; } = string.Empty;
        public bool? VinculoAtivo { get; set; }
        public string? TipoAcesso { get; set; }
        public string? PermissoesCustomizadas { get; set; }
    }

    public class UsuarioEmpresaAcesso
    {
        public Guid Id { get; set; }
        public Guid EmpresaId { get; set; }
        public int UsuarioId { get; set; }
        public string Status { get; set; } = string.Empty;
        public bool? VinculoAtivo { get; set; }
        public string? TipoAcesso { get; set; }
    }
}
