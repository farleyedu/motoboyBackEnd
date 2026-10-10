using APIBack.DTOs.Delivery;
using APIBack.Repository;
using APIBack.Service;
using Xunit;

namespace APIBack.Tests.Integration;

public partial class DeliverySyncDatabaseTests
{
    [DeliveryDatabaseFact]
    public async Task AttentionPersistsWithStoreIsolationAndOnlyMarksSelectedOrderItems()
    {
        await using var db = await Database.Create();
        await db.Execute("""
CREATE TABLE cardapio_produto(id uuid PRIMARY KEY,id_estabelecimento uuid,deleted_at timestamptz,updated_at timestamptz);
CREATE TABLE cardapio_grupo_adicional(id uuid PRIMARY KEY,id_estabelecimento uuid,deleted_at timestamptz,updated_at timestamptz);
CREATE TABLE cardapio_grupo_adicional_item(id uuid PRIMARY KEY,id_grupo uuid);
""");
        await db.Execute(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,"Migrations","Delivery","20261010_02_cardapio_atencao_motoboy.sql")));
        var product = Guid.NewGuid(); var extra = Guid.NewGuid(); var option = Guid.NewGuid(); var other = Guid.NewGuid(); var foreignProduct = Guid.NewGuid();
        await db.Execute("INSERT INTO cardapio_produto(id,id_estabelecimento)VALUES(@Product,@StoreId),(@ForeignProduct,@Other); INSERT INTO cardapio_grupo_adicional(id,id_estabelecimento)VALUES(@Extra,@StoreId); INSERT INTO cardapio_grupo_adicional_item VALUES(@Option,@Extra)",new { Product=product, db.StoreId, Extra=extra, Option=option, Other=other, ForeignProduct=foreignProduct });
        var repository = new CardapioMotoboyAttentionRepository(db.Source);
        Assert.False(await repository.SetAsync(db.StoreId,"produtos",foreignProduct,true,default));
        Assert.True(await repository.SetAsync(other,"produtos",foreignProduct,true,default));
        Assert.True(await repository.SetAsync(db.StoreId,"produtos",product,true,default));
        Assert.True(await repository.SetAsync(db.StoreId,"adicionais",extra,true,default));
        var stored = await repository.ReadAsync(db.StoreId,default);
        Assert.Equal(new[]{product},stored.Produtos); Assert.Equal(new[]{extra},stored.Adicionais);
        var items = new List<PedidoItemDto> { new() { ProdutoId=product,Nome="Item sem palavra especial",Quantidade=2,Adicionais=new(){new(){Id=extra,Nome="Volume",Quantidade=2},new(){Id=option,Nome="Opção"},new(){Nome="Bacon",AtencaoMotoboy=true}} }, new() { ProdutoId=foreignProduct,Nome="Outra loja",AtencaoMotoboy=true } };
        await using var connection = await db.Source.OpenConnectionAsync();
        await CardapioMotoboyAttentionRepository.ApplyAsync(connection,null,db.StoreId,items);
        var manifest = DeliveryChecklistRules.Build(items);
        Assert.True(manifest.Items[0].Extra); Assert.True(manifest.Items[1].Extra); Assert.Equal(4,manifest.Items[1].Quantity);
        Assert.True(manifest.Items[2].Extra); Assert.False(manifest.Items[3].Extra); Assert.False(manifest.Items[4].Extra);
        Assert.True(await repository.SetAsync(db.StoreId,"produtos",product,false,default));
        Assert.Empty((await repository.ReadAsync(db.StoreId,default)).Produtos);
        await CardapioMotoboyAttentionRepository.ApplyAsync(connection,null,db.StoreId,items);
        Assert.False(items[0].AtencaoMotoboy);
    }
}
