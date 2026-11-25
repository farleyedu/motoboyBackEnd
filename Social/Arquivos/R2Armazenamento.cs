using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using APIBack.Options;
using APIBack.Social.Nucleo;
using Microsoft.Extensions.Options;

namespace APIBack.Social.Arquivos;

/// <summary>Cloudflare R2 pela API do S3 (AWSSDK.S3). Uma instância só (singleton): o cliente é reaproveitado.</summary>
public sealed class R2Armazenamento : IArmazenamento, IDisposable
{
    private readonly SocialR2Options _opcoes;
    private readonly AmazonS3Client? _s3;

    public R2Armazenamento(IOptions<SocialR2Options> opcoes)
    {
        _opcoes = opcoes.Value;
        if (!_opcoes.Completo) return;
        var config = new AmazonS3Config
        {
            ServiceURL = _opcoes.Endpoint!.TrimEnd('/'),
            ForcePathStyle = true,
            AuthenticationRegion = "auto",
            // O R2 não aceita os checksums que o SDK novo manda por padrão.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        };
        _s3 = new AmazonS3Client(new BasicAWSCredentials(_opcoes.AccessKey, _opcoes.SecretKey), config);
    }

    public bool Configurado => _s3 is not null;

    private AmazonS3Client Cliente => _s3 ?? throw SocialException.SemArmazenamento();

    public string UrlParaEnviar(string key, string mime, long bytes, TimeSpan validade) =>
        Cliente.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = _opcoes.Bucket,
            Key = key,
            Verb = HttpVerb.PUT,
            ContentType = mime,
            Expires = DateTime.UtcNow.Add(validade),
            Protocol = Protocol.HTTPS,
        });

    public string UrlParaLer(string key, TimeSpan validade) =>
        Cliente.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = _opcoes.Bucket,
            Key = key,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(validade),
            Protocol = Protocol.HTTPS,
        });

    public async Task ApagarAsync(string key, CancellationToken ct) =>
        await Cliente.DeleteObjectAsync(new DeleteObjectRequest { BucketName = _opcoes.Bucket, Key = key }, ct);

    public async Task<long?> TamanhoAsync(string key, CancellationToken ct)
    {
        try
        {
            var meta = await Cliente.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = _opcoes.Bucket, Key = key }, ct);
            return meta.ContentLength;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task EnviarAsync(string key, byte[] bytes, string mime, CancellationToken ct)
    {
        using var corpo = new MemoryStream(bytes, writable: false);
        await Cliente.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _opcoes.Bucket,
            Key = key,
            InputStream = corpo,
            ContentType = mime,
            DisablePayloadSigning = true,
        }, ct);
    }

    public void Dispose() => _s3?.Dispose();
}
