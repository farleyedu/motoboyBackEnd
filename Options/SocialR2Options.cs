namespace APIBack.Options;

/// <summary>
/// Cloudflare R2 do Zippy Social (seção <c>Social:R2</c>). Em produção vem das variáveis de ambiente
/// <c>Social__R2__Endpoint</c>, <c>Social__R2__Bucket</c>, <c>Social__R2__AccessKey</c> e <c>Social__R2__SecretKey</c>.
/// Faltando qualquer uma, as rotas de arquivo respondem 503 e o resto do backend segue normal.
/// </summary>
public sealed class SocialR2Options
{
    public const string Secao = "Social:R2";

    /// <summary>https://&lt;account-id&gt;.r2.cloudflarestorage.com</summary>
    public string? Endpoint { get; set; }
    public string? Bucket { get; set; }
    public string? AccessKey { get; set; }
    public string? SecretKey { get; set; }

    public bool Completo =>
        !string.IsNullOrWhiteSpace(Endpoint) && !string.IsNullOrWhiteSpace(Bucket) &&
        !string.IsNullOrWhiteSpace(AccessKey) && !string.IsNullOrWhiteSpace(SecretKey);
}
