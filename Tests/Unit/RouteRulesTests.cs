using System;
using System.IO;
using System.Linq;
using APIBack.Service;
using Xunit;

namespace APIBack.Tests.Unit
{
    public class RouteLockRulesTests
    {
        private static RouteSlot[] Queue(params (int Id, bool Locked, bool EnRoute)[] slots) =>
            slots.Select(s => new RouteSlot(s.Id, s.Locked, s.EnRoute)).ToArray();

        [Fact]
        public void Free_queue_accepts_any_order()
        {
            var current = Queue((101, false, false), (102, false, false), (103, false, false));

            RouteLockRules.ValidateReorder(current, new[] { 103, 101, 102 });
        }

        [Fact]
        public void Anchor_in_the_middle_keeps_its_position_while_others_swap()
        {
            // Fila [101, 102*, 103, 104]: troca 101 <-> 103 e permitida, 102 continua na posicao 2.
            var current = Queue((101, false, false), (102, true, false), (103, false, false), (104, false, false));

            RouteLockRules.ValidateReorder(current, new[] { 103, 102, 101, 104 });
            RouteLockRules.ValidateReorder(current, new[] { 104, 102, 103, 101 });
        }

        [Fact]
        public void Moving_the_anchor_is_refused_with_ORDER_LOCKED()
        {
            var current = Queue((101, false, false), (102, true, false), (103, false, false), (104, false, false));

            var error = Assert.Throws<DeliveryDomainException>(() =>
                RouteLockRules.ValidateReorder(current, new[] { 102, 101, 103, 104 }));

            Assert.Equal("ORDER_LOCKED", error.Code);
            Assert.Equal(409, error.StatusCode);
        }

        [Fact]
        public void Pushing_the_anchor_by_moving_a_neighbour_is_also_refused()
        {
            // Mover o 104 para a frente empurraria o 102 para a posicao 3.
            var current = Queue((101, false, false), (102, true, false), (103, false, false), (104, false, false));

            Assert.Throws<DeliveryDomainException>(() =>
                RouteLockRules.ValidateReorder(current, new[] { 104, 101, 102, 103 }));
        }

        [Fact]
        public void Anchor_in_first_position_pins_the_head()
        {
            var current = Queue((101, true, false), (102, false, false), (103, false, false));

            RouteLockRules.ValidateReorder(current, new[] { 101, 103, 102 });
            Assert.Throws<DeliveryDomainException>(() => RouteLockRules.ValidateReorder(current, new[] { 102, 101, 103 }));
        }

        [Fact]
        public void Several_anchors_leave_only_the_free_slots_to_move()
        {
            var current = Queue((1, true, false), (2, false, false), (3, true, false), (4, false, false), (5, false, false));

            RouteLockRules.ValidateReorder(current, new[] { 1, 5, 3, 2, 4 });
            Assert.Throws<DeliveryDomainException>(() => RouteLockRules.ValidateReorder(current, new[] { 1, 2, 4, 3, 5 }));
        }

        [Fact]
        public void Current_delivery_locked_stays_first()
        {
            var current = Queue((10, true, true), (11, false, false), (12, true, false));

            RouteLockRules.ValidateReorder(current, new[] { 10, 11, 12 });
            Assert.Throws<DeliveryDomainException>(() => RouteLockRules.ValidateReorder(current, new[] { 11, 10, 12 }));
            Assert.Throws<DeliveryDomainException>(() => RouteLockRules.ValidateReorder(current, new[] { 10, 12, 11 }));
        }

        [Fact]
        public void Same_order_is_always_allowed_even_when_everything_is_locked()
        {
            var current = Queue((1, true, false), (2, true, false));

            RouteLockRules.ValidateReorder(current, new[] { 1, 2 });
        }

        [Fact]
        public void Only_assigned_or_en_route_stops_can_be_locked()
        {
            Assert.True(RouteLockRules.CanLock("assigned"));
            Assert.True(RouteLockRules.CanLock("en_route"));
            Assert.False(RouteLockRules.CanLock("completed"));
            Assert.False(RouteLockRules.CanLock("removed"));
        }

        [Fact]
        public void Motoboy_actions_on_a_locked_order_fail_with_ORDER_LOCKED()
        {
            RouteLockRules.EnsureNotLockedForMotoboy(false, 5, "recusado");

            var error = Assert.Throws<DeliveryDomainException>(() => RouteLockRules.EnsureNotLockedForMotoboy(true, 5, "recusado"));
            Assert.Equal("ORDER_LOCKED", error.Code);
        }

        [Fact]
        public void Lock_request_ids_are_validated_and_deduplicated()
        {
            Assert.Equal(new[] { 1, 2 }, RouteLockRules.NormalizeIds(new[] { 1, 2, 1 }));
            Assert.Throws<DeliveryDomainException>(() => RouteLockRules.NormalizeIds(null));
            Assert.Throws<DeliveryDomainException>(() => RouteLockRules.NormalizeIds(Array.Empty<int>()));
            Assert.Throws<DeliveryDomainException>(() => RouteLockRules.NormalizeIds(new[] { 1, 0 }));
            Assert.Throws<DeliveryDomainException>(() =>
                RouteLockRules.NormalizeIds(Enumerable.Range(1, RouteLockRules.MaxPedidosPerLockRequest + 1).ToArray()));
        }
    }

    public class ReturnToStoreRulesTests
    {
        [Theory]
        [InlineData(true, 0, true)]
        [InlineData(true, 1, false)]
        [InlineData(false, 0, false)]
        public void Route_returns_only_when_required_and_no_stop_is_left(bool require, int left, bool expected)
        {
            Assert.Equal(expected, ReturnToStoreRules.ShouldEnterReturning(require, left));
        }

        [Fact]
        public void Radius_defaults_and_is_bounded()
        {
            Assert.Equal(80, ReturnToStoreRules.NormalizeRadius(null));
            Assert.Equal(10, ReturnToStoreRules.NormalizeRadius(10));
            Assert.Equal(2000, ReturnToStoreRules.NormalizeRadius(2000));
            Assert.Throws<DeliveryDomainException>(() => ReturnToStoreRules.NormalizeRadius(9));
            Assert.Throws<DeliveryDomainException>(() => ReturnToStoreRules.NormalizeRadius(2001));
        }

        // Loja no Centro de Uberlandia; ~0,001 grau de latitude = ~111 m.
        private const double StoreLat = -18.9186;
        private const double StoreLon = -48.2772;

        [Fact]
        public void Inside_the_radius_counts_as_arrived()
        {
            Assert.True(ReturnToStoreRules.IsInsideStore(StoreLat, StoreLon, StoreLat, StoreLon, 80));
            Assert.True(ReturnToStoreRules.IsInsideStore(StoreLat + 0.0005, StoreLon, StoreLat, StoreLon, 80)); // ~55 m
        }

        [Fact]
        public void Outside_the_radius_does_not_count()
        {
            Assert.False(ReturnToStoreRules.IsInsideStore(StoreLat + 0.001, StoreLon, StoreLat, StoreLon, 80)); // ~111 m
            Assert.True(ReturnToStoreRules.IsInsideStore(StoreLat + 0.001, StoreLon, StoreLat, StoreLon, 200));
        }

        [Fact]
        public void Missing_coordinates_never_count_as_inside()
        {
            Assert.False(ReturnToStoreRules.IsInsideStore(null, StoreLon, StoreLat, StoreLon, 80));
            Assert.False(ReturnToStoreRules.IsInsideStore(StoreLat, StoreLon, null, null, 80));
            Assert.False(ReturnToStoreRules.IsInsideStore(double.NaN, StoreLon, StoreLat, StoreLon, 80));
        }
    }

    public class RouteRulesMigrationTests
    {
        private static string Migration(string name)
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "APIBack.csproj"))) dir = Path.GetDirectoryName(dir);
            return File.ReadAllText(Path.Combine(dir ?? string.Empty, "Migrations", "Delivery", name));
        }

        [Fact]
        public void Migration_is_additive_with_safe_defaults()
        {
            var sql = Migration("20260927_01_regras_de_rota.sql");

            Assert.Contains("locked BOOLEAN NOT NULL DEFAULT FALSE", sql);
            Assert.Contains("route_state TEXT NOT NULL DEFAULT 'idle'", sql);
            Assert.Contains("require_return_to_store BOOLEAN NOT NULL DEFAULT FALSE", sql);
            Assert.Contains("store_return_radius_m INTEGER NOT NULL DEFAULT 80", sql);
            Assert.Contains("delivery_tracking_schema_versions", sql);
            Assert.DoesNotContain("DROP TABLE", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("DROP COLUMN", sql, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Migration_runs_before_the_demo_seed()
        {
            Assert.True(string.CompareOrdinal("20260927_01_regras_de_rota.sql", "20260927_90_seed_uberlandia.sql") < 0);
        }
    }
}
