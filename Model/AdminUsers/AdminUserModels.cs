using System;
using System.Collections.Generic;

namespace APIBack.Model.AdminUsers
{
    public class AdminActorSnapshot
    {
        public int UserId { get; set; }
        public bool IsSuperAdmin { get; set; }
        public Guid? EmpresaId { get; set; }
        public string EmpresaNome { get; set; } = string.Empty;
        public string? CompanyRole { get; set; }
        public Guid? EstabelecimentoId { get; set; }
        public string EstabelecimentoNome { get; set; } = string.Empty;
        public string TipoEstabelecimento { get; set; } = string.Empty;
        public string? EstablishmentRole { get; set; }
    }

    public class AdminEmpresaOption
    {
        public Guid Id { get; set; }
        public string Nome { get; set; } = string.Empty;
    }

    public class AdminEstabelecimentoOption
    {
        public Guid Id { get; set; }
        public Guid EmpresaId { get; set; }
        public string EmpresaNome { get; set; } = string.Empty;
        public string Nome { get; set; } = string.Empty;
        public string TipoEstabelecimento { get; set; } = string.Empty;
        public bool IsCurrent { get; set; }
    }

    public class AdminTargetEstabelecimento
    {
        public Guid Id { get; set; }
        public Guid EmpresaId { get; set; }
        public string EmpresaNome { get; set; } = string.Empty;
        public string Nome { get; set; } = string.Empty;
        public string TipoEstabelecimento { get; set; } = string.Empty;
        public bool Ativo { get; set; }
        public string Status { get; set; } = string.Empty;
    }

    public class AdminUsuarioListRow
    {
        public int Id { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public Guid EmpresaId { get; set; }
        public string EmpresaNome { get; set; } = string.Empty;
        public string? CompanyRole { get; set; }
        public Guid EstabelecimentoId { get; set; }
        public string EstabelecimentoNome { get; set; } = string.Empty;
        public string TipoEstabelecimento { get; set; } = string.Empty;
        public string? EstablishmentRole { get; set; }
    }

    public class AdminCreateUserCommand
    {
        public string Nome { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string SenhaHash { get; set; } = string.Empty;
        public Guid EmpresaId { get; set; }
        public string CompanyRole { get; set; } = "colaborador";
        public string? EstablishmentRole { get; set; }
        public IReadOnlyCollection<Guid> EstabelecimentoIds { get; set; } = Array.Empty<Guid>();
        public int CreatedByUserId { get; set; }
    }

    public class AdminCreatedUserSummary
    {
        public int Id { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string Status { get; set; } = "ativo";
        public Guid EmpresaId { get; set; }
        public string EmpresaNome { get; set; } = string.Empty;
        public string? CompanyRole { get; set; }
        public List<AdminCreatedUserEstabelecimento> Estabelecimentos { get; set; } = new();
    }

    public class AdminCreatedUserEstabelecimento
    {
        public Guid Id { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string TipoEstabelecimento { get; set; } = string.Empty;
        public string? TipoAcesso { get; set; }
    }
}
