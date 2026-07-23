using System;
using System.IO;
using Xunit;

namespace APIBack.Tests.Unit
{
    public sealed class DeliveryMigrationContractTests
    {
        [Fact]
        public void ConstraintsMigration_EnforcesGlobalSessionAndPerSessionSequence()
        {
            var sql = ReadMigration("20260722_04_constraints.sql");

            Assert.Contains("ux_motoboy_one_open_session", sql, StringComparison.Ordinal);
            Assert.Contains("WHERE ended_at_utc IS NULL AND revoked_at IS NULL", sql, StringComparison.Ordinal);
            Assert.Contains("ck_motoboy_location_sequence", sql, StringComparison.Ordinal);
        }

        [Fact]
        public void Backfill_PreservesAliasesAndAuditsDuplicateSessions()
        {
            var sql = ReadMigration("20260722_03_backfill.sql");

            Assert.Contains("canonical_motoboy_id", sql, StringComparison.Ordinal);
            Assert.Contains("migration_duplicate", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("DELETE FROM motoboy", sql, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Schema_HasOutboxAndServerReceivedTimestamp()
        {
            var sql = ReadMigration("20260722_02_schema.sql");

            Assert.Contains("delivery_realtime_outbox", sql, StringComparison.Ordinal);
            Assert.Contains("received_at_utc", sql, StringComparison.Ordinal);
            Assert.Contains("UNIQUE (session_id, sequence)", sql, StringComparison.Ordinal);
        }

        private static string ReadMigration(string fileName)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", fileName);
            Assert.True(File.Exists(path), $"Migration ausente no output de teste: {path}");
            return File.ReadAllText(path);
        }
    }
}

