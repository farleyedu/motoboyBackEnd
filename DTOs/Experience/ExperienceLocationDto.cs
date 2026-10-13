using System.ComponentModel.DataAnnotations;

namespace APIBack.DTOs.Experience;

public sealed class ExperienceLocationDto
{
    [Required, Range(-85d, 85d)]
    public double? Latitude { get; set; }

    [Required, Range(-180d, 180d)]
    public double? Longitude { get; set; }

    [StringLength(120)]
    public string? Cidade { get; set; }

    [RegularExpression("^[a-zA-Z]{2}$")]
    public string? Uf { get; set; }
}
