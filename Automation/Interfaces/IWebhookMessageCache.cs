namespace APIBack.Automation.Interfaces
{
    public interface IWebhookMessageCache
    {
        /// <summary>
        /// Registra o messageId se ainda não tiver sido visto nos últimos minutos.
        /// Retorna true quando o ID é novo (deve ser processado) e false quando é duplicado.
        /// </summary>
        bool TryRegister(string? messageId);
    }
}
