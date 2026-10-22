using System.Text.Json.Nodes;
using APIBack.Attributes;
using APIBack.Controllers;
using APIBack.DTOs.Cardapio;
using APIBack.DTOs.Common;
using APIBack.DTOs.Delivery;
using APIBack.Integrations.Ifood.Orders;
using APIBack.Model.Cardapio;
using APIBack.Repository;
using APIBack.Service;
using APIBack.Tests.Unit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace APIBack.Tests.Integration;

public sealed partial class IfoodIntegrationSchemaDatabaseTests
{
    private const string LinkFile = "20261024_01_ifood_produto_vinculo.sql";
    private const string LinkVerifyFile = "20261024_02_ifood_produto_vinculo_verify.sql";

    // Cardápio real (não o stub de outros testes): o vínculo depende de nome, exclusão lógica, categoria e aviso de bag.
    private static async Task<Database> LinkDatabase()
    {
        var db = await ImportDatabase();
        try
        {
            // A fila de eventos (ClaimAsync) lê a situação comercial e os módulos da loja; o estabelecimentos de teste só tem o id.
            await db.Execute("""
CREATE TYPE modulo_enum AS ENUM ('IFOOD');
ALTER TABLE estabelecimentos ADD COLUMN situacao_comercial text NOT NULL DEFAULT 'teste', ADD COLUMN modulos_ativos modulo_enum[];
DROP TABLE cardapio_produto;
""");
            await db.Migrate("20261001_01_cardapio_schema.sql");
            await db.Migrate("20261010_02_cardapio_atencao_motoboy.sql");
            await db.Migrate(LinkFile);
            return db;
        }
        catch { await db.DisposeAsync(); throw; }
    }

    private static async Task<Guid> Category(Database db, Guid store, string name = "Lanches")
    {
        var id = Guid.NewGuid();
        await db.Execute("INSERT INTO cardapio_categoria(id,id_estabelecimento,nome,slug)VALUES(@Id,@Store,@Name,@Slug)", new { Id = id, Store = store, Name = name, Slug = id.ToString("N") });
        return id;
    }

    private static async Task<Guid> Product(Database db, Guid store, string name, string? image = null, bool attention = false, string category = "Lanches")
    {
        var id = Guid.NewGuid(); var categoryId = await Category(db, store, category);
        await db.Execute("INSERT INTO cardapio_produto(id,id_estabelecimento,categoria_id,nome,slug,preco_base,imagem_url,atencao_motoboy)VALUES(@Id,@Store,@Category,@Name,@Slug,10,@Image,@Attention)",
            new { Id = id, Store = store, Category = categoryId, Name = name, Slug = id.ToString("N"), Image = image, Attention = attention });
        return id;
    }

    private static string OrderPayload(Database db, Guid order, params (string Id, string Name, string? Code, string? Image)[] items)
    {
        var root = JsonNode.Parse(IfoodOrderImportTests.Payload(db.MerchantA, order))!.AsObject();
        var array = new JsonArray();
        foreach (var (id, name, code, image) in items)
        {
            var item = new JsonObject { ["id"] = id, ["name"] = name, ["quantity"] = 2, ["unit"] = "UN", ["unitPrice"] = 10, ["price"] = 20, ["optionsPrice"] = 0, ["totalPrice"] = 20, ["options"] = new JsonArray() };
            if (code != null) item["externalCode"] = code;
            if (image != null) item["imageUrl"] = image;
            array.Add(item);
        }
        root["items"] = array;
        return root.ToJsonString();
    }

    private static async Task<int> ImportOrder(Database db, IfoodOrderImportRepository repo, Guid order, string payload) =>
        await repo.ImportAsync(await ImportEvent(db, order), IfoodOrderNormalizer.Normalize(payload, db.MerchantA, order));

    private static IfoodOrderImportRepository LinkingRepository(Database db) => new(db.Source, new IfoodItemLinkService(db.Source));

    private sealed record LinkState(string Estado, string? Origem, Guid? Produto, int Pedidos, string? Code);
    private static async Task<LinkState> State(Database db, string key = "item-1") =>
        (await db.Query<LinkState>("SELECT estado AS Estado,origem AS Origem,produto_id AS Produto,pedidos_count AS Pedidos,external_code AS Code FROM ifood_produto_vinculo WHERE chave_id=@Key", new { Key = key })).Single();

    private static ApiResponse<T> Body<T>(IActionResult result, int status = 200)
    {
        var obj = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(status, obj.StatusCode ?? 200);
        return Assert.IsType<ApiResponse<T>>(obj.Value);
    }

    private static CardapioIfoodVinculoController Controller(Database db, Guid store, int user = 7)
    {
        var context = new DefaultHttpContext();
        context.Items["EstabelecimentoId"] = store; context.Items["UserId"] = user;
        // Cardápio e fotos só entram no "criar produto", coberto direto no repositório (PrepararCriacaoAsync).
        return new CardapioIfoodVinculoController(new IfoodProdutoVinculoRepository(db.Source), null!, null!,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CardapioIfoodVinculoController>.Instance) { ControllerContext = new ControllerContext { HttpContext = context } };
    }

    [DeliveryDatabaseFact]
    public async Task CreateFromItemRequiresAFreeItemAndAnUnusedNameAndReturnsTheIfoodPhoto()
    {
        await using var db = await LinkDatabase(); var repo = LinkingRepository(db);
        var payload = OrderPayload(db, db.OrderA, ("item-1", "Pizza Calabresa G", "7993", "https://ifood.example/p.jpg"));
        await ImportOrder(db, repo, db.OrderA, payload);
        var link = await db.Scalar<Guid>("SELECT id FROM ifood_produto_vinculo WHERE chave_id='item-1'");
        var links = new IfoodProdutoVinculoRepository(db.Source);

        Assert.Equal("https://ifood.example/p.jpg", await links.PrepararCriacaoAsync(db.StoreA, link, "Calabresa Grande", default));

        var same = await Product(db, db.StoreA, "Calabresa  GRANDE");
        var byName = await Assert.ThrowsAsync<DeliveryDomainException>(() => links.PrepararCriacaoAsync(db.StoreA, link, "calabresa grande", default));
        Assert.Equal((409, "PRODUTO_NOME_JA_EXISTE"), (byName.StatusCode, byName.Code));
        Assert.Equal(same, (Guid)byName.Details!.GetType().GetProperty("produtoId")!.GetValue(byName.Details)!);

        await links.VincularAsync(db.StoreA, 7, same, link, default);
        var used = await Assert.ThrowsAsync<DeliveryDomainException>(() => links.PrepararCriacaoAsync(db.StoreA, link, "Outro nome", default));
        Assert.Equal((409, "IFOOD_VINCULO_JA_USADO"), (used.StatusCode, used.Code));

        var foreign = await Assert.ThrowsAsync<DeliveryDomainException>(() => links.PrepararCriacaoAsync(db.StoreB, link, "Outro nome", default));
        Assert.Equal(404, foreign.StatusCode);
    }

    [DeliveryDatabaseFact]
    public async Task LinkMigrationIsRepeatableConstraintsHoldAndRollbackIsClean()
    {
        await using var db = await LinkDatabase();
        await db.Migrate(LinkFile); await db.Migrate(LinkVerifyFile);
        var own = await Product(db, db.StoreA, "Do A"); var foreign = await Product(db, db.StoreB, "Do B");
        const string insert = "INSERT INTO ifood_produto_vinculo(estabelecimento_id,merchant_id,chave_id,nome_ifood,estado,produto_id,origem)VALUES(@Store,@Merchant,@Key,'Item',@Estado,@Produto,@Origem)";
        object Args(string key, string estado, Guid? produto, string? origem, Guid? merchant = null, Guid? store = null) =>
            new { Store = store ?? db.StoreA, Merchant = merchant ?? db.MerchantA, Key = key, Estado = estado, Produto = produto, Origem = origem };
        await db.Execute(insert, Args("ok", "confirmado", own, "manual"));
        await Reject("23505", () => db.Execute(insert, Args("ok", "pendente", null, null)));              // chave única por loja/merchant
        await Reject("23514", () => db.Execute(insert, Args("sem-produto", "confirmado", null, "nome")));   // confirmado exige produto
        await Reject("23514", () => db.Execute(insert, Args("sem-origem", "confirmado", own, null)));       // confirmado exige origem
        await Reject("23514", () => db.Execute(insert, Args("pendente-com-produto", "pendente", own, null)));
        await Reject("23503", () => db.Execute(insert, Args("outra-loja", "confirmado", foreign, "manual"))); // produto de outra loja
        await Reject("23503", () => db.Execute(insert, Args("merchant-alheio", "pendente", null, null, merchant: db.MerchantB))); // merchant de outra loja
        await Reject("23514", () => db.Execute("INSERT INTO ifood_produto_vinculo(estabelecimento_id,merchant_id,tipo,chave_id,nome_ifood)VALUES(@S,@M,'outro','k','n')", new { S = db.StoreA, M = db.MerchantA }));
        await db.Execute(insert, Args("ok", "pendente", null, null, merchant: db.MerchantB, store: db.StoreB)); // mesma chave em outra loja é outra linha
        Assert.Equal(2, await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_produto_vinculo"));
        Assert.True(await db.Scalar<bool>("SELECT EXISTS(SELECT 1 FROM delivery_tracking_schema_versions WHERE version='20261024_01_ifood_produto_vinculo')"));

        await db.Execute(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", "rollback", "20261024_01_ifood_produto_vinculo.down.sql")));
        Assert.False(await db.Scalar<bool>("SELECT to_regclass('ifood_produto_vinculo') IS NOT NULL"));
        Assert.False(await db.Scalar<bool>("SELECT EXISTS(SELECT 1 FROM delivery_tracking_schema_versions WHERE version='20261024_01_ifood_produto_vinculo')"));
        Assert.Equal(1, await db.Scalar<int>("SELECT COUNT(*)::int FROM cardapio_produto WHERE id=@Id", new { Id = own })); // cardápio intacto
        await db.Migrate(LinkFile); await db.Migrate(LinkVerifyFile);
    }

    [DeliveryDatabaseFact]
    public async Task ImportRegistersKeysIdempotentlyAndConfirmsByNameOnlyWhenTheNameIsUnique()
    {
        await using var db = await LinkDatabase(); var repo = LinkingRepository(db);
        var product = await Product(db, db.StoreA, "X-Burger");
        var payload = OrderPayload(db, db.OrderA, ("item-1", "  x-BURGER ", "COD-1", null), ("item-2", "Sem produto", null, null));
        var pedido = await ImportOrder(db, repo, db.OrderA, payload);
        var linked = await State(db);
        Assert.Equal(new LinkState("confirmado", "nome", product, 1, "COD-1"), linked);
        Assert.Equal(new LinkState("pendente", null, null, 1, null), await State(db, "item-2"));

        // Mesmo pedido de novo (outro evento): não duplica chave nem soma o pedido outra vez.
        Assert.Equal(pedido, await ImportOrder(db, repo, db.OrderA, payload));
        Assert.Equal(2, await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_produto_vinculo"));
        Assert.Equal(1, (await State(db)).Pedidos);

        // Outro pedido com o mesmo item: só sobe a contagem, o vínculo continua o mesmo.
        await ImportOrder(db, repo, db.OrderB, OrderPayload(db, db.OrderB, ("item-1", "X-Burger", "COD-OUTRO", null)));
        var again = await State(db);
        Assert.Equal(2, again.Pedidos); Assert.Equal(product, again.Produto); Assert.Equal("COD-1", again.Code); // metadado da 1ª vez, só pedidos/visto mudam
    }

    [DeliveryDatabaseFact]
    public async Task ItemIdLinkWinsOverNameAndDuplicateOrAccentedNamesStayPending()
    {
        await using var db = await LinkDatabase(); var repo = LinkingRepository(db);
        await Product(db, db.StoreA, "Suco"); await Product(db, db.StoreA, "SUCO ");           // duplicidade de nome
        await Product(db, db.StoreA, "Acai");                                                    // sem acento
        await Product(db, db.StoreA, "Pizza"); var other = await Product(db, db.StoreA, "Outra");
        await ImportOrder(db, repo, db.OrderA, OrderPayload(db, db.OrderA, ("item-1", "Suco", null, null), ("item-2", "Açaí", null, null), ("item-3", "Sem par", null, null)));
        Assert.Equal("pendente", (await State(db)).Estado); Assert.Null((await State(db)).Produto);
        Assert.Equal("pendente", (await State(db, "item-2")).Estado);
        Assert.Equal("pendente", (await State(db, "item-3")).Estado);

        // Vínculo já confirmado pelo id vale mesmo que o nome agora bata com outro produto.
        await db.Execute("UPDATE ifood_produto_vinculo SET estado='confirmado',origem='manual',produto_id=@P WHERE chave_id='item-3'", new { P = other });
        await ImportOrder(db, repo, db.OrderB, OrderPayload(db, db.OrderB, ("item-3", "Pizza", null, null)));
        var stay = await State(db, "item-3");
        Assert.Equal(("confirmado", "manual", (Guid?)other), (stay.Estado, stay.Origem, stay.Produto));
    }

    [DeliveryDatabaseFact]
    public async Task ManualLinkUndoAndRelinkNeverReturnToAutomaticMatching()
    {
        await using var db = await LinkDatabase(); var repo = LinkingRepository(db);
        await Product(db, db.StoreA, "Combo"); var chosen = await Product(db, db.StoreA, "Combo "); // dois: fica pendente
        await ImportOrder(db, repo, db.OrderA, OrderPayload(db, db.OrderA, ("item-1", "Combo", null, null)));
        var vinculo = await db.Scalar<Guid>("SELECT id FROM ifood_produto_vinculo WHERE chave_id='item-1'");
        var vinculos = new IfoodProdutoVinculoRepository(db.Source);

        var linked = await vinculos.VincularAsync(db.StoreA, 7, chosen, vinculo, default);
        Assert.Equal(("manual", 7), (linked.Origem, linked.ConfirmadoPor)); Assert.NotNull(linked.ConfirmadoEm);
        Assert.Equal(linked.VinculoId, (await vinculos.VincularAsync(db.StoreA, 8, chosen, vinculo, default)).VinculoId); // repetir é seguro
        Assert.Equal(7, await db.Scalar<int>("SELECT confirmado_por FROM ifood_produto_vinculo WHERE id=@Id", new { Id = vinculo })); // quem ligou primeiro

        Assert.Equal("desfeito", (await vinculos.DesfazerAsync(db.StoreA, 9, vinculo, default)).Estado);
        Assert.Equal("desfeito", (await vinculos.DesfazerAsync(db.StoreA, 9, vinculo, default)).Estado); // idempotente
        Assert.True(await db.Scalar<bool>("SELECT desfeito_por=9 AND desfeito_em IS NOT NULL AND produto_id IS NULL AND origem IS NULL FROM ifood_produto_vinculo WHERE id=@Id", new { Id = vinculo }));
        Assert.Equal(1, await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_produto_vinculo")); // não apaga a linha

        // Mesmo que agora só reste UM produto com o nome, desfeito por pessoa não religa sozinho.
        await db.Execute("UPDATE cardapio_produto SET deleted_at=NOW() WHERE id<>@Keep AND nome ILIKE 'Combo%'", new { Keep = chosen });
        await ImportOrder(db, repo, db.OrderB, OrderPayload(db, db.OrderB, ("item-1", "Combo", null, null)));
        await IfoodItemLinkService.ReevaluatePendingAsync(await db.Source.OpenConnectionAsync(), db.StoreA, "Combo");
        var state = await State(db);
        Assert.Equal(("desfeito", (Guid?)null, 2), (state.Estado, state.Produto, state.Pedidos));
        // Uma pessoa pode religar de propósito.
        Assert.Equal("manual", (await vinculos.VincularAsync(db.StoreA, 7, chosen, vinculo, default)).Origem);
    }

    [DeliveryDatabaseFact]
    public async Task CreatingOrRenamingAProductMatchesPendingItemsOnlyWhenTheNameIsUnique()
    {
        await using var db = await LinkDatabase(); var repo = LinkingRepository(db);
        await ImportOrder(db, repo, db.OrderA, OrderPayload(db, db.OrderA, ("item-1", "Torta de Limão", null, null), ("item-2", "Pudim", null, null)));
        Assert.Equal("pendente", (await State(db)).Estado);

        var cardapio = new CardapioRepository(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = db.Source.ConnectionString }).Build());
        var category = await Category(db, db.StoreA);
        // Criar com o nome (caixa e espaços diferentes) liga o pendente.
        var torta = new CardapioProduto { IdEstabelecimento = db.StoreA, CategoriaId = category, Nome = "  torta  de LIMÃO", PrecoBase = 12, Ativo = true, Disponivel = true, PublicoWeb = true };
        await cardapio.CriarProdutoAsync(torta);
        Assert.Equal(new LinkState("confirmado", "nome", torta.Id, 1, null), await State(db));

        // Renomear um produto existente para o nome do item também liga.
        var other = new CardapioProduto { IdEstabelecimento = db.StoreA, CategoriaId = category, Nome = "Doce", PrecoBase = 8, Ativo = true, Disponivel = true, PublicoWeb = true };
        await cardapio.CriarProdutoAsync(other);
        Assert.Equal("pendente", (await State(db, "item-2")).Estado);
        other.Nome = "Pudim";
        Assert.True(await cardapio.AtualizarProdutoAsync(other));
        Assert.Equal(other.Id, (await State(db, "item-2")).Produto);

        // Um segundo "Pudim" não rouba o vínculo e um terceiro com o mesmo nome não cria ambiguidade retroativa.
        var twin = new CardapioProduto { IdEstabelecimento = db.StoreA, CategoriaId = category, Nome = "Pudim", PrecoBase = 8, Ativo = true, Disponivel = true, PublicoWeb = true };
        await cardapio.CriarProdutoAsync(twin);
        Assert.Equal(other.Id, (await State(db, "item-2")).Produto);

        // Criar produto jamais falha por causa do vínculo: sem a tabela, o cardápio segue funcionando.
        await db.Execute("DROP TABLE ifood_produto_vinculo");
        var free = new CardapioProduto { IdEstabelecimento = db.StoreA, CategoriaId = category, Nome = "Livre", PrecoBase = 1, Ativo = true, Disponivel = true, PublicoWeb = true };
        Assert.NotEqual(Guid.Empty, await cardapio.CriarProdutoAsync(free));
    }

    [DeliveryDatabaseFact]
    public async Task ReevaluationLeavesAmbiguousNamesAndOtherStoresAlone()
    {
        await using var db = await LinkDatabase(); var repo = LinkingRepository(db);
        await ImportOrder(db, repo, db.OrderA, OrderPayload(db, db.OrderA, ("item-1", "Pizza", null, null)));
        await Product(db, db.StoreB, "Pizza");               // outra loja nunca casa
        await using var connection = await db.Source.OpenConnectionAsync();
        await IfoodItemLinkService.ReevaluatePendingAsync(connection, db.StoreA, "Pizza");
        Assert.Equal("pendente", (await State(db)).Estado);
        await Product(db, db.StoreA, "Pizza"); await Product(db, db.StoreA, "PIZZA");
        await IfoodItemLinkService.ReevaluatePendingAsync(connection, db.StoreA, "Pizza");
        Assert.Equal("pendente", (await State(db)).Estado);   // dois na mesma loja: ambíguo
    }

    [DeliveryDatabaseFact]
    public async Task ManualBackfillRegistersKeysFromExistingSnapshotsAndIsIdempotent()
    {
        await using var db = await LinkDatabase();
        var plain = new IfoodOrderImportRepository(db.Source); // pedidos de antes do vínculo: ninguém registrou as chaves
        var produto = await Product(db, db.StoreA, "X-Burger");
        await ImportOrder(db, plain, db.OrderA, OrderPayload(db, db.OrderA, ("item-1", "x-burger", "COD-1", "https://static.ifood/a.png"), ("item-2", "Suco", null, null)));
        await ImportOrder(db, plain, db.OrderB, OrderPayload(db, db.OrderB, ("item-1", "X-Burger", null, null)));
        Assert.Equal(0, await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_produto_vinculo"));
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "APIBack.csproj"))) root = Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar))!;
        var script = await File.ReadAllTextAsync(Path.Combine(root, "Migrations", "Delivery", "manual", "20261024_ifood_produto_vinculo_backfill.sql"));

        await db.Execute(script);
        Assert.Equal(2, await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_produto_vinculo"));
        Assert.Equal(new LinkState("confirmado", "nome", produto, 2, "COD-1"), await State(db));
        Assert.Equal(new LinkState("pendente", null, null, 1, null), await State(db, "item-2"));
        Assert.Equal("https://static.ifood/a.png", await db.Scalar<string>("SELECT imagem_url FROM ifood_produto_vinculo WHERE chave_id='item-1'"));

        // Rodar de novo não duplica, não soma pedidos e não religa o que uma pessoa desfez.
        await db.Execute("UPDATE ifood_produto_vinculo SET estado='desfeito',produto_id=NULL,origem=NULL,desfeito_em=NOW() WHERE chave_id='item-1'");
        await db.Execute(script);
        Assert.Equal(2, await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_produto_vinculo"));
        Assert.Equal(new LinkState("desfeito", null, null, 2, "COD-1"), await State(db));
    }

    [DeliveryDatabaseFact]
    public async Task LinkServiceFailureNeverBreaksTheImport()
    {
        await using var db = await LinkDatabase();
        var throwing = new IfoodOrderImportRepository(db.Source, new ThrowingLinkService(db.Source));
        var pedido = await ImportOrder(db, throwing, db.OrderA, OrderPayload(db, db.OrderA, ("item-1", "X", null, null)));
        Assert.Equal(1, await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_order_links WHERE pedido_id=@P", new { P = pedido }));
        Assert.Equal("processed", await db.Scalar<string>("SELECT state FROM ifood_events_inbox"));
        Assert.Equal(0, await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_produto_vinculo"));

        // Falha de verdade no banco (a linha violaria um CHECK): o serviço real engole e o pedido segue importado.
        await db.Execute("ALTER TABLE ifood_produto_vinculo ADD CONSTRAINT injected_link_failure CHECK(false) NOT VALID");
        var second = await ImportOrder(db, LinkingRepository(db), db.OrderB, OrderPayload(db, db.OrderB, ("item-1", "X", null, null)));
        Assert.Equal(1, await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_order_links WHERE pedido_id=@P", new { P = second }));
        Assert.Equal(0, await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_produto_vinculo"));

        // Banco sem a tabela (migration ainda não aplicada) também não atrapalha.
        await db.Execute("DROP TABLE ifood_produto_vinculo");
        var third = Guid.NewGuid();
        Assert.True(await ImportOrder(db, LinkingRepository(db), third, OrderPayload(db, third, ("item-1", "X", null, null))) > 0);
    }

    private sealed class ThrowingLinkService(NpgsqlDataSource source) : IfoodItemLinkService(source)
    {
        public override Task RegisterAsync(Guid store, Guid merchant, IReadOnlyList<IfoodImportedItem> items, bool countOrder, CancellationToken ct = default) =>
            throw new InvalidOperationException("falha injetada");
    }

    [DeliveryDatabaseFact]
    public async Task QueueAndDetailEnrichFromTheSamePassWithoutChangingValuesRevisionOrSnapshot()
    {
        await using var db = await LinkDatabase(); var repo = LinkingRepository(db);
        var product = await Product(db, db.StoreA, "X-Burger", image: "/imagens/xburger.png", attention: true, category: "Hambúrgueres");
        var payload = OrderPayload(db, db.OrderA, ("item-1", "X-Burger", "COD-1", null), ("item-2", "Sem vínculo", null, "https://static.ifood/semvinculo.png"));
        var pedido = await ImportOrder(db, repo, db.OrderA, payload);
        var baseline = IfoodOrderNormalizer.Normalize(payload, db.MerchantA, db.OrderA);
        var revision = await db.Scalar<long>("SELECT revision FROM ifood_order_snapshots");
        var digest = await db.Scalar<string>("SELECT md5(payload::text) FROM ifood_order_snapshots");

        await using var connection = await db.Source.OpenConnectionAsync();
        var detail = (await IfoodOrderReadModel.DetailsAsync(connection, db.StoreA, pedido))!;
        var view = await IfoodOrderReadModel.QueueViewsAsync(connection, db.StoreA, new[] { pedido });
        foreach (var order in new[] { detail, view.Officials[pedido] })
        {
            var linked = order.Items[0]; var loose = order.Items[1];
            Assert.True(linked.Vinculado); Assert.Equal(product, linked.ProdutoId); Assert.Equal("Hambúrgueres", linked.CategoriaNome);
            Assert.True(linked.AtencaoMotoboy); Assert.Equal("/imagens/xburger.png", linked.ImagemUrl); Assert.Equal("COD-1", linked.ExternalCode);
            Assert.False(loose.Vinculado); Assert.Null(loose.ProdutoId); Assert.False(loose.AtencaoMotoboy);
            Assert.Equal("https://static.ifood/semvinculo.png", loose.ImagemUrl); // foto do iFood continua aparecendo sem vínculo
            for (var i = 0; i < 2; i++)
                Assert.Equal((baseline.Items[i].Name, baseline.Items[i].Quantity, baseline.Items[i].Unit, baseline.Items[i].UnitPrice, baseline.Items[i].Price, baseline.Items[i].OptionsPrice, baseline.Items[i].TotalPrice),
                    (order.Items[i].Name, order.Items[i].Quantity, order.Items[i].Unit, order.Items[i].UnitPrice, order.Items[i].Price, order.Items[i].OptionsPrice, order.Items[i].TotalPrice));
            Assert.Equal((baseline.OrderAmount, baseline.PendingAmount, baseline.PrepaidAmount), (order.OrderAmount, order.PendingAmount, order.PrepaidAmount));
        }
        Assert.Equal(revision, view.Identities[pedido].Revision);

        // iFood manda foto: ela tem prioridade sobre a do produto.
        var withImage = OrderPayload(db, db.OrderB, ("item-1", "X-Burger", null, "https://static.ifood/xburger.png"));
        var second = await ImportOrder(db, repo, db.OrderB, withImage);
        Assert.Equal("https://static.ifood/xburger.png", (await IfoodOrderReadModel.DetailsAsync(connection, db.StoreA, second))!.Items[0].ImagemUrl);

        // Desfazer, trocar o aviso ou excluir o produto: leitura muda, snapshot/revisão/valores não.
        await db.Execute("UPDATE ifood_produto_vinculo SET estado='desfeito',produto_id=NULL,origem=NULL WHERE chave_id='item-1'");
        var undone = (await IfoodOrderReadModel.DetailsAsync(connection, db.StoreA, pedido))!;
        Assert.False(undone.Items[0].Vinculado); Assert.Equal(baseline.Items[0].TotalPrice, undone.Items[0].TotalPrice);
        await db.Execute("UPDATE ifood_produto_vinculo SET estado='confirmado',origem='manual',produto_id=@P WHERE chave_id='item-1'", new { P = product });
        Assert.True((await IfoodOrderReadModel.DetailsAsync(connection, db.StoreA, pedido))!.Items[0].Vinculado);
        await db.Execute("UPDATE cardapio_produto SET deleted_at=NOW() WHERE id=@P", new { P = product });
        Assert.False((await IfoodOrderReadModel.DetailsAsync(connection, db.StoreA, pedido))!.Items[0].Vinculado); // excluído = sem vínculo
        Assert.False(IfoodOrderReadModel.QueueViewsAsync(connection, db.StoreA, new[] { pedido }).Result.Officials[pedido].Items[0].Vinculado);
        Assert.Equal(revision, await db.Scalar<long>("SELECT revision FROM ifood_order_snapshots WHERE external_order_id=@O", new { O = db.OrderA }));
        Assert.Equal(digest, await db.Scalar<string>("SELECT md5(payload::text) FROM ifood_order_snapshots WHERE external_order_id=@O", new { O = db.OrderA }));
    }

    [DeliveryDatabaseFact]
    public async Task LinksNeverLeakAcrossStores()
    {
        await using var db = await LinkDatabase(); var repo = LinkingRepository(db);
        var pedido = await ImportOrder(db, repo, db.OrderA, OrderPayload(db, db.OrderA, ("item-1", "X-Burger", null, null)));
        // A loja B tem produto de mesmo nome e uma chave igual: nada disso aparece no pedido da loja A.
        var foreign = await Product(db, db.StoreB, "X-Burger", image: "/b.png", attention: true, category: "Da B");
        await db.Execute("INSERT INTO ifood_produto_vinculo(estabelecimento_id,merchant_id,chave_id,nome_ifood,estado,produto_id,origem)VALUES(@S,@M,'item-1','X-Burger','confirmado',@P,'manual')",
            new { S = db.StoreB, M = db.MerchantB, P = foreign });
        await using var connection = await db.Source.OpenConnectionAsync();
        var item = (await IfoodOrderReadModel.DetailsAsync(connection, db.StoreA, pedido))!.Items[0];
        Assert.False(item.Vinculado); Assert.Null(item.ProdutoId); Assert.Null(item.CategoriaNome); Assert.False(item.AtencaoMotoboy);
        Assert.Null(await IfoodOrderReadModel.DetailsAsync(connection, db.StoreB, pedido));
        Assert.Equal("pendente", (await db.Scalar<string>("SELECT estado FROM ifood_produto_vinculo WHERE estabelecimento_id=@S AND chave_id='item-1'", new { S = db.StoreA })));
    }

    [DeliveryDatabaseFact]
    public async Task ChecklistVersionIsUnchangedByLinkAndChangesWithTheBagWarning()
    {
        await using var db = await LinkDatabase(); var repo = LinkingRepository(db);
        var payload = OrderPayload(db, db.OrderA, ("item-1", "X-Burger", null, null), ("item-2", "Batata", null, null));
        var pedido = await ImportOrder(db, repo, db.OrderA, payload);
        var baseline = DeliveryChecklistRules.BuildIfood(IfoodOrderNormalizer.Normalize(payload, db.MerchantA, db.OrderA)); // como era antes do vínculo
        await using var connection = await db.Source.OpenConnectionAsync();
        var unlinked = DeliveryChecklistRules.BuildIfood((await IfoodOrderReadModel.DetailsAsync(connection, db.StoreA, pedido))!);
        Assert.Equal(baseline.Version, unlinked.Version);

        var product = await Product(db, db.StoreA, "X-Burger", image: "/x.png");
        await IfoodItemLinkService.ReevaluatePendingAsync(connection, db.StoreA, "X-Burger");
        var linked = DeliveryChecklistRules.BuildIfood((await IfoodOrderReadModel.DetailsAsync(connection, db.StoreA, pedido))!);
        Assert.Equal(baseline.Version, linked.Version);                      // foto não muda a versão
        Assert.Equal("/x.png", linked.Items[0].ImageUrl); Assert.False(linked.Items[0].Extra);

        await db.Execute("UPDATE cardapio_produto SET atencao_motoboy=TRUE WHERE id=@P", new { P = product });
        var warned = DeliveryChecklistRules.BuildIfood((await IfoodOrderReadModel.DetailsAsync(connection, db.StoreA, pedido))!);
        Assert.NotEqual(baseline.Version, warned.Version);                   // aviso novo exige nova conferência
        Assert.True(warned.Items[0].Extra); Assert.False(warned.Items[1].Extra);
        var oldConfirmation = new DeliveryChecklistConfirmation { PedidoId = pedido, Version = baseline.Version, ConfirmedKeys = baseline.Items.Select(i => i.Key).ToList(), RecheckedExtraKeys = new() };
        Assert.Equal("CHECKLIST_CHANGED", Assert.Throws<DeliveryDomainException>(() => DeliveryChecklistRules.Validate(pedido, warned, oldConfirmation)).Code);
    }

    [DeliveryDatabaseFact]
    public async Task PanelEndpointsScopeByTokenStoreAndAnswer200_404_409()
    {
        await using var db = await LinkDatabase(); var repo = LinkingRepository(db);
        await ImportOrder(db, repo, db.OrderA, OrderPayload(db, db.OrderA, ("item-1", "Suco de uva", "C1", "https://static.ifood/suco.png"), ("item-2", "Pastel", null, null)));
        var suco = await db.Scalar<Guid>("SELECT id FROM ifood_produto_vinculo WHERE chave_id='item-1'");
        var pastel = await db.Scalar<Guid>("SELECT id FROM ifood_produto_vinculo WHERE chave_id='item-2'");
        var produtoA = await Product(db, db.StoreA, "Suco Integral"); var produtoA2 = await Product(db, db.StoreA, "Pastel de carne");
        var foreignProduct = await Product(db, db.StoreB, "Do B");
        var controller = Controller(db, db.StoreA);

        var resumo = Body<IfoodVinculoResumoDto>(await controller.Resumo(default)).Data!;
        Assert.Equal((true, 2), (resumo.Conectado, resumo.Pendentes));
        var pendentes = Body<IfoodVinculoPendentesDto>(await controller.Pendentes(null, 1, 20, default)).Data!;
        Assert.Equal(2, pendentes.Total);
        var first = pendentes.Itens.Single(i => i.VinculoId == suco);
        Assert.Equal(("Suco de uva", "C1", "https://static.ifood/suco.png", 1, false), (first.Nome, first.ExternalCode, first.ImagemUrl, first.Pedidos, first.Desfeito));
        Assert.Equal(suco, Body<IfoodVinculoPendentesDto>(await controller.Pendentes("UVA", 1, 20, default)).Data!.Itens.Single().VinculoId);
        Assert.Empty(Body<IfoodVinculoPendentesDto>(await controller.Pendentes("100%", 1, 20, default)).Data!.Itens); // % não vira curinga
        Assert.Single(Body<IfoodVinculoPendentesDto>(await controller.Pendentes(null, 2, 1, default)).Data!.Itens);
        Assert.Empty(Body<IfoodVinculoPendentesDto>(await controller.Pendentes(null, 3, 1, default)).Data!.Itens);

        // Ligar: 200; repetir é seguro; ligar o mesmo item a outro produto é 409 com o dono atual.
        var linked = Body<IfoodProdutoVinculoDto>(await controller.Vincular(produtoA, new IfoodVincularRequest { VinculoId = suco }, default)).Data!;
        Assert.Equal((suco, "manual", 7, "Suco de uva"), (linked.VinculoId, linked.Origem, linked.ConfirmadoPor, linked.NomeIfood));
        Body<IfoodProdutoVinculoDto>(await controller.Vincular(produtoA, new IfoodVincularRequest { VinculoId = suco }, default));
        var conflict = Body<object>(await controller.Vincular(produtoA2, new IfoodVincularRequest { VinculoId = suco }, default), 409);
        Assert.Equal("IFOOD_VINCULO_JA_USADO", conflict.Code);
        var details = System.Text.Json.JsonSerializer.Serialize(conflict.Details);
        Assert.Contains(produtoA.ToString(), details); Assert.Contains("Suco Integral", details);
        Assert.Equal(produtoA, await db.Scalar<Guid>("SELECT produto_id FROM ifood_produto_vinculo WHERE id=@Id", new { Id = suco }));

        // Do produto: lista só os confirmados dele; resumo desce para 1.
        Assert.Equal(suco, Assert.Single(Body<IfoodProdutoVinculosDto>(await controller.DoProduto(produtoA, default)).Data!.Vinculos).VinculoId);
        Assert.Empty(Body<IfoodProdutoVinculosDto>(await controller.DoProduto(produtoA2, default)).Data!.Vinculos);
        Assert.Equal(1, Body<IfoodVinculoResumoDto>(await controller.Resumo(default)).Data!.Pendentes);

        // 404: produto/vínculo de outra loja, inexistentes, e a loja B nunca enxerga os itens da A.
        Assert.Equal("PRODUTO_NAO_ENCONTRADO", Body<object>(await controller.Vincular(foreignProduct, new IfoodVincularRequest { VinculoId = pastel }, default), 404).Code);
        Assert.Equal("PRODUTO_NAO_ENCONTRADO", Body<object>(await controller.DoProduto(Guid.NewGuid(), default), 404).Code);
        Assert.Equal("IFOOD_VINCULO_NAO_ENCONTRADO", Body<object>(await controller.Vincular(produtoA2, new IfoodVincularRequest { VinculoId = Guid.NewGuid() }, default), 404).Code);
        var storeB = Controller(db, db.StoreB);
        Assert.Equal("IFOOD_VINCULO_NAO_ENCONTRADO", Body<object>(await storeB.Vincular(foreignProduct, new IfoodVincularRequest { VinculoId = pastel }, default), 404).Code);
        Assert.Equal("IFOOD_VINCULO_NAO_ENCONTRADO", Body<object>(await storeB.Desfazer(pastel, default), 404).Code);
        Assert.Equal("PRODUTO_NAO_ENCONTRADO", Body<object>(await storeB.DoProduto(produtoA, default), 404).Code);
        Assert.Equal(0, Body<IfoodVinculoPendentesDto>(await storeB.Pendentes(null, 1, 20, default)).Data!.Total);
        Assert.Equal(400, ((ObjectResult)await controller.Vincular(produtoA, new IfoodVincularRequest(), default)).StatusCode);
        Assert.Equal(400, ((ObjectResult)await controller.Vincular(produtoA, null, default)).StatusCode);

        // Desfazer: 200, grava quem/quando, volta à lista de pendentes marcado como desfeito; pendente nunca ligado é 409.
        Assert.Equal("desfeito", Body<IfoodVinculoDesfeitoDto>(await controller.Desfazer(suco, default)).Data!.Estado);
        Assert.Equal("IFOOD_VINCULO_NAO_CONFIRMADO", Body<object>(await controller.Desfazer(pastel, default), 409).Code);
        var afterUndo = Body<IfoodVinculoPendentesDto>(await controller.Pendentes(null, 1, 20, default)).Data!;
        Assert.True(afterUndo.Itens.Single(i => i.VinculoId == suco).Desfeito);
        Assert.Equal(2, afterUndo.Total);
        Assert.True(await db.Scalar<bool>("SELECT desfeito_por=7 AND desfeito_em IS NOT NULL FROM ifood_produto_vinculo WHERE id=@Id", new { Id = suco }));

        // Produto excluído solta o vínculo: o item pode ir para outro produto sem 409.
        Body<IfoodProdutoVinculoDto>(await controller.Vincular(produtoA, new IfoodVincularRequest { VinculoId = suco }, default));
        await db.Execute("UPDATE cardapio_produto SET deleted_at=NOW() WHERE id=@P", new { P = produtoA });
        Body<IfoodProdutoVinculoDto>(await controller.Vincular(produtoA2, new IfoodVincularRequest { VinculoId = suco }, default));
        Assert.Equal(produtoA2, await db.Scalar<Guid>("SELECT produto_id FROM ifood_produto_vinculo WHERE id=@Id", new { Id = suco }));
    }

    [DeliveryDatabaseFact]
    public async Task PanelAnswersOnlyForStoresWithAnAuthorizedIfoodConnection()
    {
        await using var db = await LinkDatabase(); var repo = LinkingRepository(db);
        await ImportOrder(db, repo, db.OrderA, OrderPayload(db, db.OrderA, ("item-1", "Suco", null, null)));
        var vinculo = await db.Scalar<Guid>("SELECT id FROM ifood_produto_vinculo");
        var produto = await Product(db, db.StoreA, "Outro");
        var controller = Controller(db, db.StoreA);
        await db.Execute("UPDATE ifood_connections SET status='revoked',revoked_at_utc=NOW() WHERE merchant_id=@M", new { M = db.MerchantA });
        var resumo = Body<IfoodVinculoResumoDto>(await controller.Resumo(default)).Data!;
        Assert.Equal((false, 0), (resumo.Conectado, resumo.Pendentes));
        Assert.Equal("IFOOD_NAO_CONECTADO", Body<object>(await controller.Pendentes(null, 1, 20, default), 404).Code);
        Assert.Equal("IFOOD_NAO_CONECTADO", Body<object>(await controller.DoProduto(produto, default), 404).Code);
        Assert.Equal("IFOOD_NAO_CONECTADO", Body<object>(await controller.Vincular(produto, new IfoodVincularRequest { VinculoId = vinculo }, default), 404).Code);
        Assert.Equal("IFOOD_NAO_CONECTADO", Body<object>(await controller.Desfazer(vinculo, default), 404).Code);
    }

    [Fact]
    public void PanelEndpointsRequireTheSamePermissionThatEditsTheMenu()
    {
        var actions = typeof(CardapioIfoodVinculoController).GetMethods().Where(m => m.DeclaringType == typeof(CardapioIfoodVinculoController) && m.GetCustomAttributes(typeof(HttpMethodAttribute), true).Any()).ToList();
        Assert.Equal(6, actions.Count);
        foreach (var action in actions)
        {
            var permission = Assert.Single(action.GetCustomAttributes(typeof(RequirePermissionAttribute), true).Cast<RequirePermissionAttribute>());
            // Criar produto a partir do item pede a mesma permissão de criar produto no cardápio.
            var expected = action.Name == nameof(CardapioIfoodVinculoController.CriarProduto) ? "criar" : "editar";
            Assert.Equal(("Cardapio", expected), (permission.Modulo, permission.Acao));
        }
        Assert.Equal(new[] { "api/v2/cardapio/ifood/pendentes", "api/v2/cardapio/ifood/pendentes/{vinculoId:guid}/criar-produto",
                "api/v2/cardapio/ifood/resumo", "api/v2/cardapio/ifood/vinculos/{vinculoId:guid}",
                "api/v2/cardapio/produtos/{produtoId:guid}/ifood", "api/v2/cardapio/produtos/{produtoId:guid}/ifood/vinculos" },
            actions.Select(a => ((HttpMethodAttribute)a.GetCustomAttributes(typeof(HttpMethodAttribute), true).Single()).Template!).Order().ToArray());
    }
}
