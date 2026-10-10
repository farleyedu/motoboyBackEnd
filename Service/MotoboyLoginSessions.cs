using APIBack.DTOs.Auth;
using APIBack.Repository.Interface;
using Dapper;
using Npgsql;

namespace APIBack.Service;

// O bloqueio do usuário serializa dois logins e a confirmação de substituição.
internal static class MotoboyLoginSessions
{
    public static async Task<Guid> BeginAsync(NpgsqlConnection connection, int userId,
        LoginRequest request, DateTime expiresAtUtc, IOperationalSessionRepository operational)
    {
        if (!Guid.TryParse(request.ClientInstanceId, out var clientId) || clientId == Guid.Empty)
            throw new DeliveryDomainException(422, "INVALID_CLIENT_INSTANCE", "Identificação do aparelho inválida.");

        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await connection.ExecuteAsync("SELECT id FROM usuario WHERE id=@UserId FOR UPDATE",
            new { UserId = userId }, transaction);
        await connection.ExecuteAsync("""
UPDATE motoboy_login_sessions SET revoked_at_utc=NOW(),revoke_reason='expired'
 WHERE id_usuario=@UserId AND revoked_at_utc IS NULL AND expires_at_utc<=NOW()
""", new { UserId = userId }, transaction);
        var active = await connection.QuerySingleOrDefaultAsync<Active>("""
SELECT session_id AS SessionId,client_instance_id AS ClientInstanceId
 FROM motoboy_login_sessions WHERE id_usuario=@UserId AND revoked_at_utc IS NULL
""", new { UserId = userId }, transaction);
        // Reconhecer o turno de versões anteriores antes de criar a primeira sessão de login.
        var previousTurn = active == null ? await connection.QueryFirstOrDefaultAsync<LegacyTurn>("""
SELECT session_id AS SessionId,client_instance_id AS ClientInstanceId
 FROM motoboy_active_sessions WHERE id_usuario=@UserId AND origin='mobile'
 AND ended_at_utc IS NULL AND revoked_at IS NULL AND expires_at_utc>NOW()
 ORDER BY started_at_utc DESC LIMIT 1
""", new { UserId = userId }, transaction) : null;
        var otherSessionId = active != null && active.ClientInstanceId != clientId ? active.SessionId
            : previousTurn != null && !string.Equals(previousTurn.ClientInstanceId, clientId.ToString("D"), StringComparison.OrdinalIgnoreCase)
                ? previousTurn.SessionId : (Guid?)null;
        if (otherSessionId.HasValue && (!request.ConfirmSessionReplacement || request.ExpectedSessionId != otherSessionId))
            throw new DeliveryDomainException(409, "LOGIN_SESSION_ACTIVE",
                "Esta conta já está em uso em outro aparelho. Se continuar, a sessão anterior será encerrada.",
                new { sessionId = otherSessionId.Value });

        if (otherSessionId.HasValue)
        {
            await connection.ExecuteAsync("""
UPDATE motoboy_login_sessions SET revoked_at_utc=NOW(),revoke_reason='session_replaced'
 WHERE id_usuario=@UserId AND revoked_at_utc IS NULL;
UPDATE usuario_refresh_tokens SET revoked_at=NOW(),reason_revoked='session_replaced'
 WHERE id_usuario=@UserId AND revoked_at IS NULL
 AND (motoboy_login_session_id=@SessionId OR (@Legacy AND motoboy_login_session_id IS NULL));
""", new { UserId = userId, SessionId = otherSessionId.Value, Legacy = active == null }, transaction);
            // Mantém os pedidos no servidor e usa o encerramento/auditoria existentes.
            await operational.EndActiveMobileSessionsForUserAsync(userId, "login_replaced");
            active = null;
        }
        var sessionId = active?.SessionId ?? Guid.NewGuid();
        await connection.ExecuteAsync("""
INSERT INTO motoboy_login_sessions(session_id,id_usuario,client_instance_id,expires_at_utc)
 VALUES(@SessionId,@UserId,@ClientId,@ExpiresAt)
 ON CONFLICT(session_id) DO UPDATE SET expires_at_utc=EXCLUDED.expires_at_utc
""", new { SessionId = sessionId, UserId = userId, ClientId = clientId, ExpiresAt = expiresAtUtc }, transaction);
        await transaction.CommitAsync();
        return sessionId;
    }

    public static Task<bool> IsActiveAsync(NpgsqlConnection connection, int userId, Guid sessionId,
        CancellationToken cancellationToken = default) => connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
SELECT EXISTS(SELECT 1 FROM motoboy_login_sessions WHERE session_id=@SessionId
 AND id_usuario=@UserId AND revoked_at_utc IS NULL AND expires_at_utc>NOW())
""", new { SessionId = sessionId, UserId = userId }, cancellationToken: cancellationToken));

    private sealed class Active { public Guid SessionId { get; set; } public Guid ClientInstanceId { get; set; } }
    private sealed class LegacyTurn { public Guid SessionId { get; set; } public string? ClientInstanceId { get; set; } }
}
