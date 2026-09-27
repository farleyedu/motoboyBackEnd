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
}
