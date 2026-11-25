namespace APIBack.Social.Arquivos;

/// <summary>
/// Armazenamento de objetos do Social (R2). O navegador envia e lê direto pela URL pré-assinada; o backend nunca recebe os bytes.
/// Chaves sempre com o prefixo <c>social/{loja}/</c> (<see cref="ArmazenamentoChaves"/>).
/// Sem credencial configurada, todos os métodos lançam <c>SocialException.SemArmazenamento()</c> (503).
/// </summary>
public interface IArmazenamento
{
    bool Configurado { get; }

    /// <summary>URL para o navegador fazer <c>PUT</c> do arquivo com o <c>Content-Type</c> informado.</summary>
    string UrlParaEnviar(string key, string mime, long bytes, TimeSpan validade);

    string UrlParaLer(string key, TimeSpan validade);

    Task ApagarAsync(string key, CancellationToken ct);

    /// <summary>Tamanho do objeto já enviado, ou null se ele não existe.</summary>
    Task<long?> TamanhoAsync(string key, CancellationToken ct);

    /// <summary>
    /// O backend grava um arquivo pequeno que ele mesmo produziu (R10: fala gerada pela IA). Upload do navegador continua pela URL.
    /// </summary>
    Task EnviarAsync(string key, byte[] bytes, string mime, CancellationToken ct) =>
        throw new NotSupportedException("Este armazenamento não recebe arquivos pelo backend.");
}

public static class ArmazenamentoChaves
{
    public static string Prefixo(Guid loja) => $"social/{loja:D}/";

    public static string Arquivo(Guid loja, Guid arquivoId, string extensao) => $"{Prefixo(loja)}arquivos/{arquivoId:N}{extensao}";

    public static bool DaLoja(Guid loja, string key) => key.StartsWith(Prefixo(loja), StringComparison.Ordinal);
}
