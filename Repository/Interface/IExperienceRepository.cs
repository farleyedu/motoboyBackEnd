using APIBack.DTOs.Experience;

namespace APIBack.Repository.Interface;

public interface IExperienceRepository
{
    Task<ExperienceLocationDto?> GetLocationAsync(CancellationToken cancellationToken);
    Task<ExperienceLocationDto> SaveLocationAsync(ExperienceLocationDto location, int userId, CancellationToken cancellationToken);
}
