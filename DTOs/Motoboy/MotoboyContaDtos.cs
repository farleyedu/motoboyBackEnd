using System.ComponentModel.DataAnnotations;

namespace APIBack.DTOs.Motoboy;

public sealed class MotoboyContaDto
{
    public int MotoboyId { get; set; }
    public string Nome { get; set; } = "";
    public string Email { get; set; } = "";
    public string? Telefone { get; set; }
    public string? Cidade { get; set; }
    public string? Uf { get; set; }
    public string? ModeloMoto { get; set; }
    public string? PlacaMoto { get; set; }
    public int? AnoMoto { get; set; }
    public string StatusCadastro { get; set; } = "ativo";
    public string? Avatar { get; set; }
}

// Campos próprios explícitos: não aceita status, vínculo, saldo ou identidade no corpo.
public sealed class MotoboyDadosRequest
{
    [Required, StringLength(120, MinimumLength = 2)] public string Nome { get; set; } = "";
    [Required, EmailAddress, StringLength(200)] public string Email { get; set; } = "";
    [StringLength(30)] public string? Telefone { get; set; }
    [StringLength(120)] public string? Cidade { get; set; }
    [RegularExpression("^$|^[a-zA-Z]{2}$")] public string? Uf { get; set; }
}

public sealed class MotoboyVeiculoRequest
{
    [Required, StringLength(120, MinimumLength = 2)] public string ModeloMoto { get; set; } = "";
    [Required, RegularExpression("^[A-Za-z]{3}[0-9][A-Za-z0-9][0-9]{2}$")] public string PlacaMoto { get; set; } = "";
    [Range(1900, 2100)] public int AnoMoto { get; set; }
}

public sealed class MotoboyImagemRequest
{
    [Required, StringLength(5_600_000)] public string Base64 { get; set; } = "";
    [Required, RegularExpression("^(identificacao|cnh|moto|avatar)$")] public string Tipo { get; set; } = "";
}

public sealed class MotoboyDocumentoDto
{
    public Guid Id { get; set; }
    public string Tipo { get; set; } = "";
    public DateTimeOffset EnviadoEmUtc { get; set; }
    // Recebido significa armazenamento confirmado; não equivale a documento aprovado.
    public string Status { get; set; } = "recebido";
}
