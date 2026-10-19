using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using APIBack.DTOs.Inteligencia;
using APIBack.Repository.Interface;
using APIBack.Service;
using Xunit;

namespace APIBack.Tests.Unit
{
    public sealed class IngredienteRulesTests
    {
        private static CustoRequest Compra(decimal quantidade, string unidade, decimal valor, bool gratuito = false, string? motivo = null) =>
            new() { QuantidadeCompra = quantidade, UnidadeCompra = unidade, ValorCompra = valor, Gratuito = gratuito, Motivo = motivo };

        [Theory]
        [InlineData("g", 2, "kg", 60, "0.03")]
        [InlineData("ml", 1.5, "l", 12, "0.008")]
        [InlineData("un", 30, "un", 24, "0.8")]
        [InlineData("g", 500, "g", 10, "0.02")]
        public void Calculate_ConvertePraUnidadeBase(string unidadeBase, double quantidade, string unidade, double valor, string esperado)
        {
            var custo = IngredienteRules.Calculate(unidadeBase, Compra((decimal)quantidade, unidade, (decimal)valor));
            Assert.Equal(decimal.Parse(esperado, System.Globalization.CultureInfo.InvariantCulture), custo.CustoBase);
        }

        [Fact]
        public void Calculate_NaoMisturaMassaComVolume()
        {
            var erro = Assert.Throws<DeliveryDomainException>(() => IngredienteRules.Calculate("g", Compra(1, "l", 10)));
            Assert.Equal("CUSTO_UNIDADE", erro.Code);
            Assert.Throws<DeliveryDomainException>(() => IngredienteRules.Calculate("un", Compra(1, "kg", 10)));
        }

        [Theory]
        [InlineData(0, 10)]
        [InlineData(-1, 10)]
        [InlineData(1, -5)]
        public void Calculate_RecusaQuantidadeEValorInvalidos(double quantidade, double valor) =>
            Assert.Throws<DeliveryDomainException>(() => IngredienteRules.Calculate("g", Compra((decimal)quantidade, "kg", (decimal)valor)));

        [Fact]
        public void Calculate_ZeroSoValeComoGratuitoComMotivo()
        {
            Assert.Equal("CUSTO_ZERO", Assert.Throws<DeliveryDomainException>(() => IngredienteRules.Calculate("g", Compra(1, "kg", 0))).Code);
            Assert.Equal("CUSTO_MOTIVO", Assert.Throws<DeliveryDomainException>(() => IngredienteRules.Calculate("g", Compra(1, "kg", 0, gratuito: true))).Code);
            var custo = IngredienteRules.Calculate("g", Compra(1, "kg", 0, gratuito: true, motivo: "Doação do fornecedor"));
            Assert.True(custo.Gratuito);
            Assert.Equal(0m, custo.CustoBase);
            Assert.Equal("CUSTO_GRATUITO", Assert.Throws<DeliveryDomainException>(() => IngredienteRules.Calculate("g", Compra(1, "kg", 5, gratuito: true))).Code);
        }

        [Fact]
        public void Calculate_RecusaValorMinusculoQueArredondariaParaZero() =>
            Assert.Equal("CUSTO_VALOR", Assert.Throws<DeliveryDomainException>(() => IngredienteRules.Calculate("g", Compra(1_000_000, "kg", 0.0001m))).Code);

        [Fact]
        public void Validate_NormalizaECobraVersaoNaEdicao()
        {
            var input = IngredienteRules.Validate(new IngredienteRequest { Nome = "  Queijo muçarela ", Unidade = " G ", Categoria = " " }, edicao: false);
            Assert.Equal("Queijo muçarela", input.Nome);
            Assert.Equal("g", input.Unidade);
            Assert.Null(input.Categoria);
            Assert.Equal("INGREDIENTE_VERSAO", Assert.Throws<DeliveryDomainException>(() =>
                IngredienteRules.Validate(new IngredienteRequest { Nome = "A", Unidade = "g" }, edicao: true)).Code);
        }

        [Theory]
        [InlineData("", "g", "INGREDIENTE_NOME")]
        [InlineData("Sal", "kg", "INGREDIENTE_UNIDADE")]
        [InlineData("Sal", "", "INGREDIENTE_UNIDADE")]
        public void Validate_RecusaNomeEUnidadeInvalidos(string nome, string unidade, string codigo) =>
            Assert.Equal(codigo, Assert.Throws<DeliveryDomainException>(() => IngredienteRules.Validate(new IngredienteRequest { Nome = nome, Unidade = unidade }, false)).Code);

        [Fact]
        public void Validate_FotoPrecisaSerHttp()
        {
            Assert.Equal("INGREDIENTE_FOTO", Assert.Throws<DeliveryDomainException>(() =>
                IngredienteRules.Validate(new IngredienteRequest { Nome = "Sal", Unidade = "g", FotoUrl = "javascript:alert(1)" }, false)).Code);
            Assert.Equal("https://cdn.exemplo.com/sal.png",
                IngredienteRules.Validate(new IngredienteRequest { Nome = "Sal", Unidade = "g", FotoUrl = "https://cdn.exemplo.com/sal.png" }, false).FotoUrl);
        }

        [Fact]
        public void Variation_SoExisteComDoisPrecosDiferentesESemPercentualDeZero()
        {
            var desde = DateTimeOffset.UtcNow.AddDays(-10);
            Assert.Null(IngredienteRules.Variation(0.05m, null, null));
            Assert.Null(IngredienteRules.Variation(0.05m, 0.05m, desde));
            var subiu = IngredienteRules.Variation(0.035m, 0.03m, desde)!;
            Assert.Equal(0.005m, subiu.Diferenca);
            Assert.Equal(16.67m, subiu.Percentual);
            Assert.Null(IngredienteRules.Variation(0.03m, 0m, desde)!.Percentual);
        }

        [Fact]
        public void PrecoAlterado_SoNaJanelaDeDias()
        {
            var agora = DateTimeOffset.UtcNow;
            var variacao = IngredienteRules.Variation(2m, 1m, agora.AddDays(-60));
            Assert.True(IngredienteRules.PrecoAlterado(variacao, agora.AddDays(-5), agora));
            Assert.False(IngredienteRules.PrecoAlterado(variacao, agora.AddDays(-45), agora));
            Assert.False(IngredienteRules.PrecoAlterado(null, agora, agora));
        }

        [Fact]
        public void SimilarNames_AvisaIgualOuContidoSemAcento()
        {
            var existentes = new[] { "Queijo Muçarela", "Queijo", "Bacon", "Mussarela de búfala" };
            var parecidos = IngredienteRules.SimilarNames("queijo mucarela", existentes);
            Assert.Contains("Queijo Muçarela", parecidos);
            Assert.Contains("Queijo", parecidos);
            Assert.DoesNotContain("Bacon", parecidos);
            Assert.Empty(IngredienteRules.SimilarNames("Sal", new[] { "Salmão" }));
        }

        [Theory]
        [InlineData("sem_custo", "sem_custo")]
        [InlineData(" PRECO_ALTERADO ", "preco_alterado")]
        [InlineData("qualquer", "todos")]
        [InlineData(null, "todos")]
        public void NormalizeFilter_AceitaSoOsFiltrosConhecidos(string? entrada, string esperado) =>
            Assert.Equal(esperado, IngredienteRules.NormalizeFilter(entrada));
    }

    public sealed class IngredienteServiceTests
    {
        private sealed class Fake : IIngredienteRepository
        {
            public readonly Dictionary<Guid, (IngredienteInput Input, bool Ativo, int Versao)> Rows = new();
            public readonly List<CustoCalculado> Custos = new();

            public Task<IngredienteListaDto> ListAsync(Guid e, string filtro, string? busca, int page, int pageSize) => Task.FromResult(new IngredienteListaDto());
            public Task<IngredienteDto?> GetAsync(Guid e, Guid id) => Task.FromResult(Rows.TryGetValue(id, out var row)
                ? new IngredienteDto { Id = id, Nome = row.Input.Nome, Unidade = row.Input.Unidade, Ativo = row.Ativo, Versao = row.Versao,
                    CustoAtual = Custos.Count == 0 ? null : new IngredienteCustoDto { CustoBase = Custos[^1].CustoBase } }
                : null);
            public Task<IReadOnlyList<string>> NamesAsync(Guid e, Guid? exceto) =>
                Task.FromResult<IReadOnlyList<string>>(Rows.Where(r => r.Key != exceto).Select(r => r.Value.Input.Nome).ToList());
            public Task<Guid> CreateAsync(Guid e, int user, IngredienteInput input, CustoCalculado? custo)
            {
                var id = Guid.NewGuid(); Rows[id] = (input, true, 1);
                if (custo != null) Custos.Add(custo);
                return Task.FromResult(id);
            }
            public Task<bool> UpdateAsync(Guid e, Guid id, IngredienteInput input)
            {
                if (!Rows.TryGetValue(id, out var row)) return Task.FromResult(false);
                if (row.Versao != input.Versao) throw new DeliveryDomainException(409, "INGREDIENTE_CONFLITO", "conflito");
                Rows[id] = (input, row.Ativo, row.Versao + 1);
                return Task.FromResult(true);
            }
            public Task<Guid?> AddCustoAsync(Guid e, int user, Guid id, CustoCalculado custo) { Custos.Add(custo); return Task.FromResult<Guid?>(Guid.NewGuid()); }
            public Task<IngredienteCustosDto?> ListCustosAsync(Guid e, Guid id, int page, int pageSize) => Task.FromResult<IngredienteCustosDto?>(new IngredienteCustosDto());
            public Task<bool> SetAtivoAsync(Guid e, Guid id, bool ativo)
            {
                if (!Rows.TryGetValue(id, out var row)) return Task.FromResult(false);
                Rows[id] = (row.Input, ativo, row.Versao + 1);
                return Task.FromResult(true);
            }
        }

        private static readonly Guid Loja = Guid.NewGuid();

        [Fact]
        public async Task Create_SemCustoFicaSemCustoENuncaZero()
        {
            var service = new IngredienteService(new Fake());
            var salvo = await service.CreateAsync(Loja, 1, new IngredienteRequest { Nome = "Bacon", Unidade = "g" });
            Assert.Null(salvo.Ingrediente.CustoAtual);
            Assert.Empty(salvo.Avisos);
        }

        [Fact]
        public async Task Create_ComCompraConverteEAvisaNomeParecido()
        {
            var service = new IngredienteService(new Fake());
            await service.CreateAsync(Loja, 1, new IngredienteRequest { Nome = "Queijo muçarela", Unidade = "g" });
            var salvo = await service.CreateAsync(Loja, 1, new IngredienteRequest
            { Nome = "Queijo mucarela fatiado", Unidade = "g", Custo = new CustoRequest { QuantidadeCompra = 2, UnidadeCompra = "kg", ValorCompra = 60 } });
            Assert.Equal(0.03m, salvo.Ingrediente.CustoAtual!.CustoBase);
            Assert.Single(salvo.Avisos);
        }

        [Fact]
        public async Task Update_NaoTrocaUnidadeBaseEExigeVersaoAtual()
        {
            var service = new IngredienteService(new Fake());
            var criado = (await service.CreateAsync(Loja, 1, new IngredienteRequest { Nome = "Leite", Unidade = "ml" })).Ingrediente;
            var erroUnidade = await Assert.ThrowsAsync<DeliveryDomainException>(() =>
                service.UpdateAsync(Loja, criado.Id, new IngredienteRequest { Nome = "Leite", Unidade = "g", Versao = criado.Versao }));
            Assert.Equal("INGREDIENTE_UNIDADE_FIXA", erroUnidade.Code);
            var erroVersao = await Assert.ThrowsAsync<DeliveryDomainException>(() =>
                service.UpdateAsync(Loja, criado.Id, new IngredienteRequest { Nome = "Leite integral", Unidade = "ml", Versao = 99 }));
            Assert.Equal(409, erroVersao.StatusCode);
            var ok = await service.UpdateAsync(Loja, criado.Id, new IngredienteRequest { Nome = "Leite integral", Unidade = "ml", Versao = criado.Versao });
            Assert.Equal("Leite integral", ok.Ingrediente.Nome);
        }

        [Fact]
        public async Task AddCusto_RecusaIngredienteInativoEUnidadeIncompativel()
        {
            var service = new IngredienteService(new Fake());
            var criado = (await service.CreateAsync(Loja, 1, new IngredienteRequest { Nome = "Farinha", Unidade = "g" })).Ingrediente;
            await Assert.ThrowsAsync<DeliveryDomainException>(() => service.AddCustoAsync(Loja, 1, criado.Id, new CustoRequest { QuantidadeCompra = 1, UnidadeCompra = "l", ValorCompra = 5 }));
            var comCusto = await service.AddCustoAsync(Loja, 1, criado.Id, new CustoRequest { QuantidadeCompra = 1, UnidadeCompra = "kg", ValorCompra = 5 });
            Assert.Equal(0.005m, comCusto.CustoAtual!.CustoBase);
            await service.SetAtivoAsync(Loja, criado.Id, false);
            var inativo = await Assert.ThrowsAsync<DeliveryDomainException>(() => service.AddCustoAsync(Loja, 1, criado.Id, new CustoRequest { QuantidadeCompra = 1, UnidadeCompra = "kg", ValorCompra = 6 }));
            Assert.Equal("INGREDIENTE_INATIVO", inativo.Code);
        }

        [Fact]
        public async Task OutraLoja_NaoEncontra()
        {
            var fake = new Fake();
            var service = new IngredienteService(fake);
            var erro = await Assert.ThrowsAsync<DeliveryDomainException>(() => service.GetAsync(Loja, Guid.NewGuid()));
            Assert.Equal(404, erro.StatusCode);
            Assert.False((await service.UsosAsync(Loja, (await service.CreateAsync(Loja, 1, new IngredienteRequest { Nome = "Sal", Unidade = "g" })).Ingrediente.Id)).ReceitasDisponiveis);
        }
    }

    public sealed class IngredienteMigrationContractTests
    {
        private static string Ler(params string[] parts)
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "APIBack.csproj"))) dir = Directory.GetParent(dir)?.FullName;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir! }.Concat(parts).ToArray()));
        }

        [Fact]
        public void Migracao_EAditivaEImpoeCustoDesconhecidoDiferenteDeZero()
        {
            var sql = Ler("Migrations", "Inteligencia", "20261019_01_ingredientes.sql");
            Assert.Contains("CREATE TABLE IF NOT EXISTS insumo ", sql, StringComparison.Ordinal);
            Assert.Contains("CREATE TABLE IF NOT EXISTS insumo_custo ", sql, StringComparison.Ordinal);
            Assert.Contains("unidade IN ('g', 'ml', 'un')", sql, StringComparison.Ordinal);
            Assert.Contains("custo_base > 0 OR (gratuito AND", sql, StringComparison.Ordinal);
            Assert.Contains("FOREIGN KEY (estabelecimento_id, insumo_id) REFERENCES insumo (estabelecimento_id, id)", sql, StringComparison.Ordinal);
            foreach (var destrutivo in new[] { "DROP TABLE", "DROP COLUMN", "TRUNCATE", "DELETE FROM" })
                Assert.DoesNotContain(destrutivo, sql, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Rollback_ExisteEApagaSoAsTabelasNovas()
        {
            var sql = Ler("Migrations", "Inteligencia", "rollback", "20261019_01_ingredientes_rollback.sql");
            Assert.Contains("DROP TABLE IF EXISTS insumo_custo", sql, StringComparison.Ordinal);
            Assert.Contains("DROP TABLE IF EXISTS insumo;", sql, StringComparison.Ordinal);
        }
    }
}
