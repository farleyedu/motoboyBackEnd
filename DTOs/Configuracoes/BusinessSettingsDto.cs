using System.ComponentModel.DataAnnotations;

namespace APIBack.DTOs.Configuracoes;

public sealed class BusinessSettingsDto
{
    [Required, StringLength(200)] public string NomeFantasia { get; set; } = string.Empty;
    public string? CnpjLoja { get; set; }
    [StringLength(30)] public string? Telefone { get; set; }
    [EmailAddress, StringLength(200)] public string? Email { get; set; }
    [StringLength(300)] public string? Logradouro { get; set; }
    [StringLength(30)] public string? Numero { get; set; }
    [StringLength(100)] public string? Complemento { get; set; }
    [StringLength(100)] public string? Bairro { get; set; }
    [StringLength(100)] public string? Cidade { get; set; }
    [RegularExpression("^[A-Za-z]{2}$")] public string? Uf { get; set; }
    [RegularExpression("^[0-9]{5}-?[0-9]{3}$")] public string? Cep { get; set; }
    [Url, StringLength(2000)] public string? UrlLogo { get; set; }
    [Url, StringLength(2000)] public string? SiteUrl { get; set; }
    [Url, StringLength(2000)] public string? FaviconUrl { get; set; }
    [RegularExpression("^#[0-9A-Fa-f]{6}$")] public string? CorPrimaria { get; set; }
    [RegularExpression("^#[0-9A-Fa-f]{6}$")] public string? CorSecundaria { get; set; }
    [StringLength(60)] public string? Tipografia { get; set; }
    [Url, StringLength(2000)] public string? InstagramUrl { get; set; }
    [Url, StringLength(2000)] public string? FacebookUrl { get; set; }
}

/// <summary>Um dia da semana no horario de funcionamento (0 = segunda ... 6 = domingo, mesmo indice da tela de Disponibilidade).</summary>
public sealed class HorarioDiaDto
{
    [Range(0, 6)] public int DiaSemana { get; set; }
    public bool Fechado { get; set; }
    /// <summary>"HH:mm"; null quando fechado.</summary>
    public string? AbreAs { get; set; }
    public string? FechaAs { get; set; }
}

public sealed class SalvarHorariosRequest
{
    [Required, MinLength(7), MaxLength(7)] public List<HorarioDiaDto> Dias { get; set; } = new();
}

/// <summary>Outro estabelecimento da mesma empresa (para o widget "Unidades e filiais").</summary>
public sealed class UnidadeResumoDto
{
    public Guid Id { get; set; }
    public string NomeFantasia { get; set; } = string.Empty;
    public string? Cidade { get; set; }
    public string? Uf { get; set; }
    public bool EhAtual { get; set; }
}

/// <summary>Completude do cadastro: cada item e um sinal real (nunca inventado) do que falta preencher.</summary>
public sealed class CompletudePerfilDto
{
    public int Percentual { get; set; }
    public List<CompletudeItemDto> Itens { get; set; } = new();
}

public sealed class CompletudeItemDto
{
    public string Chave { get; set; } = string.Empty;
    public string Titulo { get; set; } = string.Empty;
    public bool Completo { get; set; }
}
