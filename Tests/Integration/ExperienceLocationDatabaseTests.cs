using APIBack.DTOs.Experience;
using APIBack.Repository;
using Npgsql;
using Xunit;

namespace APIBack.Tests.Integration;

public partial class DeliverySyncDatabaseTests
{
    [DeliveryDatabaseFact]
    public async Task ExperienceLocationPersistsGloballyAndMigrationIsIdempotent()
    {
        await using var db = await Database.Create();
        var sql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", "20261013_03_zippy_experience_location.sql"));
        await db.Execute(sql);
        await db.Execute(sql);
        var repository = new ExperienceRepository(db.Source);
        Assert.Null(await repository.GetLocationAsync(default));
        await repository.SaveLocationAsync(new ExperienceLocationDto { Latitude = -18.9186, Longitude = -48.2772, Cidade = "Uberlândia", Uf = "MG" }, 7, default);
        var freshRepository = new ExperienceRepository(db.Source);
        var saved = await freshRepository.GetLocationAsync(default);
        Assert.Equal("Uberlândia", saved!.Cidade);
        await freshRepository.SaveLocationAsync(new ExperienceLocationDto { Latitude = -23.5505, Longitude = -46.6333, Cidade = "São Paulo", Uf = "SP" }, 8, default);
        Assert.Equal("São Paulo", (await repository.GetLocationAsync(default))!.Cidade);
        await Assert.ThrowsAsync<PostgresException>(() => db.Execute("INSERT INTO zippy_experience_location (id, latitude, longitude, updated_by_user_id) VALUES (2, 0, 0, 1)"));
        await Assert.ThrowsAsync<PostgresException>(() => db.Execute("UPDATE zippy_experience_location SET latitude = 'NaN'::double precision"));
    }
}
