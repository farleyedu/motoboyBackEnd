using APIBack.Model.Auth;

namespace APIBack.Service.Interface;

public sealed record AuthDecisionKey(int? UserId, Guid? EstabelecimentoId, Guid? EmpresaId,
    Guid? MotoboySessionId, long? SessionEpoch, Guid? MotoboyLoginSessionId, int? MotoboyId,
    string TokenUse, Guid? VinculoId, Guid? EmpresaVinculoId, bool IsSuperAdmin,
    string? TipoAcesso, string? ClientType, string? Scope, string Check)
{
    public static AuthDecisionKey From(JwtPayload p, string check) => new(p.UserId,
        p.EstabelecimentoId, p.EmpresaId, p.MotoboySessionId, p.SessionEpoch,
        p.MotoboyLoginSessionId, p.MotoboyId, p.TokenUse, p.VinculoId, p.EmpresaVinculoId,
        p.IsSuperAdmin, p.TipoAcesso, p.ClientType, p.Scope, check);
}

public interface IAuthDecisionCache
{
    Task<bool> IsAllowedAsync(AuthDecisionKey key, Func<Task<bool>> evaluate);
    void InvalidateUser(int userId);
    void InvalidateSession(Guid sessionId);
    void InvalidateMotoboy(int motoboyId);
    void InvalidateEstablishment(Guid estabelecimentoId);
    void InvalidateCompany(Guid empresaId);
}
