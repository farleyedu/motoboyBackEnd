using APIBack.Repository;
using APIBack.Service;
using Xunit;

namespace APIBack.Tests.Integration;

public partial class DeliverySyncDatabaseTests
{
    [DeliveryDatabaseFact]
    public async Task ContactsPageOnlineFirstThenNameAndIdWithoutDuplicatesAndLegacyCapped()
    {
        await using var db = await Database.Create();
        await db.Execute("""
INSERT INTO motoboy(id,nome,status) SELECT i, CASE WHEN i<30 THEN 'Mesmo nome' ELSE 'Último' END,2 FROM generate_series(2,250) i;
INSERT INTO motoboy_estabelecimento(motoboy_id,estabelecimento_id,ativo) SELECT i,@StoreId,TRUE FROM generate_series(2,250) i;
""", new { db.StoreId });
        var repository = new AtendimentoRepository(db.Source);
        var page = await repository.ListMotoboysPageAsync(db.StoreId, null);
        Assert.Equal(15, page.Contacts.Count); Assert.True(page.HasMore);
        Assert.True(page.Contacts[0].Online); Assert.Equal(1, page.Contacts[0].MotoboyId);
        var ids = page.Contacts.Select(c => c.MotoboyId).ToList();
        while (page.HasMore)
        {
            page = await repository.ListMotoboysPageAsync(db.StoreId, page.Cursor);
            Assert.InRange(page.Contacts.Count, 1, 10);
            ids.AddRange(page.Contacts.Select(c => c.MotoboyId));
        }
        Assert.Equal(250, ids.Count); Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Equal(200, (await repository.ListMotoboysVinculadosAsync(db.StoreId)).Count);
        Assert.Single((await repository.ListMotoboysPageAsync(db.StoreId, null, "Teste")).Contacts);
        Assert.Empty((await repository.ListMotoboysPageAsync(Guid.NewGuid(), null)).Contacts);
        Assert.Equal("CONTACT_CURSOR_INVALID", (await Assert.ThrowsAsync<DeliveryDomainException>(() =>
            repository.ListMotoboysPageAsync(db.StoreId, "inválido"))).Code);
        var first = await repository.ListMotoboysPageAsync(db.StoreId, null);
        Assert.Equal("CONTACT_CURSOR_INVALID", (await Assert.ThrowsAsync<DeliveryDomainException>(() =>
            repository.ListMotoboysPageAsync(Guid.NewGuid(), first.Cursor))).Code);
    }
}
