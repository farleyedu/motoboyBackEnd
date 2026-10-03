using System;
using System.IO;
using Xunit;

namespace APIBack.Tests.Unit
{
    public sealed class AtendimentoMigrationContractTests
    {
        [Fact]
        public void CatalogoECanais_ImpoemUnicidadeDoNumeroEDoIdDaMetaSemTocarEmWabaPhone()
        {
            var sql = Ler("20261002_07_catalogo_servicos_canais.sql");

            foreach (var tabela in new[]
            {
                "servico_catalogo", "tipo_servico", "estabelecimento_servico", "canal_whatsapp", "canal_servico",
                "canal_whatsapp_auditoria", "canal_whatsapp_migracao_pendencia"
            })
            {
                Assert.Contains($"CREATE TABLE IF NOT EXISTS {tabela} ", sql, StringComparison.Ordinal);
            }

            Assert.Contains("CREATE UNIQUE INDEX IF NOT EXISTS ux_canal_whatsapp_phone_number_id", sql, StringComparison.Ordinal);
            Assert.Contains("CREATE UNIQUE INDEX IF NOT EXISTS ux_canal_whatsapp_numero", sql, StringComparison.Ordinal);
            // ON COMMIT DROP so aparece nas tabelas temporarias do backfill; nenhuma tabela, coluna ou indice real e apagado.
            foreach (var destrutivo in new[] { "DROP TABLE", "DROP COLUMN", "DROP INDEX", "TRUNCATE", "DELETE FROM" })
            {
                Assert.DoesNotContain(destrutivo, sql, StringComparison.OrdinalIgnoreCase);
            }
            Assert.DoesNotContain("ALTER TABLE waba_phone", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("UPDATE waba_phone", sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("'20261002_07_catalogo_servicos_canais'", sql, StringComparison.Ordinal);
        }

        [Fact]
        public void Backfill_RegistraPendenciasEmVezDeDescartarLinhasDeWabaPhone()
        {
            var sql = Ler("20261002_07_catalogo_servicos_canais.sql");

            Assert.Contains("sem_numero_de_exibicao", sql, StringComparison.Ordinal);
            Assert.Contains("numero_duplicado_em_outra_loja", sql, StringComparison.Ordinal);
            Assert.Contains("to_regclass('public.waba_phone')", sql, StringComparison.Ordinal);
        }

        [Fact]
        public void FilaDeEventos_ImpoeIdempotenciaPorTipoEChaveEIndiceParcialDaFila()
        {
            var sql = Ler("20261002_09_wa_evento.sql");

            Assert.Contains("CREATE TABLE IF NOT EXISTS wa_evento ", sql, StringComparison.Ordinal);
            Assert.Contains("CONSTRAINT ux_wa_evento_tipo_chave UNIQUE (tipo, chave)", sql, StringComparison.Ordinal);
            Assert.Contains("WHERE estado IN ('pendente', 'processando')", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("DROP TABLE", sql, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ConversasGanhamCanalEEstadoDoFluxoSemApagarNada()
        {
            var canal = Ler("20261002_11_conversas_canal.sql");
            var fluxo = Ler("20261002_13_conversas_fluxo.sql");

            Assert.Contains("ADD COLUMN IF NOT EXISTS id_canal UUID NULL REFERENCES canal_whatsapp (id) ON DELETE SET NULL", canal, StringComparison.Ordinal);
            foreach (var coluna in new[] { "fluxo_estado JSONB NULL", "fluxo_chave TEXT NULL", "fluxo_versao INTEGER NOT NULL DEFAULT 1" })
            {
                Assert.Contains($"ADD COLUMN IF NOT EXISTS {coluna}", fluxo, StringComparison.Ordinal);
            }

            Assert.DoesNotContain("DROP ", canal + fluxo, StringComparison.OrdinalIgnoreCase);
        }

        private static string Ler(string arquivo)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", arquivo);
            Assert.True(File.Exists(path), $"Migration ausente no output de teste: {path}");
            return File.ReadAllText(path);
        }
    }
}
