using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Interesse;
using APIBack.Repository;
using APIBack.Repository.Interface;
using APIBack.Service;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace APIBack.Turnos;

public sealed record PreviaDeAberturaDto(bool NovoTurno, string Motivo, DateTimeOffset? ContinuaDesde);

/// <summary>Resposta de GET /api/v2/delivery/turno-entrega. Sem turno vigente (a próxima abertura começa um novo), os três primeiros vêm nulos.</summary>
public sealed record TurnoEntregaDto(
    long? TurnoId, int? NumeroNoDia, DateTimeOffset? IniciadoEm, bool Aberta, int Total, int Reservados, int Gastos,
    PreviaDeAberturaDto PreviaSeAbrir);

/// <summary>Tira do ar quem ficou sem crédito. Nunca lança: quem chama já terminou o que tinha de gravar.</summary>
public interface ITurnoEntregaVarredura
{
    Task TirarSemCreditoAsync(Guid estabelecimentoId, CancellationToken ct = default);
}

public interface ITurnoEntregaService : ITurnoEntregaVarredura
{
    /// <summary>Nulo = turnos ainda não habilitados neste ambiente (migração 20261212_01) ou loja inexistente.</summary>
    Task<SituacaoDoTurno?> SituacaoAsync(Guid estabelecimentoId, CancellationToken ct = default);
    /// <summary>Barra o motoboy do app que tentaria ficar online sem crédito. <paramref name="retomando"/>: o app está renovando um turno.</summary>
    Task ExigirEntradaDoUsuarioAsync(int userId, Guid estabelecimentoId, bool retomando, CancellationToken ct = default);
    /// <summary>Mesma regra para o simulador, sem registrar interesse.</summary>
    Task ExigirEntradaDoMotoboyAsync(int motoboyId, Guid estabelecimentoId, CancellationToken ct = default);
}

public sealed class TurnoEntregaService : ITurnoEntregaService
{
    /// <summary>Motivo de encerramento gravado na sessão; o app explica a partir dele.</summary>
    public const string MotivoSemCredito = "turno_sem_credito";

    private readonly NpgsqlDataSource _dataSource;
    private readonly IOperationalSessionRepository _sessoes;
    private readonly IInteresseService _interesse;
    private readonly TimeProvider _clock;
    private readonly ILogger<TurnoEntregaService>? _logger;

    public TurnoEntregaService(NpgsqlDataSource dataSource, IOperationalSessionRepository sessoes, IInteresseService interesse,
        TimeProvider? clock = null, ILogger<TurnoEntregaService>? logger = null)
    {
        _dataSource = dataSource;
        _sessoes = sessoes;
        _interesse = interesse;
        _clock = clock ?? TimeProvider.System;
        _logger = logger;
    }

    public static TurnoEntregaDto ParaDto(SituacaoDoTurno s) => new(
        s.Turno?.Id, s.Turno?.NumeroNoDia, s.Turno?.IniciadoEm, s.Aberta, s.Total, s.Reservados.Count, s.Gastos.Count,
        new PreviaDeAberturaDto(s.Previa.NovoTurno, s.Previa.Motivo, s.Previa.ContinuaDesde));

    public static string MensagemDeEntrada(SituacaoDoTurno s) => s.Total <= 0
        ? "Esta loja não tem motoboys contratados. Peça à loja para contratar."
        : $"Os créditos de motoboy deste turno de entrega acabaram ({s.Gastos.Count} de {s.Total} usados). Peça à loja para comprar mais motoboys ou espere o próximo turno.";

    public static IReadOnlyDictionary<string, object?> Dados(SituacaoDoTurno s) => new Dictionary<string, object?>
    {
        ["motivo"] = MotivoSemCredito, ["total"] = s.Total, ["gastos"] = s.Gastos.Count, ["reservados"] = s.Reservados.Count
    };

    public async Task<SituacaoDoTurno?> SituacaoAsync(Guid estabelecimentoId, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var situacao = await TurnoEntregaStore.SincronizarAsync(connection, transaction, estabelecimentoId, _clock, ct);
        await transaction.CommitAsync(ct);
        return situacao;
    }

    public async Task ExigirEntradaDoUsuarioAsync(int userId, Guid estabelecimentoId, bool retomando, CancellationToken ct = default)
    {
        int? motoboyId;
        await using (var connection = await _dataSource.OpenConnectionAsync(ct))
        {
            if (!await TurnoEntregaStore.SchemaProntoAsync(connection, null, ct)) return;
            // Mesma identidade que abre a sessão. Sem vínculo, quem recusa é a própria abertura.
            motoboyId = await connection.QueryFirstOrDefaultAsync<int?>(new CommandDefinition(@"
SELECT canonical.id
  FROM motoboy alias
  JOIN motoboy canonical ON canonical.id = alias.canonical_motoboy_id
  JOIN motoboy_estabelecimento me ON me.motoboy_id = canonical.id AND me.estabelecimento_id = @Est AND me.ativo = TRUE
 WHERE alias.id_usuario = @UserId AND canonical.is_simulated = FALSE
 ORDER BY canonical.id LIMIT 1;", new { UserId = userId, Est = estabelecimentoId }, cancellationToken: ct));
        }
        if (!motoboyId.HasValue) return;

        var (situacao, liberado) = await AvaliarEntradaAsync(motoboyId.Value, estabelecimentoId, ct);
        if (liberado || situacao == null) return;

        await _interesse.RegistrarSemCreditoAsync(estabelecimentoId, userId, motoboyId.Value, situacao, ct);
        var mensagem = MensagemDeEntrada(situacao);
        // Quem está renovando um turno precisa receber SESSION_CHANGED: é o que faz o app (inclusive antigo) sair do online.
        if (retomando)
            throw new DeliveryDomainException(409, "SESSION_CHANGED", mensagem,
                ErrosOperacionais.Detalhes(ErrosOperacionais.SemCreditoNoTurno, dados: Dados(situacao)));
        throw ErrosOperacionais.Excecao(ErrosOperacionais.SemCreditoNoTurno, mensagem, dados: Dados(situacao));
    }

    public async Task ExigirEntradaDoMotoboyAsync(int motoboyId, Guid estabelecimentoId, CancellationToken ct = default)
    {
        var (situacao, liberado) = await AvaliarEntradaAsync(motoboyId, estabelecimentoId, ct);
        if (liberado || situacao == null) return;
        throw ErrosOperacionais.Excecao(ErrosOperacionais.SemCreditoNoTurno, MensagemDeEntrada(situacao), dados: Dados(situacao));
    }

    /// <summary>Entra quem já gastou, quem está com pedido (ninguém é interrompido) ou qualquer um enquanto houver crédito não gasto.</summary>
    private async Task<(SituacaoDoTurno? Situacao, bool Liberado)> AvaliarEntradaAsync(int motoboyId, Guid estabelecimentoId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var situacao = await TurnoEntregaStore.SincronizarAsync(connection, transaction, estabelecimentoId, _clock, ct);
        var liberado = situacao == null || !situacao.Esgotado;
        if (!liberado)
        {
            var canonico = await TurnoEntregaStore.CanonicoAsync(connection, transaction, motoboyId, ct);
            liberado = situacao!.Gastos.Contains(canonico) || situacao.Reservados.Contains(canonico) ||
                await connection.ExecuteScalarAsync<bool>(new CommandDefinition(@"
SELECT CASE WHEN to_regclass('delivery_route_stops') IS NULL THEN FALSE ELSE EXISTS (
    SELECT 1 FROM delivery_route_stops s JOIN motoboy m ON m.id = s.motoboy_id
     WHERE s.estabelecimento_id = @Est AND s.stop_status IN ('assigned', 'en_route')
       AND COALESCE(m.canonical_motoboy_id, m.id) = @Motoboy) END;",
                    new { Est = estabelecimentoId, Motoboy = canonico }, transaction, cancellationToken: ct));
        }
        await transaction.CommitAsync(ct);
        return (situacao, liberado);
    }

    private sealed class SessaoRow
    {
        public Guid SessionId { get; set; }
        public int MotoboyId { get; set; }
        public long SessionEpoch { get; set; }
    }

    public async Task TirarSemCreditoAsync(Guid estabelecimentoId, CancellationToken ct = default)
    {
        try
        {
            var situacao = await SituacaoAsync(estabelecimentoId, ct);
            if (situacao is not { Esgotado: true }) return;
            List<SessaoRow> sobraram;
            await using (var connection = await _dataSource.OpenConnectionAsync(ct))
            {
                sobraram = (await connection.QueryAsync<SessaoRow>(new CommandDefinition(@"
SELECT s.session_id AS SessionId, s.motoboy_id AS MotoboyId, s.session_epoch AS SessionEpoch
  FROM motoboy_active_sessions s
 WHERE s.id_estabelecimento = @Est AND s.ended_at_utc IS NULL AND s.revoked_at IS NULL AND s.expires_at_utc > NOW()
   AND NOT (s.motoboy_id = ANY(@ComCredito))
   AND NOT EXISTS (SELECT 1 FROM delivery_route_stops rs JOIN motoboy m ON m.id = rs.motoboy_id
                    WHERE COALESCE(m.canonical_motoboy_id, m.id) = s.motoboy_id AND rs.stop_status IN ('assigned', 'en_route'));",
                    new { Est = estabelecimentoId, ComCredito = situacao.Gastos.Concat(situacao.Reservados).ToArray() }, cancellationToken: ct))).ToList();
            }

            foreach (var sessao in sobraram)
            {
                try
                {
                    await _sessoes.EndSessionAsync(sessao.SessionId, sessao.MotoboyId, sessao.SessionEpoch, MotivoSemCredito, confirmPendingWork: false);
                    await AvisarAppAsync(estabelecimentoId, sessao, ct);
                }
                catch (DeliveryDomainException ex)
                {
                    // Pedido ou transferência pendente apareceu agora: ele não é interrompido.
                    _logger?.LogInformation("Motoboy {MotoboyId} continua online sem crédito: {Codigo}.", sessao.MotoboyId, ex.Code);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogError(ex, "Não foi possível tirar do ar os motoboys sem crédito da loja {Loja}.", estabelecimentoId);
        }
    }

    /// <summary>Evento na fila do próprio motoboy: o app consulta na hora, recebe o encerramento e explica o motivo.</summary>
    private async Task AvisarAppAsync(Guid estabelecimentoId, SessaoRow sessao, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var versao = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT to_regclass('delivery_motoboy_route') IS NOT NULL;", transaction: transaction, cancellationToken: ct))
            ? await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
                "SELECT version FROM delivery_motoboy_route WHERE motoboy_id = @MotoboyId AND estabelecimento_id = @Est;",
                new { sessao.MotoboyId, Est = estabelecimentoId }, transaction, cancellationToken: ct)) ?? 0
            : 0;
        await PedidoQueueRepository.EmitQueueEventAsync(connection, transaction, estabelecimentoId, sessao.MotoboyId, null,
            "sem_credito", versao, sessao.SessionId);
        await transaction.CommitAsync(ct);
    }
}
