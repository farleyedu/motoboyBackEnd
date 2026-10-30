using APIBack.Acontecimentos;
using APIBack.GestaoLogica.Estoque;
using APIBack.GestaoLogica.Vendas;
using APIBack.Repository;
using Dapper;
using Npgsql;
using Xunit;

namespace APIBack.Tests.Integration;

/// <summary>
/// 6C3-A contra PostgreSQL de verdade: a ficha persiste com quantidades e modalidades, embalagem de delivery não vaza para as outras
/// modalidades, o de-para (iFood e cardápio) sobrevive à recarga e resolve o produto certo nos itens vendidos, e nada cruza lojas.
/// </summary>
public class GestaoVendasFichaDatabaseTests
{
    private static async Task<(NpgsqlDataSource Fonte, string Schema)> Abrir()
    {
        var conexao = Environment.GetEnvironmentVariable("TEST_DELIVERY_DATABASE")!;
        APIBack.Infrastructure.DapperConfiguration.Configure();
        var opcoes = new NpgsqlConnectionStringBuilder(conexao);
        if (opcoes.Host != "127.0.0.1" && opcoes.Host != "localhost") throw new InvalidOperationException("Use apenas PostgreSQL local.");
        var schema = "gestao_ficha_" + Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(conexao)) { await admin.OpenAsync(); await admin.ExecuteAsync($"CREATE SCHEMA {schema}"); }
        opcoes.SearchPath = schema;
        var fonte = NpgsqlDataSource.Create(opcoes.ConnectionString);
        await using var c = await fonte.OpenConnectionAsync();
        await c.ExecuteAsync("""
CREATE TABLE estabelecimentos(id uuid PRIMARY KEY, timezone_iana text);
CREATE TABLE usuario(id integer PRIMARY KEY, nome text);
INSERT INTO usuario VALUES (7, 'Marina');
CREATE TABLE cardapio_produto(id uuid PRIMARY KEY, id_estabelecimento uuid NOT NULL REFERENCES estabelecimentos(id), nome text NOT NULL,
    preco_base numeric(10,2) NOT NULL, imagem_url text, deleted_at timestamptz);
CREATE TABLE pedido(id serial PRIMARY KEY, id_estabelecimento uuid NOT NULL, created_at timestamptz NOT NULL DEFAULT now(), UNIQUE (id, id_estabelecimento));
CREATE TABLE pedido_item(id bigserial PRIMARY KEY, pedido_id integer NOT NULL REFERENCES pedido(id), produto_id uuid, nome text NOT NULL,
    quantidade integer NOT NULL, preco_unitario numeric(12,2) NOT NULL, ordem integer NOT NULL DEFAULT 0);
CREATE TABLE ifood_connections(merchant_id uuid NOT NULL, estabelecimento_id uuid NOT NULL, status text, authorized_at_utc timestamptz, revoked_at_utc timestamptz,
    PRIMARY KEY (merchant_id, estabelecimento_id));
CREATE TABLE ifood_order_links(merchant_id uuid NOT NULL, external_order_id uuid NOT NULL, estabelecimento_id uuid NOT NULL, pedido_id integer NOT NULL,
    created_at_utc timestamptz NOT NULL DEFAULT now(), PRIMARY KEY (merchant_id, external_order_id));
CREATE TABLE ifood_order_snapshots(merchant_id uuid NOT NULL, external_order_id uuid NOT NULL, estabelecimento_id uuid NOT NULL, payload jsonb NOT NULL,
    updated_at_utc timestamptz NOT NULL DEFAULT now(), PRIMARY KEY (merchant_id, external_order_id));
CREATE TABLE ifood_produto_vinculo(id uuid PRIMARY KEY DEFAULT gen_random_uuid(), estabelecimento_id uuid NOT NULL, merchant_id uuid NOT NULL,
    tipo text NOT NULL DEFAULT 'item', chave_id text NOT NULL, external_code text, nome_ifood text NOT NULL, imagem_url text, produto_id uuid,
    estado text NOT NULL DEFAULT 'pendente', origem text, pedidos_count integer NOT NULL DEFAULT 0, visto_primeira_em timestamptz NOT NULL DEFAULT now(),
    visto_ultima_em timestamptz NOT NULL DEFAULT now(), confirmado_por integer, confirmado_em timestamptz, desfeito_em timestamptz, desfeito_por integer);
""");
        foreach (var m in new[] { "Inteligencia/20261019_01_ingredientes.sql", "Delivery/20261120_01_gestao_ficha_tecnica.sql" })
            await c.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", m)));
        return (fonte, schema);
    }

    private static async Task Fechar(NpgsqlDataSource fonte, string schema)
    {
        await fonte.DisposeAsync();
        await using var admin = new NpgsqlConnection(Environment.GetEnvironmentVariable("TEST_DELIVERY_DATABASE")!);
        await admin.OpenAsync();
        await admin.ExecuteAsync($"DROP SCHEMA {schema} CASCADE");
    }

    private static async Task<Guid> Insumo(NpgsqlConnection c, Guid loja, string nome, string unidade, string categoria)
    {
        var id = Guid.NewGuid();
        await c.ExecuteAsync("INSERT INTO insumo (id, estabelecimento_id, nome, unidade, categoria, criado_por) VALUES (@id, @loja, @nome, @unidade, @categoria, 7)",
            new { id, loja, nome, unidade, categoria });
        return id;
    }

    private static async Task<Guid> Produto(NpgsqlConnection c, Guid loja, string nome, decimal preco = 30m)
    {
        var id = Guid.NewGuid();
        await c.ExecuteAsync("INSERT INTO cardapio_produto (id, id_estabelecimento, nome, preco_base) VALUES (@id, @loja, @nome, @preco)", new { id, loja, nome, preco });
        return id;
    }

    [DeliveryDatabaseFact]
    public async Task Ficha_persiste_com_modalidades_isola_lojas_e_so_cadastra()
    {
        var (fonte, schema) = await Abrir();
        try
        {
            Guid lojaA = Guid.NewGuid(), lojaB = Guid.NewGuid();
            await using var c = await fonte.OpenConnectionAsync();
            await c.ExecuteAsync("INSERT INTO estabelecimentos VALUES (@lojaA, 'America/Sao_Paulo'), (@lojaB, 'America/Sao_Paulo')", new { lojaA, lojaB });
            Guid pao = await Insumo(c, lojaA, "Pão brioche", "un", "Padaria"), caixa = await Insumo(c, lojaA, "Caixa kraft M", "un", "Embalagens");
            var estranho = await Insumo(c, lojaB, "Pão da outra loja", "un", "Padaria");
            Guid burger = await Produto(c, lojaA, "X-Zippy"), alheio = await Produto(c, lojaB, "Produto da B");
            var servico = new VendasCatalogoService(fonte, new IfoodProdutoVinculoRepository(fonte));

            var salva = await servico.SalvarFicha(lojaA, 7, burger, new[]
            {
                new ComponenteRequest(pao, 1, "un", null), new ComponenteRequest(caixa, 1, "un", new[] { "delivery" }),
            }, CancellationToken.None);
            Assert.Equal(2, salva.Componentes.Count);

            // Recarga: leitor novo lê do PostgreSQL com quantidades e modalidades reais.
            var lidas = await new FichaTecnicaLeitorPostgres(fonte).Ler(lojaA, new[] { burger }, CancellationToken.None);
            var ficha = Assert.Single(lidas).Value;
            Assert.Equal(1m, ficha.Componentes.Single(x => x.InsumoId == pao).Quantidade);
            Assert.Null(ficha.Componentes.Single(x => x.InsumoId == pao).Modalidades);
            Assert.Equal(new[] { "delivery" }, ficha.Componentes.Single(x => x.InsumoId == caixa).Modalidades);

            // Embalagem de delivery só aparece no delivery; ingrediente aparece em todas.
            var lida = await servico.Ficha(lojaA, burger, CancellationToken.None);
            Assert.Single(lida!.PorModalidade["delivery"].Embalagens);
            Assert.Empty(lida.PorModalidade["salao"].Embalagens);
            Assert.Empty(lida.PorModalidade["retirada"].Embalagens);
            Assert.All(new[] { "salao", "retirada", "delivery" }, m => Assert.Single(lida.PorModalidade[m].Ingredientes));
            Assert.All(lida.Componentes, x => Assert.Null(x.Custo)); // sem preço informado, custo desconhecido (nunca zero)

            await c.ExecuteAsync("""
INSERT INTO insumo_custo (id, estabelecimento_id, insumo_id, quantidade_compra, unidade_compra, valor_compra, custo_base, criado_por)
VALUES (gen_random_uuid(), @lojaA, @pao, 10, 'un', 25, 2.5, 7)
""", new { lojaA, pao });
            Assert.Equal(2.5m, (await servico.Ficha(lojaA, burger, CancellationToken.None))!.Componentes.Single(x => x.InsumoId == pao).Custo);

            // Isolamento entre lojas: produto, insumo e leitura.
            await Assert.ThrowsAsync<GestaoException>(() => servico.Ficha(lojaA, alheio, CancellationToken.None));
            await Assert.ThrowsAsync<GestaoException>(() => servico.SalvarFicha(lojaA, 7, alheio, new[] { new ComponenteRequest(pao, 1, "un", null) }, CancellationToken.None));
            await Assert.ThrowsAsync<GestaoException>(() => servico.SalvarFicha(lojaA, 7, burger, new[] { new ComponenteRequest(estranho, 1, "un", null) }, CancellationToken.None));
            Assert.Empty(await new FichaTecnicaLeitorPostgres(fonte).Ler(lojaB, new[] { burger }, CancellationToken.None));
            // A tentativa barrada não mexeu na ficha.
            Assert.Equal(2, (await servico.Ficha(lojaA, burger, CancellationToken.None))!.Componentes.Count);

            // Ficha só cadastra: o livro de estoque nem existe neste schema, e nada tentou escrever nele.
            Assert.False(await c.ExecuteScalarAsync<bool>("SELECT to_regclass('gestao_movimentos_estoque') IS NOT NULL"));

            // Ficha vazia limpa o produto: ele volta a "sem ficha".
            await servico.SalvarFicha(lojaA, 7, burger, Array.Empty<ComponenteRequest>(), CancellationToken.None);
            Assert.Empty(await new FichaTecnicaLeitorPostgres(fonte).Ler(lojaA, new[] { burger }, CancellationToken.None));
            Assert.Null((await servico.ProdutosVendidos(lojaA, CancellationToken.None)).Single(p => p.Id == burger).FichaId);
        }
        finally { await Fechar(fonte, schema); }
    }

    [DeliveryDatabaseFact]
    public async Task Itens_vendidos_resolvem_o_produto_certo_no_ifood_e_no_cardapio_e_o_de_para_persiste()
    {
        var (fonte, schema) = await Abrir();
        try
        {
            Guid lojaA = Guid.NewGuid(), lojaB = Guid.NewGuid(), merchant = Guid.NewGuid();
            await using var c = await fonte.OpenConnectionAsync();
            await c.ExecuteAsync("INSERT INTO estabelecimentos VALUES (@lojaA, 'America/Sao_Paulo'), (@lojaB, 'America/Sao_Paulo')", new { lojaA, lojaB });
            await c.ExecuteAsync("INSERT INTO ifood_connections VALUES (@merchant, @lojaA, 'authorized', now(), NULL)", new { merchant, lojaA });
            Guid burger = await Produto(c, lojaA, "X-Zippy", 32.9m), brownie = await Produto(c, lojaA, "Brownie da Casa", 12m), alheio = await Produto(c, lojaB, "Da outra loja");

            // Pedido do cardápio: uma linha com produto real, uma avulsa por nome, uma apontando para produto de outra loja.
            var pedidoWeb = await c.ExecuteScalarAsync<int>("INSERT INTO pedido (id_estabelecimento) VALUES (@lojaA) RETURNING id", new { lojaA });
            await c.ExecuteAsync("""
INSERT INTO pedido_item (pedido_id, produto_id, nome, quantidade, preco_unitario, ordem) VALUES
 (@p, @burger, 'X-Zippy', 2, 32.9, 0), (@p, NULL, 'Brownie  da casa', 1, 12, 1), (@p, @alheio, 'Intruso', 1, 5, 2)
""", new { p = pedidoWeb, burger, alheio });

            // Pedido iFood: itens só no snapshot oficial; o vínculo (item iFood → X-Zippy) é confirmado pelo serviço.
            var pedidoIfood = await c.ExecuteScalarAsync<int>("INSERT INTO pedido (id_estabelecimento) VALUES (@lojaA) RETURNING id", new { lojaA });
            var externo = Guid.NewGuid();
            await c.ExecuteAsync("INSERT INTO ifood_order_links (merchant_id, external_order_id, estabelecimento_id, pedido_id) VALUES (@merchant, @externo, @lojaA, @pedidoIfood)",
                new { merchant, externo, lojaA, pedidoIfood });
            await c.ExecuteAsync("""
INSERT INTO ifood_order_snapshots (merchant_id, external_order_id, estabelecimento_id, payload) VALUES (@merchant, @externo, @lojaA,
 '{"items":[{"id":"item-1","name":"X-Zippy Burger","quantity":3,"unitPrice":34.9},{"id":"item-2","name":"Combo misterioso","quantity":1,"unitPrice":44.9}]}'::jsonb)
""", new { merchant, externo, lojaA });
            await c.ExecuteAsync("""
INSERT INTO ifood_produto_vinculo (estabelecimento_id, merchant_id, chave_id, nome_ifood) VALUES (@lojaA, @merchant, 'item-1', 'X-Zippy Burger'), (@lojaA, @merchant, 'item-2', 'Combo misterioso')
""", new { lojaA, merchant });

            var servico = new VendasCatalogoService(fonte, new IfoodProdutoVinculoRepository(fonte));
            var leitor = new ItensVendidosLeitor(fonte);

            // Antes de vincular: iFood e a linha avulsa ficam "sem produto"; produto de outra loja nunca vale.
            var antes = await leitor.Ler(lojaA, new[] { pedidoWeb, pedidoIfood }, CancellationToken.None);
            Assert.Equal(burger, antes.Single(i => i.Nome == "X-Zippy").ProdutoId);
            Assert.Null(antes.Single(i => i.Nome == "Brownie  da casa").ProdutoId);
            Assert.Null(antes.Single(i => i.Nome == "Intruso").ProdutoId);
            Assert.Null(antes.Single(i => i.Nome == "X-Zippy Burger").ProdutoId);
            Assert.Equal(3, antes.Single(i => i.Nome == "X-Zippy Burger").Quantidade);

            var lista = await servico.DeParaVendas(lojaA, CancellationToken.None);
            var ifoodItem = lista.Single(v => v.NomeNoCanal == "X-Zippy Burger");
            var avulso = lista.Single(v => v.Canal == "cardapio");
            Assert.Null(ifoodItem.ProdutoVendidoId);
            Assert.Equal(34.9m, ifoodItem.PrecoNoCanal);

            await servico.VincularVenda(lojaA, 7, ifoodItem.Id, burger, CancellationToken.None);
            await servico.VincularVenda(lojaA, 7, avulso.Id, brownie, CancellationToken.None);

            // Recarga: serviço e leitor novos enxergam o vínculo gravado, com o produto correto em cada canal.
            var recarregado = new VendasCatalogoService(fonte, new IfoodProdutoVinculoRepository(fonte));
            var depois = await recarregado.DeParaVendas(lojaA, CancellationToken.None);
            Assert.Equal(burger, depois.Single(v => v.Id == ifoodItem.Id).ProdutoVendidoId);
            Assert.True(depois.Single(v => v.Id == ifoodItem.Id).PrecoDiverge); // 34,90 no iFood x 32,90 no cardápio
            Assert.Equal(brownie, depois.Single(v => v.Id == avulso.Id).ProdutoVendidoId);
            var itens = await new ItensVendidosLeitor(fonte).Ler(lojaA, new[] { pedidoWeb, pedidoIfood }, CancellationToken.None);
            Assert.Equal(burger, itens.Single(i => i.Nome == "X-Zippy Burger").ProdutoId);
            Assert.Equal(brownie, itens.Single(i => i.Nome == "Brownie  da casa").ProdutoId);
            Assert.Null(itens.Single(i => i.Nome == "Combo misterioso").ProdutoId);

            // Isolamento: a outra loja não lê os pedidos nem liga item a produto que não é dela.
            Assert.Empty(await leitor.Ler(lojaB, new[] { pedidoWeb, pedidoIfood }, CancellationToken.None));
            await Assert.ThrowsAsync<GestaoException>(() => servico.VincularVenda(lojaA, 7, ifoodItem.Id, alheio, CancellationToken.None));
            await Assert.ThrowsAsync<GestaoException>(() => servico.VincularVenda(lojaB, 7, avulso.Id, alheio, CancellationToken.None));
            await Assert.ThrowsAsync<GestaoException>(() => servico.VincularVenda(lojaB, 7, ifoodItem.Id, alheio, CancellationToken.None));

            // Nem o vínculo nem a leitura mexem no snapshot oficial.
            Assert.Equal(2, await c.ExecuteScalarAsync<int>("SELECT jsonb_array_length(payload->'items') FROM ifood_order_snapshots"));
        }
        finally { await Fechar(fonte, schema); }
    }
}
