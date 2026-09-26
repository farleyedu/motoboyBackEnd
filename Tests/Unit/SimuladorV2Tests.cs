using System;
using System.Text.Json;
using APIBack.DTOs.Clientes;
using APIBack.DTOs.Simulador;
using APIBack.Service;
using Xunit;

namespace APIBack.Tests.Unit
{
    public sealed class SimuladorEventoRulesTests
    {
        [Fact]
        public void Validate_NormalizesAndDefaults()
        {
            var input = SimuladorEventoRules.Validate(new SimEventoRequest { Entidade = " Pedido ", Tipo = "Pedido_Criado", Titulo = "  Pedido criado " });
            Assert.Equal("pedido", input.Entidade);
            Assert.Equal("pedido_criado", input.Tipo);
            Assert.Equal("Pedido criado", input.Titulo);
            Assert.Equal("sucesso", input.Status);
            Assert.Null(input.DadosJson);
        }

        [Fact]
        public void Validate_RejectsUnknownEntityStatusAndMissingText()
        {
            Assert.Throws<DeliveryDomainException>(() => SimuladorEventoRules.Validate(null));
            Assert.Throws<DeliveryDomainException>(() => SimuladorEventoRules.Validate(new SimEventoRequest { Entidade = "planeta", Tipo = "x", Titulo = "y" }));
            Assert.Throws<DeliveryDomainException>(() => SimuladorEventoRules.Validate(new SimEventoRequest { Tipo = "x", Titulo = "y", Status = "otimo" }));
            Assert.Throws<DeliveryDomainException>(() => SimuladorEventoRules.Validate(new SimEventoRequest { Titulo = "y" }));
            Assert.Throws<DeliveryDomainException>(() => SimuladorEventoRules.Validate(new SimEventoRequest { Tipo = "x" }));
            Assert.Throws<DeliveryDomainException>(() => SimuladorEventoRules.Validate(new SimEventoRequest { Tipo = "x", Titulo = new string('a', 161) }));
        }

        [Fact]
        public void Validate_KeepsJsonData_ButCapsItsSize()
        {
            using var small = JsonDocument.Parse("{\"km\":4.2}");
            var input = SimuladorEventoRules.Validate(new SimEventoRequest { Tipo = "x", Titulo = "y", Dados = small.RootElement.Clone() });
            Assert.Equal("{\"km\":4.2}", input.DadosJson);

            using var big = JsonDocument.Parse("\"" + new string('a', 4100) + "\"");
            Assert.Throws<DeliveryDomainException>(() =>
                SimuladorEventoRules.Validate(new SimEventoRequest { Tipo = "x", Titulo = "y", Dados = big.RootElement.Clone() }));
        }

        [Fact]
        public void SessaoTipo_And_Estado()
        {
            Assert.Equal("cliente", SimuladorEventoRules.ValidateSessaoTipo(" Cliente "));
            Assert.Throws<DeliveryDomainException>(() => SimuladorEventoRules.ValidateSessaoTipo("nave"));

            using var obj = JsonDocument.Parse("{\"a\":1}");
            Assert.Equal("{\"a\":1}", SimuladorEventoRules.SessaoEstado(obj.RootElement.Clone()));
            using var array = JsonDocument.Parse("[1]");
            Assert.Equal("{}", SimuladorEventoRules.SessaoEstado(array.RootElement.Clone())); // so objeto vale
            Assert.Equal("{}", SimuladorEventoRules.SessaoEstado(null));
        }
    }

    public sealed class ClienteRulesExtendedTests
    {
        private static ClienteRequest Valid() => new() { Nome = "Maria", Telefone = "(34) 99999-1234" };

        [Theory]
        [InlineData("529.982.247-25", "52998224725")]
        [InlineData("", null)]
        [InlineData(null, null)]
        public void Cpf_AcceptsValidDigitsOnly(string? raw, string? expected) => Assert.Equal(expected, ClienteRules.Cpf(raw));

        [Theory]
        [InlineData("111.111.111-11")]
        [InlineData("123.456.789-00")]
        [InlineData("123")]
        public void Cpf_RejectsInvalid(string raw) => Assert.Throws<DeliveryDomainException>(() => ClienteRules.Cpf(raw));

        [Fact]
        public void Birth_MustBeARealPastDate()
        {
            Assert.Equal("1990-05-17", ClienteRules.Birth("1990-05-17"));
            Assert.Null(ClienteRules.Birth(" "));
            Assert.Throws<DeliveryDomainException>(() => ClienteRules.Birth("17/05/1990"));
            Assert.Throws<DeliveryDomainException>(() => ClienteRules.Birth("2999-01-01"));
            Assert.Throws<DeliveryDomainException>(() => ClienteRules.Birth("1800-01-01"));
        }

        [Fact]
        public void Tags_TrimDedupeAndCap()
        {
            Assert.Equal(new[] { "Cliente VIP", "Teste" }, ClienteRules.Tags(new[] { " Cliente VIP ", "cliente vip", "Teste", "" }));
            Assert.Empty(ClienteRules.Tags(null));
            Assert.Throws<DeliveryDomainException>(() => ClienteRules.Tags(new[] { new string('a', 31) }));
            Assert.Throws<DeliveryDomainException>(() => ClienteRules.Tags(new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", "10", "11" }));
        }

        [Fact]
        public void Validate_CarriesTheNewFields_WithSafeDefaults()
        {
            var request = Valid();
            request.Cpf = "52998224725";
            request.DataNascimento = "1990-05-17";
            request.CanalPreferido = "WhatsApp";
            request.Origem = "Simulacao interna";
            request.Tags = new[] { "VIP" };
            request.Referencia = "Perto do metro";
            var input = ClienteRules.Validate(request);

            Assert.Equal("52998224725", input.Cpf);
            Assert.Equal("1990-05-17", input.DataNascimento);
            Assert.Equal("whatsapp", input.CanalPreferido);
            Assert.Equal("Simulacao interna", input.Origem);
            Assert.Equal(new[] { "VIP" }, input.Tags);
            Assert.True(input.ConsentimentoWhatsapp); // padrao: autorizado
            Assert.True(input.Ativo);                 // padrao: ativo

            request.ConsentimentoWhatsapp = false;
            request.Ativo = false;
            var off = ClienteRules.Validate(request);
            Assert.False(off.ConsentimentoWhatsapp);
            Assert.False(off.Ativo);
        }

        [Fact]
        public void Validate_RejectsUnknownChannel() =>
            Assert.Throws<DeliveryDomainException>(() => ClienteRules.Validate(new ClienteRequest { Nome = "A", Telefone = "34999991234", CanalPreferido = "pombo" }));
    }

    public sealed class SimuladorV2MigrationContractTests
    {
        private static string Read(string file) => System.IO.File.ReadAllText(
            System.IO.Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", file));

        [Fact]
        public void Migration_IsAdditiveAndIdempotent()
        {
            var sql = Read("20260930_01_simulador_v2.sql");
            Assert.Contains("CREATE TABLE IF NOT EXISTS simulador_evento", sql, StringComparison.Ordinal);
            Assert.Contains("CREATE TABLE IF NOT EXISTS simulador_sessao", sql, StringComparison.Ordinal);
            Assert.Contains("ADD COLUMN IF NOT EXISTS confirmado_em_utc", sql, StringComparison.Ordinal);
            Assert.Contains("ADD COLUMN IF NOT EXISTS preparo_em_utc", sql, StringComparison.Ordinal);
            Assert.Contains("ADD COLUMN IF NOT EXISTS canal ", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("DROP ", sql, StringComparison.OrdinalIgnoreCase);
            // As etapas de preparo NAO viram valor do status do pedido: fila, rota e motoboy dependem dele.
            Assert.DoesNotContain("status_pedido", sql, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void EventEntities_MatchTheRulesAcceptedByTheApi()
        {
            var sql = Read("20260930_01_simulador_v2.sql");
            foreach (var entidade in SimuladorEventoRules.Entidades)
            {
                Assert.Contains($"'{entidade}'", sql, StringComparison.Ordinal);
            }
            foreach (var tipo in SimuladorEventoRules.SessaoTipos)
            {
                Assert.Contains($"'{tipo}'", sql, StringComparison.Ordinal);
            }
        }
    }
}
