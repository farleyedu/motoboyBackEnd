using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Service;
using Dapper;
using Npgsql;

namespace APIBack.Turnos;

/// <summary>
/// Foto do turno que vale agora. <see cref="Turno"/> nulo = loja fechada há tempo bastante para a próxima abertura começar
/// um turno novo: o que sair até lá conta nele. Gastos e reservados são ids canônicos de motoboy e nunca se repetem.
/// </summary>
public sealed record SituacaoDoTurno(
    DateTimeOffset Agora, bool Aberta, DateTimeOffset? FechadaDesde, TurnoEntrega? Turno, int Total,
    IReadOnlySet<int> Gastos, IReadOnlySet<int> Reservados, PreviaDeAbertura Previa)
{
    public bool Esgotado => Gastos.Count >= Total;
    /// <summary>Quem já tem crédito (gasto ou reservado) pode receber; os outros precisam de um crédito livre.</summary>
    public bool PodeReceber(int motoboyId) =>
        Gastos.Contains(motoboyId) || Reservados.Contains(motoboyId) || Gastos.Count + Reservados.Count < Total;
}

/// <summary>
/// Turnos de entrega no banco. Toda leitura ou escrita passa por <see cref="SincronizarAsync"/>, que serializa a loja com
/// um lock consultivo até o fim da transação: quem chama deve pedi-lo depois das travas de linha que precisar, nunca antes.
/// As tabelas não têm chave estrangeira para estabelecimentos de propósito (a checagem travaria a linha da loja).
/// Sem a migração 20261212_01 nada é limitado.
/// </summary>
public static class TurnoEntregaStore
{
    private sealed class LojaRow
    {
        public string? Fuso { get; set; }
        public int Contratados { get; set; }
    }

    private sealed class EstadoRow
    {
        public DateTime AvaliadoEm { get; set; }
        public bool Aberta { get; set; }
        public DateTime? FechadaDesde { get; set; }
        public long? TurnoId { get; set; }
    }

    private sealed class TurnoRow
    {
        public long Id { get; set; }
        public DateTime IniciadoEm { get; set; }
        public string Dia { get; set; } = string.Empty;
        public int NumeroNoDia { get; set; }
        public string Motivo { get; set; } = string.Empty;
    }

    public static Task<bool> SchemaProntoAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken ct = default) =>
        connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT to_regclass('delivery_turno_entrega') IS NOT NULL AND to_regclass('delivery_turno_estado') IS NOT NULL AND to_regclass('delivery_turno_credito') IS NOT NULL;",
            transaction: transaction, cancellationToken: ct));

    /// <summary>Traz os turnos da loja até agora, grava o que mudou e devolve a situação. Nulo = sem migração ou loja inexistente.</summary>
    public static async Task<SituacaoDoTurno?> SincronizarAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid estabelecimentoId, TimeProvider clock, CancellationToken ct = default)
    {
        if (!await SchemaProntoAsync(connection, transaction, ct)) return null;
        var p = new { Id = estabelecimentoId };
        // Ordem fixa para não travar em círculo: primeiro a linha da loja (a mesma trava que uma chave estrangeira pediria
        // depois), só então o lock do turno.
        await connection.ExecuteAsync(new CommandDefinition("SELECT 1 FROM estabelecimentos WHERE id = @Id FOR KEY SHARE;", p, transaction, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(hashtextextended(@Chave, 0));",
            new { Chave = "turno-entrega:" + estabelecimentoId }, transaction, cancellationToken: ct));
        // O relógio é lido depois da trava: quem esperou na fila nunca volta no tempo.
        var agora = clock.GetUtcNow().ToUniversalTime();

        var loja = await connection.QuerySingleOrDefaultAsync<LojaRow>(new CommandDefinition(
            "SELECT to_jsonb(e)->>'timezone_iana' AS Fuso, " + MotoboyLimites.SqlContratados +
            " AS Contratados FROM estabelecimentos e WHERE e.id = @Id;", p, transaction, cancellationToken: ct));
        if (loja == null) return null;
        var fuso = HorarioRegras.ResolverFuso(loja.Fuso);

        var salvo = await connection.QuerySingleOrDefaultAsync<EstadoRow>(new CommandDefinition(@"
SELECT avaliado_em AS AvaliadoEm, aberta AS Aberta, fechada_desde AS FechadaDesde, turno_id AS TurnoId
  FROM delivery_turno_estado WHERE estabelecimento_id = @Id;", p, transaction, cancellationToken: ct));

        var desde = salvo == null ? agora - TurnoEntregaRegras.DuracaoMaximaAberta : Utc(salvo.AvaliadoEm);
        if (agora - desde > TurnoEntregaRegras.ReconstrucaoMaxima) desde = agora - TurnoEntregaRegras.ReconstrucaoMaxima;
        var semana = await LerHorarioAsync(connection, transaction, estabelecimentoId,
            DataLocal(desde, fuso).AddDays(-2), DataLocal(agora, fuso).AddDays(2), ct);
        var periodos = TurnoEntregaRegras.PeriodosAbertos(semana, fuso, desde, agora);

        var porDia = (await connection.QueryAsync<(string Dia, int Quantidade)>(new CommandDefinition(@"
SELECT dia_local::text AS Dia, COUNT(*)::int AS Quantidade FROM delivery_turno_entrega
 WHERE estabelecimento_id = @Id AND dia_local >= @De::date GROUP BY dia_local;",
                new { Id = estabelecimentoId, De = Texto(TurnoEntregaRegras.DiaDoTurno(desde, fuso).AddDays(-1)) }, transaction, cancellationToken: ct)))
            .ToDictionary(r => Data(r.Dia), r => r.Quantidade);

        var turnoSalvo = salvo?.TurnoId is long idSalvo ? await LerTurnoAsync(connection, transaction, idSalvo, ct) : null;
        EstadoDosTurnos estado;
        if (salvo == null)
        {
            // Primeira avaliação: se a loja abriu há menos de 24 h, o turno conta dessa abertura; senão, de agora.
            var atual = periodos.Where(i => i.Inicio <= agora && agora < i.Fim).Select(i => (DateTimeOffset?)i.Inicio).FirstOrDefault();
            var inicio = atual.HasValue && atual.Value > desde ? atual.Value : agora;
            estado = new EstadoDosTurnos(inicio, false, null, null, porDia);
        }
        else
        {
            estado = new EstadoDosTurnos(desde, salvo.Aberta, salvo.FechadaDesde.HasValue ? Utc(salvo.FechadaDesde.Value) : null,
                turnoSalvo == null ? null : ParaTurno(turnoSalvo), porDia);
        }

        var avanco = TurnoEntregaRegras.Avancar(estado, periodos, agora, fuso);
        var turno = avanco.Estado.Turno;
        long? primeiroNovo = null;
        foreach (var novo in avanco.Novos)
        {
            var id = await connection.ExecuteScalarAsync<long>(new CommandDefinition(@"
INSERT INTO delivery_turno_entrega (estabelecimento_id, iniciado_em, dia_local, numero_no_dia, motivo, total)
VALUES (@Id, @Inicio, @Dia::date, @Numero, @Motivo, @Total)
ON CONFLICT (estabelecimento_id, iniciado_em) DO UPDATE SET motivo = EXCLUDED.motivo
RETURNING id;",
                new { Id = estabelecimentoId, Inicio = novo.IniciadoEm, Dia = Texto(novo.Dia), Numero = novo.NumeroNoDia, novo.Motivo, Total = loja.Contratados },
                transaction, cancellationToken: ct));
            primeiroNovo ??= id;
            if (ReferenceEquals(novo, turno)) turno = novo with { Id = id };
        }

        // O que saiu com a loja fechada passa a contar no turno que a abertura começou (ou continuou).
        if (avanco.HouveAbertura && turno != null)
            await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE delivery_turno_credito c SET turno_id = @Turno
 WHERE c.estabelecimento_id = @Id AND c.turno_id IS NULL
   AND NOT EXISTS (SELECT 1 FROM delivery_turno_credito o WHERE o.turno_id = @Turno AND o.motoboy_id = c.motoboy_id);
DELETE FROM delivery_turno_credito WHERE estabelecimento_id = @Id AND turno_id IS NULL;",
                new { Id = estabelecimentoId, Turno = primeiroNovo ?? turno.Id }, transaction, cancellationToken: ct));

        var total = loja.Contratados;
        if (turno != null)
        {
            // O total nunca diminui no meio do turno: redução, extra vencido ou retirado valem no próximo.
            total = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                "UPDATE delivery_turno_entrega SET total = GREATEST(total, @Total) WHERE id = @Turno RETURNING total;",
                new { Turno = turno.Id, Total = loja.Contratados }, transaction, cancellationToken: ct));
        }

        await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO delivery_turno_estado (estabelecimento_id, turno_id, avaliado_em, aberta, fechada_desde)
VALUES (@Id, @Turno, @Avaliado, @Aberta, @Fechada)
ON CONFLICT (estabelecimento_id) DO UPDATE SET turno_id = EXCLUDED.turno_id, avaliado_em = EXCLUDED.avaliado_em,
       aberta = EXCLUDED.aberta, fechada_desde = EXCLUDED.fechada_desde;",
            new { Id = estabelecimentoId, Turno = turno?.Id, Avaliado = avanco.Estado.AvaliadoEm, Aberta = avanco.Estado.Aberta, Fechada = avanco.Estado.FechadaDesde },
            transaction, cancellationToken: ct));

        var final = avanco.Estado with { Turno = turno };
        var previa = TurnoEntregaRegras.Previa(final, agora, fuso);
        var vigente = final.Aberta || !previa.NovoTurno ? turno : null;
        if (vigente == null) total = loja.Contratados;

        var gastos = (await connection.QueryAsync<int>(new CommandDefinition(
                vigente == null
                    ? "SELECT motoboy_id FROM delivery_turno_credito WHERE estabelecimento_id = @Id AND turno_id IS NULL;"
                    : "SELECT motoboy_id FROM delivery_turno_credito WHERE turno_id = @Turno;",
                new { Id = estabelecimentoId, Turno = vigente?.Id }, transaction, cancellationToken: ct)))
            .ToHashSet();
        var reservados = await LerReservadosAsync(connection, transaction, estabelecimentoId, ct);
        reservados.ExceptWith(gastos);

        return new SituacaoDoTurno(agora, final.Aberta, final.FechadaDesde, vigente, total, gastos, reservados, previa);
    }

    /// <summary>
    /// Reservado = motoboy com pedido que ainda não saiu (sem coleta, ou oferta sem resposta). Pedido tirado dele antes
    /// de sair some daqui sozinho; o que já saiu foi gasto (ou pertence a um turno anterior). Simulador e teste não contam.
    /// </summary>
    private static async Task<HashSet<int>> LerReservadosAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid estabelecimentoId, CancellationToken ct)
    {
        if (!await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT to_regclass('delivery_route_stops') IS NOT NULL;", transaction: transaction, cancellationToken: ct)))
            return new HashSet<int>();
        return (await connection.QueryAsync<int>(new CommandDefinition(@"
SELECT DISTINCT COALESCE(m.canonical_motoboy_id, m.id)
  FROM delivery_route_stops s
  JOIN motoboy m ON m.id = s.motoboy_id
  JOIN pedido p ON p.id = s.pedido_id
 WHERE s.estabelecimento_id = @Id AND s.stop_status IN ('assigned', 'en_route')
   AND (s.picked_up_at_utc IS NULL OR to_jsonb(s)->>'offered_at_utc' IS NOT NULL)
   AND COALESCE(to_jsonb(p)->>'origem', '') NOT IN ('simulador', 'teste');",
            new { Id = estabelecimentoId }, transaction, cancellationToken: ct))).ToHashSet();
    }

    /// <summary>Marca o crédito do motoboy como gasto no turno vigente (uma vez por motoboy).</summary>
    public static Task GravarGastoAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid estabelecimentoId,
        SituacaoDoTurno situacao, int motoboyId, int pedidoId, CancellationToken ct = default) =>
        connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO delivery_turno_credito (estabelecimento_id, turno_id, motoboy_id, pedido_id, gasto_em)
VALUES (@Id, @Turno, @Motoboy, @Pedido, @Agora)
ON CONFLICT DO NOTHING;",
            new { Id = estabelecimentoId, Turno = situacao.Turno?.Id, Motoboy = motoboyId, Pedido = pedidoId, situacao.Agora },
            transaction, cancellationToken: ct));

    public static Task<int> CanonicoAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, int motoboyId, CancellationToken ct = default) =>
        connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COALESCE((SELECT COALESCE(canonical_motoboy_id, id) FROM motoboy WHERE id = @MotoboyId), @MotoboyId);",
            new { MotoboyId = motoboyId }, transaction, cancellationToken: ct));

    private static async Task<HorarioSemana?> LerHorarioAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid id, DateOnly de, DateOnly ate, CancellationToken ct)
    {
        if (!await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT to_regclass('estabelecimento_horario') IS NOT NULL AND to_regclass('estabelecimento_horario_especial') IS NOT NULL;",
                transaction: transaction, cancellationToken: ct)))
            return null;
        var semanal = (await connection.QueryAsync<(int Dia, bool Fechado, TimeSpan? Abre, TimeSpan? Fecha)>(new CommandDefinition(
            "SELECT dia_semana::int, fechado, abre_as, fecha_as FROM estabelecimento_horario WHERE estabelecimento_id = @Id;",
            new { Id = id }, transaction, cancellationToken: ct))).ToList();
        var especiais = await connection.QueryAsync<(string Data, bool Fechado, TimeSpan? Abre, TimeSpan? Fecha)>(new CommandDefinition(@"
SELECT data::text, fechado, abre_as, fecha_as FROM estabelecimento_horario_especial
 WHERE estabelecimento_id = @Id AND data BETWEEN @De::date AND @Ate::date;",
            new { Id = id, De = Texto(de), Ate = Texto(ate) }, transaction, cancellationToken: ct));
        return new HorarioSemana
        {
            TemSemanal = semanal.Count > 0,
            // No banco 0 = segunda; DayOfWeek 0 = domingo.
            Semanal = semanal.ToDictionary(r => (DayOfWeek)((r.Dia + 1) % 7), r => new HorarioDia(r.Fechado, r.Abre, r.Fecha)),
            Especiais = especiais.ToDictionary(r => Data(r.Data), r => new HorarioDia(r.Fechado, r.Abre, r.Fecha))
        };
    }

    private static Task<TurnoRow?> LerTurnoAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long id, CancellationToken ct) =>
        connection.QuerySingleOrDefaultAsync<TurnoRow?>(new CommandDefinition(@"
SELECT id AS Id, iniciado_em AS IniciadoEm, dia_local::text AS Dia, numero_no_dia AS NumeroNoDia, motivo AS Motivo
  FROM delivery_turno_entrega WHERE id = @Id;", new { Id = id }, transaction, cancellationToken: ct));

    private static TurnoEntrega ParaTurno(TurnoRow row) => new(row.Id, Utc(row.IniciadoEm), Data(row.Dia), row.NumeroNoDia, row.Motivo);
    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    private static DateOnly DataLocal(DateTimeOffset instante, TimeZoneInfo fuso) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instante, fuso).DateTime);
    private static string Texto(DateOnly data) => data.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static DateOnly Data(string texto) => DateOnly.ParseExact(texto, "yyyy-MM-dd", CultureInfo.InvariantCulture);
}
