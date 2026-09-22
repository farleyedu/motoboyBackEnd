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

        [Fact]
        public void Part2Schema_HasRouteStopsWithActivePositionUniqueness()
        {
            var sql = ReadMigration("20260723_02_schema.sql");

            Assert.Contains("delivery_route_stops", sql, StringComparison.Ordinal);
            Assert.Contains("delivery_motoboy_route", sql, StringComparison.Ordinal);
            Assert.Contains("stop_status", sql, StringComparison.Ordinal);
        }

        [Fact]
        public void Part2Backfill_OnlyFillsUnambiguousLinksAndNeverDeletesPedido()
        {
            var sql = ReadMigration("20260723_03_backfill.sql");

            Assert.Contains("COUNT(DISTINCT estabelecimento_id) = 1", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("DELETE FROM pedido", sql, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Part2Constraints_EnforceOneEnRoutePerMotoboyAndPedidoIfoodUniqueness()
        {
            var sql = ReadMigration("20260723_04_constraints.sql");

            Assert.Contains("ux_delivery_route_stop_en_route_per_motoboy", sql, StringComparison.Ordinal);
            Assert.Contains("ux_delivery_route_stop_pedido_active", sql, StringComparison.Ordinal);
            Assert.Contains("ux_pedido_id_ifood", sql, StringComparison.Ordinal);
        }

        [Fact]
        public void Part3Schema_AddsTransferSettingsAndIsolatesQueuePerEstablishment()
        {
            var sql = ReadMigration("20260922_02_schema.sql");

            Assert.Contains("CREATE TABLE IF NOT EXISTS delivery_settings", sql, StringComparison.Ordinal);
            Assert.Contains("CREATE TABLE IF NOT EXISTS delivery_transfer_requests", sql, StringComparison.Ordinal);
            Assert.Contains("PRIMARY KEY (motoboy_id, estabelecimento_id)", sql, StringComparison.Ordinal);
            Assert.Contains("ADD COLUMN IF NOT EXISTS picked_up_at_utc", sql, StringComparison.Ordinal);
            Assert.Contains("'20260922_02_schema'", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("DELETE FROM", sql, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Part3Constraints_AllowNewStopStatesAndOnePendingTransferPerPedido()
        {
            var sql = ReadMigration("20260922_04_constraints.sql");

            Assert.Contains("'failed', 'refused', 'transferred'", sql, StringComparison.Ordinal);
            Assert.Contains("ux_delivery_route_stop_position_active_v2", sql, StringComparison.Ordinal);
            Assert.Contains("(motoboy_id, estabelecimento_id, position)", sql, StringComparison.Ordinal);
            Assert.Contains("ux_delivery_transfer_pending_per_pedido", sql, StringComparison.Ordinal);
            Assert.Contains("CHECK (transfer_policy IN ('direct', 'establishment_approval'))", sql, StringComparison.Ordinal);
            Assert.Contains("'20260922_04_constraints'", sql, StringComparison.Ordinal);
        }

        private static string ReadMigration(string fileName)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", fileName);
            Assert.True(File.Exists(path), $"Migration ausente no output de teste: {path}");
            return File.ReadAllText(path);
        }
    }
}

