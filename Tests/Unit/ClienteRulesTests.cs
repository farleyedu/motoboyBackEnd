using System;
using APIBack.DTOs.Clientes;
using APIBack.Service;
using Xunit;

namespace APIBack.Tests.Unit
{
    public sealed class ClienteRulesTests
    {
        private static ClienteRequest Valid() => new() { Nome = "  Maria Silva ", Telefone = "(34) 99999-1234" };

        [Theory]
        [InlineData("(34) 99999-1234", "34999991234")]
        [InlineData("+55 34 99999-1234", "34999991234")]
        [InlineData("5534999991234", "34999991234")]
        [InlineData("3432221234", "3432221234")]
        [InlineData("", null)]
        [InlineData(null, null)]
        public void NationalDigits_DropsMaskAndCountryCode(string? raw, string? expected) =>
            Assert.Equal(expected, ClienteRules.NationalDigits(raw));

        [Fact]
        public void Validate_TrimsAndNormalizes()
        {
            var request = Valid();
            request.Email = " Maria@Exemplo.COM ";
            request.Cep = "38408100";
            request.Uf = "mg";
            var input = ClienteRules.Validate(request);

            Assert.Equal("Maria Silva", input.Nome);
            // Mesma chave que o webhook do WhatsApp grava (+55 + DDD + numero).
            Assert.Equal("+5534999991234", input.TelefoneE164);
            Assert.Equal("maria@exemplo.com", input.Email);
            Assert.Equal("38408-100", input.Cep);
            Assert.Equal("MG", input.Uf);
        }

        [Fact]
        public void Validate_PhoneIsRequired_AndMustBeReal()
        {
            Assert.Throws<DeliveryDomainException>(() => ClienteRules.Validate(new ClienteRequest { Nome = "Sem telefone" }));
            Assert.Throws<DeliveryDomainException>(() => ClienteRules.Validate(new ClienteRequest { Nome = "A", Telefone = "123" }));
        }

        [Fact]
        public void Validate_E164_MatchesWhatsappWebhookKey()
        {
            // O webhook recebe wa_id sem '+': o cadastro tem que gerar exatamente a mesma chave.
            var fromWebhook = APIBack.Automation.Helpers.TelefoneHelper.ToE164("5534999991234");
            Assert.Equal(fromWebhook, ClienteRules.Validate(new ClienteRequest { Nome = "A", Telefone = "(34) 99999-1234" }).TelefoneE164);
        }

        [Fact]
        public void Validate_RejectsMissingNameBadEmailBadCepBadUf()
        {
            Assert.Throws<DeliveryDomainException>(() => ClienteRules.Validate(null));
            Assert.Throws<DeliveryDomainException>(() => ClienteRules.Validate(new ClienteRequest { Nome = "  " }));
            var email = Valid(); email.Email = "sem-arroba";
            Assert.Throws<DeliveryDomainException>(() => ClienteRules.Validate(email));
            var cep = Valid(); cep.Cep = "123";
            Assert.Throws<DeliveryDomainException>(() => ClienteRules.Validate(cep));
            var uf = Valid(); uf.Uf = "M1";
            Assert.Throws<DeliveryDomainException>(() => ClienteRules.Validate(uf));
        }

        [Fact]
        public void Validate_LocationMustComeCompleteAndReal()
        {
            var half = Valid(); half.Latitude = -18.9;
            Assert.Throws<DeliveryDomainException>(() => ClienteRules.Validate(half));
            var zero = Valid(); zero.Latitude = 0; zero.Longitude = 0;
            Assert.Throws<DeliveryDomainException>(() => ClienteRules.Validate(zero));
            var far = Valid(); far.Latitude = 91; far.Longitude = 10;
            Assert.Throws<DeliveryDomainException>(() => ClienteRules.Validate(far));

            var ok = Valid(); ok.Latitude = -18.9186; ok.Longitude = -48.2772;
            Assert.Equal(-18.9186, ClienteRules.Validate(ok).Latitude);
        }

        [Theory]
        [InlineData(0, 0, 1, 25)]
        [InlineData(-3, 500, 1, 100)]
        [InlineData(4, 10, 4, 10)]
        public void ClampPaging_NeverTrustsTheClient(int page, int size, int expectedPage, int expectedSize) =>
            Assert.Equal((expectedPage, expectedSize), ClienteRules.ClampPaging(page, size));
    }

    public sealed class ClienteMigrationContractTests
    {
        private static string Read(string file) => System.IO.File.ReadAllText(
            System.IO.Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", file));

        [Fact]
        public void ModuleEnum_IsAloneInItsFile_AndBackfillIsSeparate()
        {
            var enumSql = Read("20260929_01_modulo_clientes.sql");
            Assert.Contains("ALTER TYPE modulo_enum ADD VALUE IF NOT EXISTS 'CLIENTES'", enumSql, StringComparison.Ordinal);
            Assert.DoesNotContain("UPDATE ", enumSql, StringComparison.OrdinalIgnoreCase);

            var backfill = Read("20260929_03_modulo_clientes_backfill.sql");
            Assert.Contains("'CLIENTES'::modulo_enum", backfill, StringComparison.Ordinal);
            Assert.DoesNotContain("DELETE ", backfill, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Schema_ExtendsExistingClientesTable_Additively()
        {
            var sql = Read("20260929_02_clientes_cadastro.sql");
            Assert.Contains("ALTER TABLE clientes", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("CREATE TABLE", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("UNIQUE", sql, StringComparison.OrdinalIgnoreCase); // linhas antigas do WhatsApp podem repetir
            Assert.DoesNotContain("DROP ", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ALTER TABLE pedido", sql, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ModuleMapper_KnowsClientes()
        {
            var db = APIBack.Model.Gestao.EstabelecimentoModuleMapper.ToDatabaseModules(new[] { "Clientes" }, null);
            Assert.Contains("CLIENTES", db);
        }
    }
}
