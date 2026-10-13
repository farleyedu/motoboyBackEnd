using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;
using APIBack.DTOs.Experience;
using APIBack.Repository.Interface;

namespace APIBack.Service;

public sealed class ExperienceService(IExperienceRepository repository)
{
    public Task<ExperienceLocationDto?> GetLocationAsync(CancellationToken cancellationToken) =>
        repository.GetLocationAsync(cancellationToken);

    public Task<ExperienceLocationDto> SaveLocationAsync(ExperienceLocationDto location, int userId,
        bool isSuperAdmin, string establishmentName, IEnumerable<string> modules, bool canViewManagement,
        CancellationToken cancellationToken)
    {
        if (userId <= 0 || !CanConfigure(isSuperAdmin, establishmentName, modules, canViewManagement))
            throw new UnauthorizedAccessException("Somente quem tem acesso à Gestão pode alterar a localização do Zippy Experience.");

        if (!location.Latitude.HasValue || !double.IsFinite(location.Latitude.Value) ||
            !location.Longitude.HasValue || !double.IsFinite(location.Longitude.Value))
            throw new ValidationException("Informe uma localização válida no mapa.");
        Validator.ValidateObject(location, new ValidationContext(location), validateAllProperties: true);

        location.Cidade = string.IsNullOrWhiteSpace(location.Cidade) ? null : location.Cidade.Trim();
        location.Uf = string.IsNullOrWhiteSpace(location.Uf) ? null : location.Uf.Trim().ToUpperInvariant();
        return repository.SaveLocationAsync(location, userId, cancellationToken);
    }

    public static bool CanConfigure(bool isSuperAdmin, string establishmentName, IEnumerable<string> modules, bool canViewManagement)
    {
        if (isSuperAdmin) return true;
        if (!canViewManagement) return false;
        var name = Normalize(establishmentName);
        var active = modules.Select(Normalize).ToHashSet();
        return name.Contains("zippygo centro") || name.Contains("zippy go centro") ||
            (active.Contains("empresas") && active.Contains("estabelecimentos") && active.Contains("usuarios"));
    }

    private static string Normalize(string value) => string.Concat(value.Trim().ToLowerInvariant()
        .Normalize(NormalizationForm.FormD)
        .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark))
        .Normalize(NormalizationForm.FormC);
}
