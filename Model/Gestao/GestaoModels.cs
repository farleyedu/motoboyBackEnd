using System;
using System.Collections.Generic;

namespace APIBack.Model.Gestao
{
    public class GestaoEmpresaRow
    {
        public Guid Id { get; set; }
        public int? IdInt { get; set; }
        public string NomeFantasia { get; set; } = string.Empty;
        public string? RazaoSocial { get; set; }
        public string? Cnpj { get; set; }
        public string? Email { get; set; }
        public string? Telefone { get; set; }
        public string? Site { get; set; }
        public string? CidadeBase { get; set; }
        public string TipoOrganizacao { get; set; } = "empresa";
        public bool Ativa { get; set; }
        public DateTime? CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
    }

    public class GestaoEstabelecimentoRow
    {
        public Guid Id { get; set; }
        public Guid EmpresaId { get; set; }
        public string TipoUnidade { get; set; } = "matriz";
        public string NomeFantasia { get; set; } = string.Empty;
        public string? CnpjLoja { get; set; }
        public string? InscricaoEstadual { get; set; }
        public string? InscricaoMunicipal { get; set; }
        public string? Telefone { get; set; }
        public string? WhatsappE164 { get; set; }
        public string? Email { get; set; }
        public string? Logradouro { get; set; }
        public string? Numero { get; set; }
        public string? Complemento { get; set; }
        public string? Bairro { get; set; }
        public string? Cidade { get; set; }
        public string? Uf { get; set; }
        public string? Cep { get; set; }
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        public string? UrlLogo { get; set; }
        public bool AceitaPedidos { get; set; }
        public string TimezoneIana { get; set; } = "America/Sao_Paulo";
        public decimal? RaioEntregaKm { get; set; }
        public decimal? PedidoMinimo { get; set; }
        public decimal? TaxaEntregaFixa { get; set; }
        public decimal? TaxaEntregaPorKm { get; set; }
        public int? TempoPreparoMin { get; set; }
        public DateTime? CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public Guid? TipoEstabelecimentoId { get; set; }
        public string? TipoEstabelecimentoSlug { get; set; }
        public string? TipoEstabelecimentoNome { get; set; }
        public string? Slug { get; set; }
        public string[]? ModulosAtivosRaw { get; set; }
        public bool Ativo { get; set; }
        public string Status { get; set; } = "ativo";
    }

    public class GestaoUsuarioResumoRow
    {
        public int Id { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public bool IsSuperAdmin { get; set; }
        public DateTime? CreatedAt { get; set; }
        public DateTime? DeletedAt { get; set; }
    }

    public class GestaoUsuarioEmpresaRow
    {
        public Guid VinculoId { get; set; }
        public int UserId { get; set; }
        public Guid EmpresaId { get; set; }
        public string EmpresaNome { get; set; } = string.Empty;
        public string TipoAcesso { get; set; } = string.Empty;
        public string Status { get; set; } = "ativo";
        public bool? Ativo { get; set; }
    }

    public class GestaoUsuarioEstabelecimentoRow
    {
        public Guid VinculoId { get; set; }
        public int UserId { get; set; }
        public Guid EmpresaId { get; set; }
        public Guid EstabelecimentoId { get; set; }
        public string EstabelecimentoNome { get; set; } = string.Empty;
        public string TipoEstabelecimentoNome { get; set; } = string.Empty;
        public string TipoEstabelecimentoSlug { get; set; } = string.Empty;
        public string TipoAcesso { get; set; } = string.Empty;
        public string Status { get; set; } = "ativo";
        public bool? Ativo { get; set; }
        public string? PermissoesCustomizadas { get; set; }
    }

    public class GestaoTargetEstabelecimento
    {
        public Guid Id { get; set; }
        public Guid EmpresaId { get; set; }
        public string EmpresaNome { get; set; } = string.Empty;
        public string Nome { get; set; } = string.Empty;
        public string TipoEstabelecimentoNome { get; set; } = string.Empty;
        public string TipoEstabelecimentoSlug { get; set; } = string.Empty;
        public bool Ativo { get; set; }
        public string Status { get; set; } = "ativo";
    }

    public class GestaoEmpresaScope
    {
        public bool IsSuperAdmin { get; set; }
        public Guid? EmpresaId { get; set; }
        public Guid? EstabelecimentoId { get; set; }
        public string CompanyRole { get; set; } = string.Empty;
        public string EstablishmentRole { get; set; } = string.Empty;
    }

    public class GestaoPersistenciaUsuarioCommand
    {
        public int? UsuarioId { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string? SenhaHash { get; set; }
        public bool IsSuperAdmin { get; set; }
        public Guid EmpresaId { get; set; }
        public string CompanyRole { get; set; } = "colaborador";
        public string? EstablishmentRole { get; set; }
        public IReadOnlyCollection<Guid> EstabelecimentoIds { get; set; } = Array.Empty<Guid>();
        public string? PermissoesCustomizadasJson { get; set; }
        public int ActorUserId { get; set; }
    }
}
