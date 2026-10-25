using System.Net;
using CloudinaryDotNet;
using CloudinaryDotNet.Actions;
using SkiaSharp;

namespace APIBack.Service
{
    /// <summary>Arquivo recebido do cliente, ainda sem validar. Abrir devolve um fluxo novo a cada chamada.</summary>
    public sealed record ArquivoEnviado(string? Nome, string? ContentType, long Tamanho, Func<Stream> Abrir);

    /// <summary>Imagem ja validada (tipo real, tamanho, legibilidade), pronta para subir.</summary>
    public sealed record ChamadoAnexoPreparado(string Nome, string Tipo, byte[] Bytes);

    /// <summary>Imagem ja guardada no Cloudinary; PublicId serve para apagar se a gravacao no banco falhar.</summary>
    public sealed record ChamadoAnexoSalvo(string PublicId, string Url, string Nome, string Tipo, int Bytes);

    public sealed class ChamadoAnexoDto
    {
        public long Id { get; set; }
        public string Url { get; set; } = string.Empty;
        public string Nome { get; set; } = string.Empty;
        public string Tipo { get; set; } = string.Empty;
        public int Bytes { get; set; }
    }

    /// <summary>Onde os prints ficam guardados. Separado do servico para os testes nao dependerem do Cloudinary.</summary>
    public interface IChamadoAnexoStorage
    {
        Task<string> EnviarAsync(string publicId, string nomeArquivo, byte[] bytes, CancellationToken ct = default);
        Task ExcluirAsync(string publicId, CancellationToken ct = default);
    }

    public interface IChamadoAnexoService
    {
        /// <summary>Valida quantidade, tamanho e tipo real. Nao toca em rede nem em banco; lanca 422 com mensagem clara.</summary>
        Task<IReadOnlyList<ChamadoAnexoPreparado>> PrepararAsync(IReadOnlyList<ArquivoEnviado>? arquivos, CancellationToken ct = default);

        /// <summary>Sobe todos ou nenhum: se um falhar, os que ja subiram sao apagados e lanca 502.</summary>
        Task<IReadOnlyList<ChamadoAnexoSalvo>> EnviarAsync(Guid loja, long chamadoId, IReadOnlyList<ChamadoAnexoPreparado> prontos, CancellationToken ct = default);

        /// <summary>Apaga o que ja subiu (a gravacao do chamado falhou). Melhor esforco: nunca lanca.</summary>
        Task DescartarAsync(IEnumerable<ChamadoAnexoSalvo> anexos);
    }

    /// <summary>
    /// Prints dos chamados. Diferente das fotos do cardapio, nada de cortar em quadrado: a imagem manda inteira e so e
    /// reduzida, mantendo a proporcao, quando passa de <see cref="MaxLado"/>px (o suficiente para ler a tela e para o
    /// Cloudinary aceitar). Abaixo disso o arquivo original sobe sem ser recomprimido.
    /// </summary>
    public sealed class ChamadoAnexoService(IChamadoAnexoStorage storage, ILogger<ChamadoAnexoService> logger) : IChamadoAnexoService
    {
        public const int MaxArquivos = 3;
        public const int MaxBytes = 5 * 1024 * 1024;
        public const int MaxLado = 3000;
        private const long MaxPixels = 100_000_000; // Acima disso nem decodifica (imagem "bomba").

        public async Task<IReadOnlyList<ChamadoAnexoPreparado>> PrepararAsync(IReadOnlyList<ArquivoEnviado>? arquivos, CancellationToken ct = default)
        {
            if (arquivos is null || arquivos.Count == 0) return [];
            if (arquivos.Count > MaxArquivos) throw new ChamadoException(422, $"Envie no máximo {MaxArquivos} imagens por mensagem.");
            var prontos = new List<ChamadoAnexoPreparado>(arquivos.Count);
            foreach (var arquivo in arquivos) prontos.Add(await PrepararUmAsync(arquivo, ct));
            return prontos;
        }

        private static async Task<ChamadoAnexoPreparado> PrepararUmAsync(ArquivoEnviado arquivo, CancellationToken ct)
        {
            var nome = NomeSeguro(arquivo.Nome);
            if (arquivo.Tamanho <= 0) throw new ChamadoException(422, $"A imagem \"{nome}\" está vazia.");
            if (arquivo.Tamanho > MaxBytes) throw new ChamadoException(422, $"A imagem \"{nome}\" passa de 5 MB.");
            var bytes = await LerAsync(arquivo, ct);
            if (bytes.Length == 0) throw new ChamadoException(422, $"A imagem \"{nome}\" está vazia.");
            var tipo = Detectar(bytes) ?? throw new ChamadoException(422, "Só imagens PNG, JPG ou WEBP.");
            var (formato, extensao) = tipo switch
            {
                "image/png" => (SKEncodedImageFormat.Png, ".png"),
                "image/jpeg" => (SKEncodedImageFormat.Jpeg, ".jpg"),
                _ => (SKEncodedImageFormat.Webp, ".webp"),
            };
            bytes = Reduzir(bytes, formato, nome);
            return new ChamadoAnexoPreparado(ComExtensao(nome, extensao), tipo, bytes);
        }

        private static async Task<byte[]> LerAsync(ArquivoEnviado arquivo, CancellationToken ct)
        {
            await using var stream = arquivo.Abrir();
            using var memoria = new MemoryStream();
            var buffer = new byte[81920];
            int lidos;
            while ((lidos = await stream.ReadAsync(buffer, ct)) > 0)
            {
                // O Length declarado pode mentir: o limite vale para o que de fato chegou.
                if (memoria.Length + lidos > MaxBytes) throw new ChamadoException(422, $"A imagem \"{NomeSeguro(arquivo.Nome)}\" passa de 5 MB.");
                memoria.Write(buffer, 0, lidos);
            }
            return memoria.ToArray();
        }

        /// <summary>Tipo pelo conteudo, nunca pelo nome nem pelo Content-Type que o cliente declarou.</summary>
        internal static string? Detectar(ReadOnlySpan<byte> b)
        {
            if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 && b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A) return "image/png";
            if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "image/jpeg";
            if (b.Length >= 12 && b[0] == (byte)'R' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'F'
                && b[8] == (byte)'W' && b[9] == (byte)'E' && b[10] == (byte)'B' && b[11] == (byte)'P') return "image/webp";
            return null;
        }

        /// <summary>Confere que decodifica de verdade e, se for enorme, reduz mantendo a proporcao.</summary>
        private static byte[] Reduzir(byte[] bytes, SKEncodedImageFormat formato, string nome)
        {
            var ilegivel = new ChamadoException(422, $"Não foi possível ler a imagem \"{nome}\". Tente outro arquivo.");
            using var dados = SKData.CreateCopy(bytes);
            using var codec = SKCodec.Create(dados) ?? throw ilegivel;
            if (codec.EncodedFormat != formato) throw ilegivel;
            var largura = codec.Info.Width;
            var altura = codec.Info.Height;
            if (largura <= 0 || altura <= 0) throw ilegivel;
            if ((long)largura * altura > MaxPixels) throw new ChamadoException(422, $"A imagem \"{nome}\" é grande demais.");
            if (Math.Max(largura, altura) <= MaxLado) return bytes;
            // Foto girada pelo EXIF perderia a rotacao ao recodificar: nesse caso sobe o original.
            if (codec.EncodedOrigin != SKEncodedOrigin.TopLeft) return bytes;

            using var original = SKBitmap.Decode(codec) ?? throw ilegivel;
            var escala = (double)MaxLado / Math.Max(largura, altura);
            var info = new SKImageInfo(Math.Max(1, (int)Math.Round(largura * escala)), Math.Max(1, (int)Math.Round(altura * escala)));
            using var menor = original.Resize(info, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)) ?? throw ilegivel;
            using var imagem = SKImage.FromBitmap(menor);
            using var codificada = imagem.Encode(formato, formato == SKEncodedImageFormat.Png ? 100 : 90);
            return codificada is null ? bytes : codificada.ToArray();
        }

        public async Task<IReadOnlyList<ChamadoAnexoSalvo>> EnviarAsync(Guid loja, long chamadoId, IReadOnlyList<ChamadoAnexoPreparado> prontos, CancellationToken ct = default)
        {
            var salvos = new List<ChamadoAnexoSalvo>(prontos.Count);
            try
            {
                foreach (var pronto in prontos)
                {
                    var publicId = $"chamados/{loja}/{chamadoId}/{Guid.NewGuid():N}";
                    var url = await storage.EnviarAsync(publicId, pronto.Nome, pronto.Bytes, ct);
                    salvos.Add(new ChamadoAnexoSalvo(publicId, url, pronto.Nome, pronto.Tipo, pronto.Bytes.Length));
                }
                return salvos;
            }
            catch (Exception ex)
            {
                if (ex is not OperationCanceledException) logger.LogError(ex, "Falha ao enviar anexo do chamado {Chamado}.", chamadoId);
                await DescartarAsync(salvos);
                if (ex is OperationCanceledException) throw;
                throw new ChamadoException(502, "Não foi possível enviar as imagens agora. Tente de novo em instantes.");
            }
        }

        public async Task DescartarAsync(IEnumerable<ChamadoAnexoSalvo> anexos)
        {
            foreach (var anexo in anexos)
            {
                try { await storage.ExcluirAsync(anexo.PublicId, CancellationToken.None); }
                catch (Exception ex) { logger.LogWarning(ex, "Anexo órfão no armazenamento: {PublicId}.", anexo.PublicId); }
            }
        }

        private static string NomeSeguro(string? nome)
        {
            var limpo = Path.GetFileName((nome ?? string.Empty).Replace('\\', '/'));
            limpo = new string(limpo.Where(c => !char.IsControl(c) && c != '"').ToArray()).Trim();
            if (limpo.Length > 120) limpo = limpo[..120];
            return limpo.Length == 0 ? "imagem" : limpo;
        }

        private static string ComExtensao(string nome, string extensao)
        {
            var atual = Path.GetExtension(nome).ToLowerInvariant();
            var aceitas = extensao == ".jpg" ? new[] { ".jpg", ".jpeg" } : new[] { extensao };
            return aceitas.Contains(atual) ? nome : nome + extensao;
        }
    }

    /// <summary>Cloudinary, com as mesmas chaves do cardapio. O cliente so e montado no primeiro uso: sem chaves, os chamados sem imagem seguem funcionando.</summary>
    public sealed class CloudinaryChamadoAnexoStorage(IConfiguration configuration) : IChamadoAnexoStorage
    {
        private readonly Lazy<Cloudinary> _cloudinary = new(() => CardapioImagemService.BuildCloudinary(configuration));

        public async Task<string> EnviarAsync(string publicId, string nomeArquivo, byte[] bytes, CancellationToken ct = default)
        {
            await using var stream = new MemoryStream(bytes);
            var resultado = await _cloudinary.Value.UploadAsync(new ImageUploadParams
            {
                File = new FileDescription(nomeArquivo, stream),
                PublicId = publicId,
                UseFilename = false,
                UniqueFilename = false,
                Overwrite = false,
            }, ct);
            if (resultado.Error != null || resultado.StatusCode != HttpStatusCode.OK)
                throw new InvalidOperationException($"Cloudinary recusou o anexo: {resultado.Error?.Message ?? resultado.StatusCode.ToString()}.");
            var url = resultado.SecureUrl?.ToString() ?? resultado.Url?.ToString();
            return string.IsNullOrWhiteSpace(url) ? throw new InvalidOperationException("Cloudinary não devolveu a URL do anexo.") : url;
        }

        public async Task ExcluirAsync(string publicId, CancellationToken ct = default)
        {
            var resultado = await _cloudinary.Value.DestroyAsync(new DeletionParams(publicId));
            if (resultado.Error != null) throw new InvalidOperationException($"Cloudinary não apagou o anexo: {resultado.Error.Message}.");
        }
    }
}
