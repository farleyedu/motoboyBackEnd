using APIBack.DTOs.Delivery;
using APIBack.Interesse;
using APIBack.Repository;
using APIBack.Service;
using APIBack.Turnos;
using Xunit;

namespace APIBack.Tests.Integration;

public partial class DeliverySyncDatabaseTests
{
    private sealed class Relogio : TimeProvider
    {
        public DateTimeOffset Agora { get; set; }
        public override DateTimeOffset GetUtcNow() => Agora;
    }

    /// <summary>Terça 20/10/2026, hora local de São Paulo.</summary>
    private static DateTimeOffset Hora(int hora, int minuto = 0, int dia = 20) =>
        new DateTimeOffset(2026, 10, dia, hora, minuto, 0, TimeSpan.FromHours(-3)).ToUniversalTime();

    private const string EsquemaDosTurnos = """
ALTER TABLE estabelecimentos ADD COLUMN nome_fantasia text DEFAULT 'Loja de teste', ADD COLUMN motoboys_base int NOT NULL DEFAULT 3,
 ADD COLUMN timezone_iana text DEFAULT 'America/Sao_Paulo', ADD COLUMN aceita_pedidos bool DEFAULT true, ADD COLUMN data_atualizacao timestamptz;
CREATE TABLE motoboy_pacotes(id bigserial PRIMARY KEY, estabelecimento_id uuid, quantidade int, inicio_utc timestamptz, fim_utc timestamptz, cancelado_em timestamptz);
CREATE TABLE estabelecimento_horario(estabelecimento_id uuid, dia_semana int, fechado bool, abre_as time, fecha_as time);
CREATE TABLE estabelecimento_horario_especial(id bigserial PRIMARY KEY, estabelecimento_id uuid, data date, fechado bool, abre_as time, fecha_as time, motivo text, UNIQUE(estabelecimento_id, data));
INSERT INTO estabelecimento_horario SELECT @StoreId, d, false, '11:00', '23:00' FROM generate_series(0, 6) d;
""";

    private static async Task AplicarTurnos(Database db)
    {
        await db.Execute(EsquemaDosTurnos, new { db.StoreId });
        var pasta = Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery");
        foreach (var arquivo in new[] { "20261212_01_turno_entrega.sql", "20261212_03_loja_interesse.sql" })
        {
            var sql = await File.ReadAllTextAsync(Path.Combine(pasta, arquivo));
            await db.Execute(sql);
            await db.Execute(sql); // idempotente
        }
    }

    /// <summary>Loja com 3 créditos, aberta das 11h às 23h, e 5 motoboys (2 a 6) além do da fixture.</summary>
    private static async Task<(Database Db, Relogio Relogio, TurnoEntregaService Turnos)> LojaComTurnos()
    {
        var db = await Database.Create(maxPoolSize: 20);
        Dapper.SqlMapper.AddTypeHandler(new APIBack.Infrastructure.DateOnlyTypeHandler()); // registrado pela API na subida
        await db.Execute("""
UPDATE motoboy SET canonical_motoboy_id = id;
INSERT INTO motoboy(id, nome, canonical_motoboy_id, is_simulated, id_usuario) SELECT n, 'Motoboy ' || n, n, false, 100 + n FROM generate_series(2, 6) n;
INSERT INTO motoboy_estabelecimento SELECT n, @StoreId, true, false FROM generate_series(2, 6) n;
UPDATE motoboy_active_sessions SET expires_at_utc = NOW() + INTERVAL '1 hour';
CREATE TABLE pedido(id int PRIMARY KEY, id_estabelecimento uuid, origem text);
CREATE TABLE delivery_route_stops(id bigserial PRIMARY KEY, pedido_id int, motoboy_id int, estabelecimento_id uuid, stop_status text, picked_up_at_utc timestamptz, offered_at_utc timestamptz);
""", new { db.StoreId });
        await AplicarTurnos(db);
        var relogio = new Relogio { Agora = Hora(11) };
        var turnos = new TurnoEntregaService(db.Source, db.Repository, new InteresseService(db.Source), relogio);
        return (db, relogio, turnos);
    }

    private static async Task Gastar(Database db, Relogio relogio, int motoboy)
    {
        await using var c = await db.Source.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        var situacao = await TurnoEntregaStore.SincronizarAsync(c, tx, db.StoreId, relogio);
        await TurnoEntregaStore.GravarGastoAsync(c, tx, db.StoreId, situacao!, motoboy, 900 + motoboy);
        await tx.CommitAsync();
    }

    [DeliveryDatabaseFact]
    public async Task ReopeningWithinTwoHoursKeepsTurnAndCreditsAndLaterStartsAnother()
    {
        var (db, relogio, turnos) = await LojaComTurnos();
        await using var _ = db;
        var onze = (await turnos.SituacaoAsync(db.StoreId))!;
        Assert.Equal(3, onze.Total); Assert.Empty(onze.Gastos); Assert.Equal(Hora(11), onze.Turno!.IniciadoEm);
        relogio.Agora = Hora(14);
        foreach (var motoboy in new[] { 1, 2, 3 }) await Gastar(db, relogio, motoboy);

        relogio.Agora = Hora(15);
        var horarios = new HorarioOperacaoRepository(db.Source, relogio);
        await horarios.FecharAgoraAsync(db.StoreId, 7);
        var fechada = (await turnos.SituacaoAsync(db.StoreId))!;
        Assert.False(fechada.Aberta); Assert.Equal(3, fechada.Gastos.Count); Assert.False(fechada.Previa.NovoTurno);

        relogio.Agora = Hora(15, 30);
        await horarios.AbrirHojeAsync(db.StoreId, 7, new() { EstabelecimentoId = db.StoreId, DataLocal = "2026-10-20", FechaAs = "23:00" });
        var reaberta = (await turnos.SituacaoAsync(db.StoreId))!;
        Assert.True(reaberta.Aberta); Assert.Equal(onze.Turno.Id, reaberta.Turno!.Id); Assert.Equal(3, reaberta.Gastos.Count);

        relogio.Agora = Hora(15, 40);
        await horarios.FecharAgoraAsync(db.StoreId, 7);
        relogio.Agora = Hora(18);
        await horarios.AbrirHojeAsync(db.StoreId, 7, new() { EstabelecimentoId = db.StoreId, DataLocal = "2026-10-20", FechaAs = "23:00" });
        var segundo = (await turnos.SituacaoAsync(db.StoreId))!;
        Assert.NotEqual(onze.Turno.Id, segundo.Turno!.Id); Assert.Equal(2, segundo.Turno.NumeroNoDia); Assert.Empty(segundo.Gastos);
    }

    [DeliveryDatabaseFact]
    public async Task PausingOrdersDoesNotCloseTheStore()
    {
        var (db, relogio, turnos) = await LojaComTurnos();
        await using var _ = db;
        var antes = (await turnos.SituacaoAsync(db.StoreId))!;
        await db.Execute("UPDATE estabelecimentos SET aceita_pedidos = false");
        relogio.Agora = Hora(20);
        var depois = (await turnos.SituacaoAsync(db.StoreId))!;
        Assert.True(depois.Aberta); Assert.Equal(antes.Turno!.Id, depois.Turno!.Id);
    }

    [DeliveryDatabaseFact]
    public async Task PurchaseReleasesAtOnceAndTotalNeverShrinksDuringTheTurn()
    {
        var (db, relogio, turnos) = await LojaComTurnos();
        await using var _ = db;
        foreach (var motoboy in new[] { 2, 3, 4 }) await Gastar(db, relogio, motoboy);
        var recusa = await Assert.ThrowsAsync<DeliveryDomainException>(() => turnos.ExigirEntradaDoUsuarioAsync(105, db.StoreId, false));
        Assert.Equal("SEM_CREDITO_NO_TURNO", recusa.Code);
        await Assert.ThrowsAsync<DeliveryDomainException>(() => turnos.ExigirEntradaDoUsuarioAsync(105, db.StoreId, false));
        Assert.Equal(1, await db.Scalar<int>("SELECT COUNT(*)::int FROM loja_interesse WHERE tipo = 'sem_credito'"));
        Assert.Equal("SESSION_CHANGED", (await Assert.ThrowsAsync<DeliveryDomainException>(() => turnos.ExigirEntradaDoUsuarioAsync(105, db.StoreId, true))).Code);
        await turnos.ExigirEntradaDoUsuarioAsync(102, db.StoreId, false); // quem já gastou entra

        await db.Execute("INSERT INTO motoboy_pacotes(estabelecimento_id, quantidade, inicio_utc, fim_utc) VALUES(@StoreId, 3, NOW() - INTERVAL '1 day', NOW() + INTERVAL '29 days')", new { db.StoreId });
        await turnos.ExigirEntradaDoUsuarioAsync(105, db.StoreId, false);
        Assert.Equal(6, (await turnos.SituacaoAsync(db.StoreId))!.Total);

        await db.Execute("UPDATE motoboy_pacotes SET cancelado_em = NOW(); UPDATE estabelecimentos SET motoboys_base = 1");
        Assert.Equal(6, (await turnos.SituacaoAsync(db.StoreId))!.Total);
        relogio.Agora = Hora(11, 0, 21);
        Assert.Equal(1, (await turnos.SituacaoAsync(db.StoreId))!.Total);
    }

    [DeliveryDatabaseFact]
    public async Task ReservationComesFromOpenStopsAndReturnsWhenTheOrderLeavesTheRider()
    {
        var (db, _, turnos) = await LojaComTurnos();
        await using var __ = db;
        await db.Execute("""
INSERT INTO pedido VALUES(1, @StoreId, 'painel'), (2, @StoreId, 'simulador'), (3, @StoreId, NULL);
INSERT INTO delivery_route_stops(pedido_id, motoboy_id, estabelecimento_id, stop_status) VALUES(1, 2, @StoreId, 'assigned'), (2, 3, @StoreId, 'en_route'), (3, 4, @StoreId, 'en_route');
""", new { db.StoreId });
        Assert.Equal(new[] { 2, 4 }, (await turnos.SituacaoAsync(db.StoreId))!.Reservados.Order());
        await db.Execute("UPDATE delivery_route_stops SET stop_status = 'removed' WHERE pedido_id = 1");
        Assert.Equal(new[] { 4 }, (await turnos.SituacaoAsync(db.StoreId))!.Reservados.Order());
    }

    [DeliveryDatabaseFact]
    public async Task ConcurrentReservationsNeverPassTheTotal()
    {
        var (db, relogio, _) = await LojaComTurnos();
        await using var __ = db;
        await db.Execute("INSERT INTO pedido SELECT n, @StoreId, 'painel' FROM generate_series(1, 6) n", new { db.StoreId });
        var resultados = await Task.WhenAll(Enumerable.Range(1, 6).Select(async motoboy =>
        {
            await using var c = await db.Source.OpenConnectionAsync();
            await using var tx = await c.BeginTransactionAsync();
            var situacao = (await TurnoEntregaStore.SincronizarAsync(c, tx, db.StoreId, relogio))!;
            if (!situacao.PodeReceber(motoboy)) return false;
            await Dapper.SqlMapper.ExecuteAsync(c,
                "INSERT INTO delivery_route_stops(pedido_id, motoboy_id, estabelecimento_id, stop_status) VALUES(@m, @m, @s, 'assigned')",
                new { m = motoboy, s = db.StoreId }, tx);
            await tx.CommitAsync();
            return true;
        }));
        Assert.Equal(3, resultados.Count(ok => ok));
        Assert.Equal(1, await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_turno_entrega"));
    }

    [DeliveryDatabaseFact]
    public async Task SpendingTheLastCreditTakesOnlyTheRidersWithoutCreditOffline()
    {
        var (db, relogio, turnos) = await LojaComTurnos();
        await using var _ = db;
        await db.Execute("""
CREATE TABLE motoboy_operational_session_events(event_id uuid, session_id uuid, motoboy_id int, estabelecimento_id uuid, event_type text,
 actor_user_id int, reason text, details jsonb, occurred_at_utc timestamptz);
CREATE TABLE delivery_transfer_requests(id bigint, status text, from_motoboy_id int, to_motoboy_id int);
""");
        foreach (var motoboy in new[] { 2, 3 }) await Gastar(db, relogio, motoboy);
        await turnos.TirarSemCreditoAsync(db.StoreId);
        Assert.Equal(1, await db.Scalar<int>("SELECT COUNT(*)::int FROM motoboy_active_sessions WHERE ended_at_utc IS NULL"));
        await Gastar(db, relogio, 4);
        await turnos.TirarSemCreditoAsync(db.StoreId);
        Assert.Equal("turno_sem_credito", await db.Scalar<string?>("SELECT end_reason FROM motoboy_active_sessions"));
    }

    [DeliveryDatabaseFact]
    public async Task PickupSpendsTheCreditAndANewAssignmentIsRefusedWhenExhausted()
    {
        await using var db = await CompletionDatabase();
        await db.Execute("ALTER TABLE pedido ADD COLUMN origem text; INSERT INTO motoboy(id, nome, canonical_motoboy_id, is_simulated, id_usuario) VALUES(2, 'Outro', 2, false, 8)");
        await AplicarTurnos(db);
        await db.Execute("UPDATE estabelecimentos SET motoboys_base = 1; DELETE FROM estabelecimento_horario; INSERT INTO estabelecimento_horario SELECT id, d, false, '00:00', '00:00' FROM estabelecimentos, generate_series(0, 6) d");
        await db.Execute("UPDATE delivery_route_stops SET picked_up_at_utc = NULL, arrived_at_utc = NULL");
        var repo = new PedidoQueueRepository(db.Source);
        var turnos = new TurnoEntregaService(db.Source, db.Repository, new InteresseService(db.Source));
        var reservado = (await turnos.SituacaoAsync(db.StoreId))!;
        Assert.Empty(reservado.Gastos); Assert.Equal(new[] { 1 }, reservado.Reservados);

        await repo.PickUpStopsAsync(db.StoreId, 1, new PickupStopsRequest { ExpectedPedidoId = 23, ExpectedVersion = 3, PedidoIds = new() { 23, 24 } });
        var gasto = (await turnos.SituacaoAsync(db.StoreId))!;
        Assert.Equal(new[] { 1 }, gasto.Gastos); Assert.Empty(gasto.Reservados); Assert.True(gasto.Esgotado);
        Assert.False(gasto.PodeReceber(2)); Assert.True(gasto.PodeReceber(1));
    }

    [DeliveryDatabaseFact]
    public async Task WhatLeavesWithTheStoreClosedCountsInTheNextTurn()
    {
        var (db, relogio, turnos) = await LojaComTurnos();
        await using var _ = db;
        await turnos.SituacaoAsync(db.StoreId);
        await Gastar(db, relogio, 2);
        relogio.Agora = Hora(2, 0, 21); // fechada há 3 h
        var madrugada = (await turnos.SituacaoAsync(db.StoreId))!;
        Assert.Null(madrugada.Turno); Assert.True(madrugada.Previa.NovoTurno); Assert.Empty(madrugada.Gastos);
        await Gastar(db, relogio, 5);
        Assert.Equal(new[] { 5 }, (await turnos.SituacaoAsync(db.StoreId))!.Gastos);

        relogio.Agora = Hora(11, 5, 21);
        var aberta = (await turnos.SituacaoAsync(db.StoreId))!;
        Assert.Equal(Hora(11, 0, 21), aberta.Turno!.IniciadoEm);
        Assert.Equal(new[] { 5 }, aberta.Gastos);
        Assert.Equal(0, await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_turno_credito WHERE turno_id IS NULL"));
    }

    [DeliveryDatabaseFact]
    public async Task DeliveryStartedInTheOldTurnDoesNotSpendTheNewOne()
    {
        await using var db = await CompletionDatabase();
        await db.Execute("ALTER TABLE pedido ADD COLUMN origem text");
        await AplicarTurnos(db);
        await db.Execute("DELETE FROM estabelecimento_horario; INSERT INTO estabelecimento_horario SELECT id, d, false, '00:00', '00:00' FROM estabelecimentos, generate_series(0, 6) d");
        // Concluída sem coleta marcada, mas em rota desde antes de o turno atual começar.
        await db.Execute("UPDATE delivery_route_stops SET picked_up_at_utc = NULL, arrived_at_utc = NULL, started_at_utc = NOW() - INTERVAL '3 days'");
        var turnos = new TurnoEntregaService(db.Source, db.Repository, new InteresseService(db.Source));
        await turnos.SituacaoAsync(db.StoreId);
        await new PedidoQueueRepository(db.Source).CompleteCurrentAsync(db.StoreId, 7, 1);
        Assert.Equal(0, await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_turno_credito"));
    }

    [DeliveryDatabaseFact]
    public async Task InterestListReadsPurchasesAndTeamRequestsWithoutCopyingThem()
    {
        var (db, _, __) = await LojaComTurnos();
        await using var ___ = db;
        await db.Execute("""
CREATE TABLE usuario(id int PRIMARY KEY, nome text);
INSERT INTO usuario VALUES (7, 'Ana');
CREATE TABLE produtos_avulsos(id bigint PRIMARY KEY, nome text, motoboys int);
INSERT INTO produtos_avulsos VALUES (1, '3 motoboys', 3), (2, 'Modulo Mesas', 0);
CREATE TABLE compra_extra(id int PRIMARY KEY, id_estabelecimento uuid, nome text, valor_total numeric(12,2), quantidade int, motoboys int,
 status text, criado_por int, ativada_em timestamptz, cortesia bool DEFAULT false, created_at timestamptz);
CREATE TABLE comercial_cobranca(id_compra int, expira_em timestamptz);
CREATE TABLE solicitacao_compra(id int, id_estabelecimento uuid, produto_avulso_id bigint, quantidade int, solicitante_id int, status text, created_at timestamptz);
INSERT INTO compra_extra VALUES
 (1, @StoreId, '3 motoboys', 45, 1, 3, 'ativa', 7, NOW() - INTERVAL '1 day', false, NOW() - INTERVAL '1 day'),
 (2, @StoreId, '3 motoboys', 45, 2, 3, 'aguardando_pagamento', 7, NULL, false, NOW() - INTERVAL '3 hours'),
 (3, @StoreId, '3 motoboys', 45, 1, 3, 'aguardando_pagamento', 7, NULL, false, NOW()),
 (4, @StoreId, 'Modulo Mesas', 60, 1, 0, 'ativa', 7, NOW(), false, NOW()),
 (5, @StoreId, '3 motoboys', 0, 1, 3, 'ativa', 7, NOW(), true, NOW());
INSERT INTO comercial_cobranca VALUES (2, NOW() - INTERVAL '2 hours'), (3, NOW() + INTERVAL '1 hour');
INSERT INTO solicitacao_compra VALUES (1, @StoreId, 1, 1, 7, 'pendente', NOW() - INTERVAL '2 days'), (2, @StoreId, 2, 1, 7, 'pendente', NOW());
""", new { db.StoreId });
        var interesse = new InteresseService(db.Source);
        var lista = await interesse.ListarMotoboysAsync(30, null);
        Assert.Equal(new[] { "pix_nao_pago", "compra", "pedido_equipe" }, lista.Select(i => i.Tipo));
        Assert.Equal(new int?[] { 6, 3, 3 }, lista.Select(i => i.Quantidade));
        Assert.All(lista, i => Assert.Equal("Ana", i.Pessoa!.Nome));
        // Sem a tabela do registro, a lista continua saindo das tabelas de compra.
        await db.Execute("DROP TABLE loja_interesse");
        Assert.Equal(3, (await interesse.ListarMotoboysAsync(30, db.StoreId)).Count);
    }

    [DeliveryDatabaseFact]
    public async Task PurchaseInterestAndRollbackKeepHistory()
    {
        var (db, _, __) = await LojaComTurnos();
        await using var ___ = db;
        var interesse = new InteresseService(db.Source);
        Assert.True(await interesse.RegistrarAbriuCompraAsync(db.StoreId, 7, "gerente"));
        Assert.True(await interesse.RegistrarAbriuCompraAsync(db.StoreId, 7, "gerente"));
        var linha = Assert.Single(await interesse.ListarMotoboysAsync(30, db.StoreId));
        Assert.Equal("abriu_compra", linha.Tipo); Assert.Equal(7, linha.Pessoa!.Id);
        var pasta = Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", "rollback");
        await Assert.ThrowsAsync<Npgsql.PostgresException>(async () => await db.Execute(await File.ReadAllTextAsync(Path.Combine(pasta, "20261212_03_loja_interesse.down.sql"))));
        await db.Execute(await File.ReadAllTextAsync(Path.Combine(pasta, "20261212_01_turno_entrega.down.sql")));
        Assert.Null(await db.Scalar<string?>("SELECT to_regclass('delivery_turno_entrega')::text"));
    }
}
