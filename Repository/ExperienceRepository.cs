using APIBack.DTOs.Experience;
using APIBack.Repository.Interface;
using Dapper;
using Npgsql;

namespace APIBack.Repository;

public sealed class ExperienceRepository(NpgsqlDataSource dataSource) : IExperienceRepository
{
    public async Task<ExperienceLocationDto?> GetLocationAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<ExperienceLocationDto>(new CommandDefinition(
            "SELECT latitude, longitude, cidade, uf FROM zippy_experience_location WHERE id = 1;",
            cancellationToken: cancellationToken));
    }

    public async Task<ExperienceLocationDto> SaveLocationAsync(ExperienceLocationDto location, int userId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleAsync<ExperienceLocationDto>(new CommandDefinition("""
            INSERT INTO zippy_experience_location (id, latitude, longitude, cidade, uf, updated_by_user_id)
            VALUES (1, @Latitude, @Longitude, @Cidade, @Uf, @UserId)
            ON CONFLICT (id) DO UPDATE SET
                latitude = EXCLUDED.latitude, longitude = EXCLUDED.longitude,
                cidade = EXCLUDED.cidade, uf = EXCLUDED.uf,
                updated_by_user_id = EXCLUDED.updated_by_user_id, updated_at_utc = NOW()
            RETURNING latitude, longitude, cidade, uf;
            """, new { location.Latitude, location.Longitude, location.Cidade, location.Uf, UserId = userId },
            cancellationToken: cancellationToken));
    }
}
