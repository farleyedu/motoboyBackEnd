using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace APIBack.Tests.Unit
{
    /// <summary>
    /// O seed de demonstracao roda na subida da API em qualquer ambiente. Estes testes travam as
    /// garantias que o tornam seguro (sem Postgres aqui, so conteudo do arquivo).
    /// </summary>
    public class SeedUberlandiaMigrationTests
    {
        private const string SeedFile = "20260927_90_seed_uberlandia.sql";

        private static string Dir()
        {
            var dir = AppContext.BaseDirectory;
            // Fonte (nao a copia do bin): a pasta rollback/ nao e copiada para a saida.
            while (dir != null && !File.Exists(Path.Combine(dir, "APIBack.csproj")))
            {
                dir = Path.GetDirectoryName(dir);
            }
            return Path.Combine(dir ?? string.Empty, "Migrations", "Delivery");
        }

        private static string Seed() => File.ReadAllText(Path.Combine(Dir(), SeedFile));

        [Fact]
        public void Seed_never_creates_establishment_company_or_user()
        {
            var sql = Seed();

            Assert.DoesNotMatch(new Regex(@"INSERT\s+INTO\s+(estabelecimentos|empresas|usuario\w*)\b", RegexOptions.IgnoreCase), sql);
        }

        [Fact]
        public void Seed_only_targets_active_delivery_establishment_without_real_data()
        {
            var sql = Seed();

            Assert.Contains("e.ativo = TRUE", sql);
            Assert.Contains("'DELIVERY' = ANY", sql);
            Assert.Contains("pg_temp.seed_alvo_ok(e.id)", sql);
            Assert.Contains("NOT pg_temp.seed_alvo_ok(v_est)", sql);
        }

        [Fact]
        public void Seed_has_opt_out_target_override_and_records_outcome()
        {
            var sql = Seed();

            Assert.Contains("seed_demo_desligado", sql);
            Assert.Contains("seed_demo_alvo:", sql);
            Assert.Contains("seed_demo_sem_alvo", sql);
            Assert.Contains("seed_demo_erro:", sql);
        }

        [Fact]
        public void Seed_failure_is_isolated_so_later_migrations_still_run()
        {
            var sql = Seed();

            Assert.Contains("EXCEPTION WHEN OTHERS THEN", sql);
            // O ledger do arquivo so e gravado no caminho de sucesso ou de "nada a fazer".
            var errorHandler = sql[sql.IndexOf("EXCEPTION WHEN OTHERS THEN", StringComparison.Ordinal)..];
            Assert.DoesNotContain("'20260927_90_seed_uberlandia'", errorHandler[..errorHandler.IndexOf("END $seed$", StringComparison.Ordinal)]);
        }

        [Fact]
        public void Seed_is_idempotent_by_construction()
        {
            var sql = Seed();
            var inserts = Regex.Matches(sql, @"^\s*INSERT INTO (cardapio_\w+|clientes|conversas|mensagens|motoboy\w*) ", RegexOptions.Multiline).Count;
            var guards = Regex.Matches(sql, @"ON CONFLICT|WHERE NOT EXISTS", RegexOptions.IgnoreCase).Count;

            Assert.True(inserts > 50);
            Assert.True(guards >= inserts, $"{guards} protecoes para {inserts} inserts");
        }

        [Fact]
        public void Seed_runs_after_the_schema_it_depends_on_and_verify_is_not_auto_applied()
        {
            var names = Directory.GetFiles(Dir(), "*.sql").Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToList();
            var seed = names.IndexOf(SeedFile);

            Assert.True(seed > names.IndexOf("20260925_05_constraints.sql"));
            Assert.True(seed > names.IndexOf("20260926_01_produto_atendimento.sql"));
            // O runner so ignora arquivos terminados em _verify.sql.
            Assert.EndsWith("_verify.sql", "20260927_91_seed_verify.sql");
            Assert.True(File.Exists(Path.Combine(Dir(), "20260927_91_seed_verify.sql")));
        }

        [Fact]
        public void Seed_data_is_from_Uberlandia_and_fictitious()
        {
            var sql = Seed();

            Assert.Contains("Uberlandia", sql);
            Assert.Contains("\"entrega_estado\":\"MG\"", sql);
            Assert.DoesNotMatch(new Regex(@"\+55(?!3499123)\d{10,11}"), sql);
        }

        [Fact]
        public void Rollback_exists()
        {
            Assert.True(File.Exists(Path.Combine(Dir(), "rollback", "20260927_90_seed_uberlandia.down.sql")));
        }
    }
}
