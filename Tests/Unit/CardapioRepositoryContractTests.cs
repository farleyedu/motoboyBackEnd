using System;
using APIBack.Repository;
using Xunit;

namespace APIBack.Tests.Unit
{
    public sealed class CardapioRepositoryContractTests
    {
        [Fact]
        public void ModuloAtivoSql_CastsUnnestedValueInsteadOfTableFunction()
        {
            var sql = CardapioRepository.EstabelecimentoTemModuloAtivoSql;

            Assert.Contains(
                "unnest(e.modulos_ativos) AS modulo_linha(modulo_ativo)",
                sql,
                StringComparison.Ordinal);
            Assert.Contains("lower(modulo_ativo::text)", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("unnest(e.modulos_ativos)::text", sql, StringComparison.Ordinal);
        }
    }
}
