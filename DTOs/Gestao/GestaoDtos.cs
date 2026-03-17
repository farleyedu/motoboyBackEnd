using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using APIBack.Infrastructure;

namespace APIBack.DTOs.Gestao
{
    public class GestaoPlanoDto
    {
        public string? Nome { get; set; }
        public decimal? ValorMensal { get; set; }
        public string? Status { get; set; }
        public List<string> Beneficios { get; set; } = new();
        public string? ProximaCobranca { get; set; }
    }

    public class GestaoEmpresaDto
    {
        public Guid Id { get; set; }
        public int? IdInt { get; set; }
        public string NomeFantasia { get; set; } = string.Empty;
        public string? RazaoSocial { get; set; }
        public string? Cnpj { get; set; }
        public string? Email { get; set; }
        public string? Telefone { get; set; }
        public string? Site { get; set; }
        public string? ImagemCapa { get; set; }
        public string? CidadeBase { get; set; }
        public string TipoOrganizacao { get; set; } = "empresa";
        public bool Ativa { get; set; } = true;
        public DateTime? CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public GestaoPlanoDto? Plano { get; set; }
    }

    public class GestaoEstabelecimentoDto
    {
        public Guid Id { get; set; }
        public Guid EmpresaId { get; set; }
        public string? TipoUnidade { get; set; }
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
        public bool AceitaPedidos { get; set; } = true;
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
        public List<string> ModulosAtivos { get; set; } = new();
        public bool Ativo { get; set; } = true;
        public string Status { get; set; } = "ativo";
        public string? Endereco { get; set; }
    }

    public class GestaoUsuarioEmpresaDto
    {
        public Guid Id { get; set; }
        public string Nome { get; set; } = string.Empty;
    }

    public class GestaoUsuarioVinculoDto
    {
        public Guid VinculoId { get; set; }
        public Guid? EmpresaId { get; set; }
        public Guid? EstabelecimentoId { get; set; }
        public string? EstabelecimentoNome { get; set; }
        public string? TipoEstabelecimento { get; set; }
        public string? TipoAcesso { get; set; }
        public string Status { get; set; } = "ativo";
        public string OrigemEscopo { get; set; } = "estabelecimento";
        public Dictionary<string, List<string>>? Permissoes { get; set; }
    }

    public class GestaoUsuarioDto
    {
        public int Id { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string? Telefone { get; set; }
        public string Status { get; set; } = "ativo";
        public bool IsSuperAdmin { get; set; }
        public DateTime? CreatedAt { get; set; }
        public string? UltimoAcesso { get; set; }
        public string? Veiculo { get; set; }
        public decimal? RaioMaxKm { get; set; }
        public decimal? TaxaBase { get; set; }
        public GestaoUsuarioEmpresaDto? Empresa { get; set; }
        public List<GestaoUsuarioVinculoDto> Vinculos { get; set; } = new();
        public Dictionary<string, List<string>>? Permissoes { get; set; }
    }

    public class SalvarEmpresaRequest
    {
        public string NomeFantasia { get; set; } = string.Empty;
        public string? RazaoSocial { get; set; }
        public string? Cnpj { get; set; }
        public string? Email { get; set; }
        public string? Telefone { get; set; }
        public string? Site { get; set; }
        public string? ImagemCapa { get; set; }
        public string? CidadeBase { get; set; }
        public string? TipoOrganizacao { get; set; }
        public GestaoPlanoDto? Plano { get; set; }
        public SalvarEstabelecimentoRequest? InitialEstablishment { get; set; }
    }

    public class SalvarEstabelecimentoRequest
    {
        [JsonConverter(typeof(LenientGuidConverter))]
        public Guid? EmpresaId { get; set; }
        public string? TipoUnidade { get; set; }
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
        public bool? AceitaPedidos { get; set; }
        public string? TimezoneIana { get; set; }
        public decimal? RaioEntregaKm { get; set; }
        public decimal? PedidoMinimo { get; set; }
        public decimal? TaxaEntregaFixa { get; set; }
        public decimal? TaxaEntregaPorKm { get; set; }
        public int? TempoPreparoMin { get; set; }
        [JsonConverter(typeof(LenientGuidConverter))]
        public Guid? TipoEstabelecimentoId { get; set; }
        public string? TipoEstabelecimentoSlug { get; set; }
        public string? TipoEstabelecimentoNome { get; set; }
        public string? Slug { get; set; }
        public List<string> ModulosAtivos { get; set; } = new();
    }

    public class SalvarUsuarioRequest
    {
        public string Nome { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string? Telefone { get; set; }
        public string? SenhaInicial { get; set; }
        public string TipoUsuario { get; set; } = string.Empty;
        public Guid? EmpresaId { get; set; }
        public List<Guid> EstabelecimentoIds { get; set; } = new();
        public Dictionary<string, List<string>> Permissoes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public string? Veiculo { get; set; }
        public decimal? RaioMaxKm { get; set; }
        public decimal? TaxaBase { get; set; }
    }

    public class AtualizarStatusEmpresaRequest
    {
        public bool Ativa { get; set; }
    }

    public class AtualizarStatusEstabelecimentoRequest
    {
        public bool Ativa { get; set; }
    }

    public class AtualizarStatusUsuarioRequest
    {
        public string Status { get; set; } = string.Empty;
    }
}
