using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using APIBack.DTOs.Cardapio;
using APIBack.DTOs.Delivery;
using APIBack.Model.Cardapio;
using APIBack.Service;
using Xunit;

namespace APIBack.Tests.Unit
{
    public class PedidoFiltroTests
    {
        [Fact]
        public void Defaults_when_request_is_empty()
        {
            var filtro = PedidoFiltro.From(null);

            Assert.Empty(filtro.Status);
            Assert.Empty(filtro.Origem);
            Assert.Equal(1, filtro.Page);
            Assert.Equal(30, filtro.PageSize);
            Assert.Equal(0, filtro.Offset);
        }

        [Fact]
        public void Status_keys_map_to_codes_and_include_draft()
        {
            var filtro = PedidoFiltro.From(new PedidoFiltroRequest { Status = "rascunho, pendente,em_rota,pendente" });

            Assert.Equal(new[] { 6, 1, 2 }, filtro.Status);
        }

        [Fact]
        public void Unknown_status_or_origin_is_rejected()
        {
            var status = Assert.Throws<DeliveryDomainException>(() => PedidoFiltro.From(new PedidoFiltroRequest { Status = "voando" }));
            Assert.Equal(422, status.StatusCode);
            Assert.Throws<DeliveryDomainException>(() => PedidoFiltro.From(new PedidoFiltroRequest { Origem = "telepatia" }));
        }

        [Fact]
        public void Origins_are_normalized()
        {
            var filtro = PedidoFiltro.From(new PedidoFiltroRequest { Origem = "ifood,cardapio_web" });

            Assert.Equal(new[] { "ifood", "cardapio_web" }, filtro.Origem);
        }

        [Theory]
        [InlineData("2026-13-40", null)]
        [InlineData("26/09/2026", null)]
        [InlineData(null, "abc")]
        public void Invalid_dates_are_rejected(string? de, string? ate)
        {
            Assert.Throws<DeliveryDomainException>(() => PedidoFiltro.From(new PedidoFiltroRequest { De = de, Ate = ate }));
        }

        [Fact]
        public void Start_after_end_is_rejected()
        {
            Assert.Throws<DeliveryDomainException>(() => PedidoFiltro.From(new PedidoFiltroRequest { De = "2026-09-26", Ate = "2026-09-25" }));
        }

        [Fact]
        public void Page_size_is_capped_and_page_floored()
        {
            var filtro = PedidoFiltro.From(new PedidoFiltroRequest { Page = -3, PageSize = 5000 });

            Assert.Equal(1, filtro.Page);
            Assert.Equal(PedidoFiltro.MaxPageSize, filtro.PageSize);
        }

        [Fact]
        public void Offset_follows_page()
        {
            var filtro = PedidoFiltro.From(new PedidoFiltroRequest { Page = 3, PageSize = 20 });

            Assert.Equal(40, filtro.Offset);
        }

        [Fact]
        public void Numeric_search_matches_id_and_phone()
        {
            var filtro = PedidoFiltro.From(new PedidoFiltroRequest { Busca = "#1234" });

            Assert.Equal(1234, filtro.BuscaId);
            Assert.Equal("1234", filtro.BuscaDigitos);
        }

        [Fact]
        public void Long_numeric_search_is_phone_only()
        {
            var filtro = PedidoFiltro.From(new PedidoFiltroRequest { Busca = "34999990000" });

            Assert.Null(filtro.BuscaId);
            Assert.Equal("34999990000", filtro.BuscaDigitos);
        }

        [Fact]
        public void Text_search_escapes_like_wildcards()
        {
            var filtro = PedidoFiltro.From(new PedidoFiltroRequest { Busca = "100%_ok" });

            Assert.Null(filtro.BuscaDigitos);
            Assert.Equal("%100\\%\\_ok%", filtro.BuscaLike);
        }

        [Fact]
        public void Overlong_search_is_rejected()
        {
            Assert.Throws<DeliveryDomainException>(() => PedidoFiltro.From(new PedidoFiltroRequest { Busca = new string('a', 101) }));
        }
    }

    public class LegacyItemsParserTests
    {
        [Fact]
        public void Empty_returns_no_items()
        {
            Assert.Empty(LegacyItemsParser.Parse(null));
            Assert.Empty(LegacyItemsParser.Parse("  "));
        }

        [Fact]
        public void Json_array_is_read_with_quantity_and_price()
        {
            var items = LegacyItemsParser.Parse("[{\"nome\":\"X-Bacon\",\"quantidade\":2,\"preco\":23.5}]");

            var item = Assert.Single(items);
            Assert.Equal("X-Bacon", item.Nome);
            Assert.Equal(2, item.Quantidade);
            Assert.Equal(23.5m, item.PrecoUnitario);
            Assert.Equal(47m, item.Total);
        }

        [Fact]
        public void Ifood_style_keys_are_understood()
        {
            var items = LegacyItemsParser.Parse("[{\"name\":\"Pizza\",\"quantity\":\"3\",\"price\":\"10,5\"}]");

            var item = Assert.Single(items);
            Assert.Equal("Pizza", item.Nome);
            Assert.Equal(3, item.Quantidade);
            Assert.Equal(10.5m, item.PrecoUnitario);
        }

        [Fact]
        public void Free_text_lines_are_split_with_quantity_prefix()
        {
            var items = LegacyItemsParser.Parse("2x Coca 2L\n1 X-Salada; Batata");

            Assert.Equal(3, items.Count);
            Assert.Equal((2, "Coca 2L"), (items[0].Quantidade, items[0].Nome));
            Assert.Equal((1, "X-Salada"), (items[1].Quantidade, items[1].Nome));
            Assert.Equal((1, "Batata"), (items[2].Quantidade, items[2].Nome));
        }

        [Fact]
        public void Objects_without_name_are_ignored()
        {
            Assert.Empty(LegacyItemsParser.Parse("[{\"quantidade\":2},{\"nome\":\"  \"}]"));
        }

        [Fact]
        public void Units_sum_quantities()
        {
            Assert.Equal(5, LegacyItemsParser.CountUnits("[{\"nome\":\"A\",\"quantidade\":2},{\"nome\":\"B\",\"quantidade\":3}]"));
        }

        [Fact]
        public void Absurd_quantity_is_capped()
        {
            var item = Assert.Single(LegacyItemsParser.Parse("[{\"nome\":\"A\",\"quantidade\":99999999}]"));

            Assert.Equal(1000, item.Quantidade);
        }
    }

    public class CardapioFichaBuilderTests
    {
        private static readonly DateTimeOffset Now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

        private static (CardapioCategoria Categoria, CardapioProduto Produto, Guid AddonItemId) Sample()
        {
            var categoria = new CardapioCategoria { Id = Guid.NewGuid(), Nome = "Lanches", Ordem = 1 };
            var addonItem = Guid.NewGuid();
            var group = CoreFixtures.Group(0, 1, (addonItem, "Bacon extra", 4m));
            group.Tipo = "adicional_global";
            var produto = CoreFixtures.Product(Guid.NewGuid(), 25m, group);
            produto.CategoriaId = categoria.Id;
            return (categoria, produto, addonItem);
        }

        [Fact]
        public void Ficha_exposes_group_item_ids_used_by_pricing()
        {
            var (categoria, produto, addonItem) = Sample();

            var ficha = CardapioFichaBuilder.Build(CoreFixtures.Restaurant(minimum: 20m), new[] { categoria }, new[] { produto },
                new Dictionary<Guid, ProdutoAtendimentoDto>(), Now);

            var item = ficha.Categorias.Single().Produtos.Single().Grupos.Single().Itens.Single();
            Assert.Equal(addonItem, item.Id);
            Assert.Equal(4m, item.Preco);
            Assert.Equal(20m, ficha.Estabelecimento.PedidoMinimo);
        }

        [Fact]
        public void Attendance_fields_are_merged_by_product()
        {
            var (categoria, produto, _) = Sample();
            var atendimento = new Dictionary<Guid, ProdutoAtendimentoDto>
            {
                [produto.Id] = new() { Apelidos = new List<string> { "xis bacon" }, Instrucoes = "Sem cebola so se pedir", TempoExtraPreparoMin = 10 }
            };

            var ficha = CardapioFichaBuilder.Build(CoreFixtures.Restaurant(), new[] { categoria }, new[] { produto }, atendimento, Now);

            var mapped = ficha.Categorias.Single().Produtos.Single();
            Assert.Equal(new[] { "xis bacon" }, mapped.Apelidos);
            Assert.Equal(10, mapped.TempoExtraPreparoMin);
        }

        [Fact]
        public void Categories_without_products_are_dropped()
        {
            var (categoria, produto, _) = Sample();
            var vazia = new CardapioCategoria { Id = Guid.NewGuid(), Nome = "Vazia", Ordem = 0 };

            var ficha = CardapioFichaBuilder.Build(CoreFixtures.Restaurant(), new[] { vazia, categoria }, new[] { produto },
                new Dictionary<Guid, ProdutoAtendimentoDto>(), Now);

            Assert.Single(ficha.Categorias);
        }

        [Fact]
        public void Version_is_stable_across_time_and_changes_with_content()
        {
            var (categoria, produto, _) = Sample();
            var none = new Dictionary<Guid, ProdutoAtendimentoDto>();

            var first = CardapioFichaBuilder.Build(CoreFixtures.Restaurant(), new[] { categoria }, new[] { produto }, none, Now);
            var later = CardapioFichaBuilder.Build(CoreFixtures.Restaurant(), new[] { categoria }, new[] { produto }, none, Now.AddHours(5));
            produto.PrecoBase = 26m;
            var changed = CardapioFichaBuilder.Build(CoreFixtures.Restaurant(), new[] { categoria }, new[] { produto }, none, Now);

            Assert.Equal(16, first.Versao.Length);
            Assert.Equal(first.Versao, later.Versao);
            Assert.NotEqual(first.Versao, changed.Versao);
        }

        [Fact]
        public void Unavailable_product_stays_in_ficha_flagged()
        {
            var (categoria, produto, _) = Sample();
            produto.Disponivel = false;

            var ficha = CardapioFichaBuilder.Build(CoreFixtures.Restaurant(), new[] { categoria }, new[] { produto },
                new Dictionary<Guid, ProdutoAtendimentoDto>(), Now);

            Assert.False(ficha.Categorias.Single().Produtos.Single().Disponivel);
        }

        [Fact]
        public void Normalize_trims_dedupes_and_limits()
        {
            var normalized = CardapioFichaBuilder.Normalize(new ProdutoAtendimentoDto
            {
                Apelidos = new List<string> { " xis ", "XIS", "", "hamburguer" },
                Instrucoes = "  ",
                TempoExtraPreparoMin = 5
            });

            Assert.Equal(new[] { "xis", "hamburguer" }, normalized.Apelidos);
            Assert.Null(normalized.Instrucoes);
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(241)]
        public void Normalize_rejects_extra_time_out_of_range(int minutes)
        {
            Assert.Throws<DeliveryDomainException>(() => CardapioFichaBuilder.Normalize(new ProdutoAtendimentoDto { TempoExtraPreparoMin = minutes }));
        }

        [Fact]
        public void Normalize_rejects_too_many_or_too_long_nicknames()
        {
            Assert.Throws<DeliveryDomainException>(() => CardapioFichaBuilder.Normalize(new ProdutoAtendimentoDto
            {
                Apelidos = Enumerable.Range(0, 21).Select(i => "a" + i).ToList()
            }));
            Assert.Throws<DeliveryDomainException>(() => CardapioFichaBuilder.Normalize(new ProdutoAtendimentoDto
            {
                Apelidos = new List<string> { new string('a', 61) }
            }));
            Assert.Throws<DeliveryDomainException>(() => CardapioFichaBuilder.Normalize(null));
        }
    }

    public class ProdutoAtendimentoMigrationTests
    {
        private static string Read(string relative)
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "Migrations", "Delivery", relative)))
            {
                var probe = Path.Combine(dir, "Migrations", "Delivery", relative);
                if (File.Exists(probe)) break;
                dir = Path.GetDirectoryName(dir);
            }
            return File.ReadAllText(Path.Combine(dir ?? string.Empty, "Migrations", "Delivery", relative));
        }

        [Fact]
        public void Migration_is_additive_and_records_ledger()
        {
            var sql = Read("20260926_01_produto_atendimento.sql");

            Assert.Contains("CREATE TABLE IF NOT EXISTS cardapio_produto_atendimento", sql);
            Assert.Contains("ON DELETE CASCADE", sql);
            Assert.Contains("delivery_tracking_schema_versions", sql);
            Assert.DoesNotContain("DROP TABLE", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ALTER TABLE cardapio_produto ", sql, StringComparison.OrdinalIgnoreCase);
        }
    }
}
