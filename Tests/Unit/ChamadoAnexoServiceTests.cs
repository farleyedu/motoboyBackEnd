using APIBack.Service;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Xunit;

namespace APIBack.Tests.Unit;

public sealed class ChamadoAnexoServiceTests
{
    private sealed class FakeStorage : IChamadoAnexoStorage
    {
        public List<string> Enviados { get; } = [];
        public List<string> Excluidos { get; } = [];
        public int FalharNoEnvioNumero { get; set; } = -1;
        public bool FalharAoExcluir { get; set; }

        public Task<string> EnviarAsync(string publicId, string nomeArquivo, byte[] bytes, CancellationToken ct = default)
        {
            if (Enviados.Count == FalharNoEnvioNumero) throw new InvalidOperationException("Cloudinary fora do ar.");
            Enviados.Add(publicId);
            return Task.FromResult("https://res.cloudinary.com/zippy/image/upload/v1/" + publicId + ".png");
        }

        public Task ExcluirAsync(string publicId, CancellationToken ct = default)
        {
            if (FalharAoExcluir) throw new InvalidOperationException("sem rede");
            Excluidos.Add(publicId);
            return Task.CompletedTask;
        }
    }

    private static ChamadoAnexoService Novo(FakeStorage? storage = null) => new(storage ?? new FakeStorage(), NullLogger<ChamadoAnexoService>.Instance);

    private static byte[] Imagem(int largura, int altura, SKEncodedImageFormat formato)
    {
        using var bitmap = new SKBitmap(largura, altura);
        bitmap.Erase(SKColors.CornflowerBlue);
        using var imagem = SKImage.FromBitmap(bitmap);
        return imagem.Encode(formato, 90).ToArray();
    }

    private static ArquivoEnviado Arquivo(byte[] bytes, string nome = "print.png", string? contentType = "image/png", long? tamanho = null) =>
        new(nome, contentType, tamanho ?? bytes.Length, () => new MemoryStream(bytes));

    private static async Task<ChamadoException> Recusa(ChamadoAnexoService service, params ArquivoEnviado[] arquivos)
    {
        var ex = await Assert.ThrowsAsync<ChamadoException>(() => service.PrepararAsync(arquivos));
        Assert.Equal(422, ex.Status);
        return ex;
    }

    [Theory]
    [InlineData(SKEncodedImageFormat.Png, "image/png", ".png")]
    [InlineData(SKEncodedImageFormat.Jpeg, "image/jpeg", ".jpg")]
    [InlineData(SKEncodedImageFormat.Webp, "image/webp", ".webp")]
    public async Task AcceptsPngJpegAndWebpByRealContentAndKeepsTheOriginalBytes(SKEncodedImageFormat formato, string tipo, string extensao)
    {
        var bytes = Imagem(400, 300, formato);
        var pronto = Assert.Single(await Novo().PrepararAsync([Arquivo(bytes, "tela", "application/octet-stream")]));
        Assert.Equal(tipo, pronto.Tipo);
        Assert.Equal("tela" + extensao, pronto.Nome);
        Assert.Equal(bytes, pronto.Bytes);
    }

    [Fact]
    public async Task NoFilesIsNotAnError()
    {
        Assert.Empty(await Novo().PrepararAsync(null));
        Assert.Empty(await Novo().PrepararAsync([]));
    }

    [Fact]
    public async Task RejectsAFourthImage()
    {
        var bytes = Imagem(20, 20, SKEncodedImageFormat.Png);
        Assert.Equal(3, (await Novo().PrepararAsync([Arquivo(bytes), Arquivo(bytes), Arquivo(bytes)])).Count);
        var ex = await Recusa(Novo(), Arquivo(bytes), Arquivo(bytes), Arquivo(bytes), Arquivo(bytes));
        Assert.Contains("no máximo 3", ex.Message);
    }

    [Fact]
    public async Task TrustsTheContentAndNotTheDeclaredTypeOrExtension()
    {
        var executavel = "MZ\u0090\0\u0003\0\0\0"u8.ToArray();
        var ex = await Recusa(Novo(), Arquivo(executavel, "print.png", "image/png"));
        Assert.Equal("Só imagens PNG, JPG ou WEBP.", ex.Message);
        await Recusa(Novo(), Arquivo([(byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a', 1, 0, 1, 0, 0, 0, 0], "print.png", "image/png"));
        await Recusa(Novo(), Arquivo("%PDF-1.4"u8.ToArray(), "print.png", "image/png"));
        await Recusa(Novo(), Arquivo("<svg xmlns='http://www.w3.org/2000/svg'/>"u8.ToArray(), "print.svg", "image/svg+xml"));
    }

    [Fact]
    public async Task RejectsCorruptImagesThatOnlyHaveAValidHeader()
    {
        var truncada = Imagem(50, 50, SKEncodedImageFormat.Png).Take(12).ToArray();
        var ex = await Recusa(Novo(), Arquivo(truncada, "quebrada.png"));
        Assert.Contains("Não foi possível ler", ex.Message);
        await Recusa(Novo(), Arquivo([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4, 5, 6, 7, 8], "lixo.png"));
    }

    [Fact]
    public async Task RejectsEmptyAndOversizedFilesEvenWhenTheDeclaredLengthLies()
    {
        await Recusa(Novo(), Arquivo([], "vazio.png"));
        var grande = await Recusa(Novo(), Arquivo(Imagem(20, 20, SKEncodedImageFormat.Png), "grande.png", tamanho: ChamadoAnexoService.MaxBytes + 1));
        Assert.Contains("5 MB", grande.Message);
        // Length declarado pequeno, mas o fluxo traz mais de 5 MB.
        var real = new byte[ChamadoAnexoService.MaxBytes + 10];
        Array.Copy(Imagem(20, 20, SKEncodedImageFormat.Png), real, 8);
        var mentiroso = await Recusa(Novo(), new ArquivoEnviado("mentira.png", "image/png", 100, () => new MemoryStream(real)));
        Assert.Contains("5 MB", mentiroso.Message);
        // Exatamente 5 MB e aceito (o limite e "ate 5 MB").
        var limite = new byte[ChamadoAnexoService.MaxBytes];
        var png = Imagem(20, 20, SKEncodedImageFormat.Png);
        Array.Copy(png, limite, png.Length);
        // PNG valido seguido de bytes extras continua legivel pelo decoder.
        Assert.Single(await Novo().PrepararAsync([Arquivo(limite, "limite.png")]));
    }

    [Fact]
    public async Task ShrinksHugeScreenshotsKeepingTheAspectRatioWithoutSquareCropping()
    {
        var bytes = Imagem(4000, 1000, SKEncodedImageFormat.Png);
        var pronto = Assert.Single(await Novo().PrepararAsync([Arquivo(bytes)]));
        using var resultado = SKBitmap.Decode(pronto.Bytes);
        Assert.Equal(ChamadoAnexoService.MaxLado, resultado.Width);
        Assert.Equal(750, resultado.Height);
        Assert.Equal("image/png", pronto.Tipo);
    }

    [Fact]
    public async Task SmallImagesAreUploadedUntouchedAndTallScreenshotsAreNotCropped()
    {
        var alta = Imagem(300, 2000, SKEncodedImageFormat.Png);
        var pronto = Assert.Single(await Novo().PrepararAsync([Arquivo(alta)]));
        Assert.Equal(alta, pronto.Bytes);
        using var resultado = SKBitmap.Decode(pronto.Bytes);
        Assert.Equal((300, 2000), (resultado.Width, resultado.Height));
    }

    [Fact]
    public async Task SanitizesTheStoredFileName()
    {
        var bytes = Imagem(10, 10, SKEncodedImageFormat.Png);
        var pronto = Assert.Single(await Novo().PrepararAsync([Arquivo(bytes, "..\\..\\pasta/segredo \"x\".png")]));
        Assert.Equal("segredo x.png", pronto.Nome);
        pronto = Assert.Single(await Novo().PrepararAsync([Arquivo(bytes, "   ")]));
        Assert.Equal("imagem.png", pronto.Nome);
    }

    [Fact]
    public async Task UploadsEachImageUnderTheStoreAndTicketFolder()
    {
        var storage = new FakeStorage();
        var loja = Guid.NewGuid();
        var prontos = await Novo(storage).PrepararAsync([Arquivo(Imagem(10, 10, SKEncodedImageFormat.Png)), Arquivo(Imagem(10, 10, SKEncodedImageFormat.Jpeg), "b.jpg")]);
        var salvos = await Novo(storage).EnviarAsync(loja, 42, prontos);
        Assert.Equal(2, salvos.Count);
        Assert.All(storage.Enviados, id => Assert.StartsWith($"chamados/{loja}/42/", id));
        Assert.Equal(2, storage.Enviados.Distinct().Count());
        Assert.All(salvos, s => Assert.StartsWith("https://", s.Url));
        Assert.Equal(prontos.Select(p => p.Bytes.Length), salvos.Select(s => s.Bytes));
    }

    [Fact]
    public async Task AFailedUploadRemovesWhatAlreadyWentUpAndReturnsABadGateway()
    {
        var storage = new FakeStorage { FalharNoEnvioNumero = 2 };
        var service = Novo(storage);
        var bytes = Imagem(10, 10, SKEncodedImageFormat.Png);
        var prontos = await service.PrepararAsync([Arquivo(bytes), Arquivo(bytes), Arquivo(bytes)]);
        var ex = await Assert.ThrowsAsync<ChamadoException>(() => service.EnviarAsync(Guid.NewGuid(), 7, prontos));
        Assert.Equal(502, ex.Status);
        Assert.Equal(2, storage.Enviados.Count);
        Assert.Equal(storage.Enviados.OrderBy(x => x), storage.Excluidos.OrderBy(x => x));
    }

    [Fact]
    public async Task DiscardingNeverThrowsEvenIfTheStorageIsDown()
    {
        var storage = new FakeStorage { FalharAoExcluir = true };
        await Novo(storage).DescartarAsync([new ChamadoAnexoSalvo("chamados/x/1/a", "https://x", "a.png", "image/png", 10)]);
        Assert.Empty(storage.Excluidos);
    }
}
