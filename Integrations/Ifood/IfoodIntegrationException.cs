using System.Net;

namespace APIBack.Integrations.Ifood;

// Nunca incorpora corpo HTTP, credenciais, token ou exceção de transporte original.
public sealed class IfoodIntegrationException : Exception
{
    public string Code { get; }
    public HttpStatusCode? RemoteStatus { get; }
    public bool Retryable { get; }
    public TimeSpan? RetryAfter { get; }

    public IfoodIntegrationException(string code, HttpStatusCode? status = null,
        bool retryable = false, TimeSpan? retryAfter = null)
        : base("Não foi possível obter acesso ao iFood. Consulte o código de diagnóstico.")
    {
        Code = code;
        RemoteStatus = status;
        Retryable = retryable;
        RetryAfter = retryAfter;
    }
}
