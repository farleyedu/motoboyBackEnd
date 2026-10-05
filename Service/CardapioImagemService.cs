using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using APIBack.DTOs.Cardapio;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using SkiaSharp;

namespace APIBack.Service
{
    /// <summary>
    /// Guarda foto de produto (e, no futuro, de categoria) do cardapio em disco (wwwroot) e devolve a URL
    /// publica -- o mesmo esquema que Automation/ConversaAnexoService ja usa para anexo de conversa, so que
    /// escopado por estabelecimento e limitado a imagem. O campo imagem_url do cardapio so guarda a URL,
    /// nunca o arquivo: antes deste servico existir, o front tentava colocar a foto inteira em base64 ali,
    /// por isso o limite de 1000 caracteres na validacao de produto/categoria (CardapioContractService).
    ///
    /// Toda imagem que entra aqui -- enviada do computador ou importada de um link -- sai do mesmo jeito:
    /// cortada no quadrado central, reduzida a no maximo <see cref="TargetSize"/>px e salva como JPEG. Assim
    /// nenhuma tela do sistema (cardapio web, painel, preview) depende de cortar a foto do jeito dela: a foto
    /// ja chega padronizada.
    /// </summary>
    public class CardapioImagemService
    {
        private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "image/png", "image/jpeg", "image/jpg", "image/webp", "image/gif", "image/bmp",
        };

        /// <summary>Tamanho maximo aceito na ENTRADA (antes de padronizar). A foto salva no disco e sempre
        /// bem menor, porque sai redimensionada e recomprimida.</summary>
        private const long MaxInputSizeBytes = 8 * 1024 * 1024;

        private const int TargetSize = 1024;
        private const int JpegQuality = 85;

        private readonly IWebHostEnvironment _environment;
        private readonly IConfiguration _configuration;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IHttpContextAccessor _httpContextAccessor;

        public CardapioImagemService(
            IWebHostEnvironment environment,
            IConfiguration configuration,
            IHttpClientFactory httpClientFactory,
            IHttpContextAccessor httpContextAccessor)
        {
            _environment = environment;
            _configuration = configuration;
            _httpClientFactory = httpClientFactory;
            _httpContextAccessor = httpContextAccessor;
        }

        /// <summary>Foto enviada do computador do atendente (formulario multipart).</summary>
        public async Task<CardapioImagemDto> SalvarAsync(Guid estabelecimentoId, IFormFile file, CancellationToken cancellationToken = default)
        {
            if (file == null || file.Length <= 0)
            {
                throw Invalida("Selecione uma imagem.");
            }

            if (file.Length > MaxInputSizeBytes)
            {
                throw Invalida("Imagem excede o limite de 8 MB.");
            }

            byte[] bytes;
            await using (var stream = file.OpenReadStream())
            {
                bytes = await ReadAllAsync(stream, MaxInputSizeBytes, cancellationToken);
            }

            return await PadronizarESalvarAsync(estabelecimentoId, bytes, cancellationToken);
        }

        /// <summary>Foto importada de um link externo (ex.: gerada numa ferramenta de IA): o servidor baixa,
        /// padroniza e passa a hospedar -- o produto nunca fica dependendo de um link de fora (que pode
        /// expirar ou cair) nem de um host interno da rede (o link nao pode apontar pra dentro da nossa
        /// propria infraestrutura).</summary>
        public async Task<CardapioImagemDto> ImportarDeUrlAsync(Guid estabelecimentoId, string? url, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw Invalida("Link invalido.");
            }

            if (await EhHostInternoAsync(uri.Host))
            {
                throw Invalida("Esse link nao pode ser usado.");
            }

            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);

            HttpResponseMessage response;
            try
            {
                response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            catch (Exception)
            {
                throw Invalida("Nao foi possivel baixar essa imagem.");
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw Invalida("Nao foi possivel baixar essa imagem.");
                }

                var contentType = response.Content.Headers.ContentType?.MediaType;
                if (string.IsNullOrWhiteSpace(contentType) || !AllowedContentTypes.Contains(contentType))
                {
                    throw Invalida("O link precisa apontar direto para uma imagem (PNG, JPG, WEBP ou GIF).");
                }

                var contentLength = response.Content.Headers.ContentLength;
                if (contentLength.HasValue && contentLength.Value > MaxInputSizeBytes)
                {
                    throw Invalida("Imagem excede o limite de 8 MB.");
                }

                await using var downloadStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var bytes = await ReadAllAsync(downloadStream, MaxInputSizeBytes, cancellationToken);
                return await PadronizarESalvarAsync(estabelecimentoId, bytes, cancellationToken);
            }
        }

        private async Task<CardapioImagemDto> PadronizarESalvarAsync(Guid estabelecimentoId, byte[] bytes, CancellationToken cancellationToken)
        {
            using var original = SKBitmap.Decode(bytes);
            if (original == null)
            {
                throw Invalida("Nao foi possivel ler essa imagem. Tente outro arquivo.");
            }

            using var padronizada = Padronizar(original);
            using var imagem = SKImage.FromBitmap(padronizada);
            using var jpeg = imagem.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);

            var root = _environment.WebRootPath;
            if (string.IsNullOrWhiteSpace(root))
            {
                root = Path.Combine(AppContext.BaseDirectory, "wwwroot");
            }

            var relativeFolder = Path.Combine("uploads", "cardapio", estabelecimentoId.ToString("N"));
            var fullFolder = Path.Combine(root, relativeFolder);
            Directory.CreateDirectory(fullFolder);

            var storedName = $"{Guid.NewGuid():N}.jpg";
            var fullPath = Path.Combine(fullFolder, storedName);

            await using (var fileStream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                jpeg.SaveTo(fileStream);
            }

            var relativePath = Path.Combine(relativeFolder, storedName).Replace("\\", "/");
            return new CardapioImagemDto { Url = BuildPublicUrl(relativePath) };
        }

        /// <summary>Corta no quadrado central e reduz (nunca amplia) para no maximo <see cref="TargetSize"/>px
        /// de lado -- toda foto do cardapio sai do mesmo tamanho, com fundo branco onde havia transparencia
        /// (o JPEG final nao tem canal alfa).</summary>
        private static SKBitmap Padronizar(SKBitmap original)
        {
            var side = Math.Min(original.Width, original.Height);
            var left = (original.Width - side) / 2;
            var top = (original.Height - side) / 2;
            var finalSide = Math.Min(side, TargetSize);

            var info = new SKImageInfo(finalSide, finalSide, SKColorType.Rgba8888, SKAlphaType.Premul);
            var result = new SKBitmap(info);
            using (var canvas = new SKCanvas(result))
            {
                canvas.Clear(SKColors.White);
                var source = new SKRectI(left, top, left + side, top + side);
                var dest = new SKRect(0, 0, finalSide, finalSide);
                canvas.DrawBitmap(original, source, dest);
            }

            return result;
        }

        private static async Task<byte[]> ReadAllAsync(Stream stream, long maxBytes, CancellationToken cancellationToken)
        {
            using var memory = new MemoryStream();
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
            {
                total += read;
                if (total > maxBytes)
                {
                    throw Invalida("Imagem excede o limite de 8 MB.");
                }

                memory.Write(buffer, 0, read);
            }

            return memory.ToArray();
        }

        /// <summary>Bloqueia link apontando pra dentro da propria rede (localhost, 10.x, 172.16-31.x,
        /// 192.168.x, link-local) -- sem isso, esse endpoint vira um jeito de fazer o servidor acessar a
        /// propria rede interna por tras de um link disfarçado de foto.</summary>
        private static async Task<bool> EhHostInternoAsync(string host)
        {
            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (IPAddress.TryParse(host, out var literalIp))
            {
                return EhIpInterno(literalIp);
            }

            try
            {
                var enderecos = await Dns.GetHostAddressesAsync(host);
                return enderecos.Length == 0 || enderecos.Any(EhIpInterno);
            }
            catch
            {
                // Nao resolveu o host: por seguranca, trata como invalido em vez de deixar passar.
                return true;
            }
        }

        private static bool EhIpInterno(IPAddress ip)
        {
            if (IPAddress.IsLoopback(ip))
            {
                return true;
            }

            if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal;
            }

            var bytes = ip.GetAddressBytes();
            if (bytes.Length != 4)
            {
                return false;
            }

            if (bytes[0] == 10) return true;
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
            if (bytes[0] == 192 && bytes[1] == 168) return true;
            if (bytes[0] == 169 && bytes[1] == 254) return true;
            return false;
        }

        /// <summary>
        /// App:BaseUrl so vem preenchido em producao (em dev o appsettings guarda o placeholder
        /// "__SET_IN_ENV__", que nenhum .env local troca). Sem ele, a URL relativa que sobrava aqui era
        /// resolvida pelo NAVEGADOR a partir do dominio do PAINEL (zippy-admin), nao do backend -- dava 404 e
        /// a foto aparecia em branco. Request.Scheme/Host sempre aponta pro host que respondeu essa
        /// requisicao, local ou em producao, entao serve de base confiavel quando App:BaseUrl nao esta setado.
        /// </summary>
        private string BuildPublicUrl(string relativePath)
        {
            var baseUrl = ConfiguredBaseUrl();
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                var request = _httpContextAccessor.HttpContext?.Request;
                if (request != null)
                {
                    baseUrl = $"{request.Scheme}://{request.Host}";
                }
            }

            return !string.IsNullOrWhiteSpace(baseUrl) ? $"{baseUrl.TrimEnd('/')}/{relativePath}" : "/" + relativePath;
        }

        private string? ConfiguredBaseUrl()
        {
            var configured = _configuration["App:BaseUrl"];
            if (string.IsNullOrWhiteSpace(configured) || configured.Contains("__SET_IN_ENV__", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return configured;
        }

        private static RequestValidationException Invalida(string message) =>
            new("Imagem invalida.", new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["file"] = new List<string> { message },
            });
    }
}
