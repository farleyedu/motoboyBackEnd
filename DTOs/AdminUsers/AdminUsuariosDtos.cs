using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace APIBack.DTOs.AdminUsers
{
    public class AdminUsuariosContextoResponse
    {
        [JsonPropertyName("actor")]
        public AdminUsuariosActorDto Actor { get; set; } = new();

        [JsonPropertyName("empresaAtual")]
        public AdminUsuariosEmpresaDto? EmpresaAtual { get; set; }

        [JsonPropertyName("estabelecimentoAtual")]
        public AdminUsuariosEstabelecimentoDto? EstabelecimentoAtual { get; set; }

        [JsonPropertyName("empresasDisponiveis")]
        public List<AdminUsuariosEmpresaDto> EmpresasDisponiveis { get; set; } = new();

        [JsonPropertyName("estabelecimentosGerenciaveis")]
        public List<AdminUsuariosEstabelecimentoDto> EstabelecimentosGerenciaveis { get; set; } = new();

        [JsonPropertyName("allowedTargetUserTypes")]
        public List<AdminUsuariosTipoDto> AllowedTargetUserTypes { get; set; } = new();

        [JsonPropertyName("allowedManagerTypes")]
        public List<AdminUsuariosTipoDto> AllowedManagerTypes { get; set; } = new();

        [JsonPropertyName("formRules")]
        public AdminUsuariosFormRulesDto FormRules { get; set; } = new();
    }

    public class AdminUsuariosActorDto
    {
        [JsonPropertyName("userId")]
        public int UserId { get; set; }

        [JsonPropertyName("isSuperAdmin")]
        public bool IsSuperAdmin { get; set; }

        [JsonPropertyName("role")]
        public string Role { get; set; } = string.Empty;

        [JsonPropertyName("canViewUsers")]
        public bool CanViewUsers { get; set; }

        [JsonPropertyName("canCreateUsers")]
        public bool CanCreateUsers { get; set; }

        [JsonPropertyName("canCreateManagers")]
        public bool CanCreateManagers { get; set; }
    }

    public class AdminUsuariosEmpresaDto
    {
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("nome")]
        public string Nome { get; set; } = string.Empty;

        [JsonPropertyName("tipoAcesso")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? TipoAcesso { get; set; }
    }

    public class AdminUsuariosEstabelecimentoDto
    {
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("empresaId")]
        public Guid EmpresaId { get; set; }

        [JsonPropertyName("empresaNome")]
        public string EmpresaNome { get; set; } = string.Empty;

        [JsonPropertyName("nome")]
        public string Nome { get; set; } = string.Empty;

        [JsonPropertyName("tipoEstabelecimento")]
        public string TipoEstabelecimento { get; set; } = string.Empty;

        [JsonPropertyName("isCurrent")]
        public bool IsCurrent { get; set; }
    }

    public class AdminUsuariosTipoDto
    {
        [JsonPropertyName("code")]
        public string Code { get; set; } = string.Empty;

        [JsonPropertyName("label")]
        public string Label { get; set; } = string.Empty;
    }

    public class AdminUsuariosFormRulesDto
    {
        [JsonPropertyName("empresaLocked")]
        public bool EmpresaLocked { get; set; }

        [JsonPropertyName("estabelecimentoLocked")]
        public bool EstabelecimentoLocked { get; set; }

        [JsonPropertyName("allowMultiEstablishment")]
        public bool AllowMultiEstablishment { get; set; }
    }

    public class AdminUsuariosListResponse
    {
        [JsonPropertyName("items")]
        public List<AdminUsuarioListItemDto> Items { get; set; } = new();

        [JsonPropertyName("total")]
        public int Total { get; set; }
    }

    public class AdminUsuarioListItemDto
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("nome")]
        public string Nome { get; set; } = string.Empty;

        [JsonPropertyName("email")]
        public string Email { get; set; } = string.Empty;

        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [JsonPropertyName("companyRole")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? CompanyRole { get; set; }

        [JsonPropertyName("empresa")]
        public AdminUsuariosEmpresaDto Empresa { get; set; } = new();

        [JsonPropertyName("establishmentRoles")]
        public List<AdminUsuarioEstabelecimentoRoleDto> EstablishmentRoles { get; set; } = new();

        [JsonPropertyName("canEdit")]
        public bool CanEdit { get; set; }

        [JsonPropertyName("canDeactivate")]
        public bool CanDeactivate { get; set; }
    }

    public class AdminUsuarioEstabelecimentoRoleDto
    {
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("nome")]
        public string Nome { get; set; } = string.Empty;

        [JsonPropertyName("tipoEstabelecimento")]
        public string TipoEstabelecimento { get; set; } = string.Empty;

        [JsonPropertyName("tipoAcesso")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? TipoAcesso { get; set; }
    }

    public class CreateAdminUsuarioRequest
    {
        [JsonPropertyName("nome")]
        public string Nome { get; set; } = string.Empty;

        [JsonPropertyName("email")]
        public string Email { get; set; } = string.Empty;

        [JsonPropertyName("senhaInicial")]
        public string SenhaInicial { get; set; } = string.Empty;

        [JsonPropertyName("tipoUsuario")]
        public string TipoUsuario { get; set; } = string.Empty;

        [JsonPropertyName("empresaId")]
        public Guid? EmpresaId { get; set; }

        [JsonPropertyName("estabelecimentoIds")]
        public List<Guid> EstabelecimentoIds { get; set; } = new();
    }
}
