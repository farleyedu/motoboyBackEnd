using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using APIBack.Controllers;
using APIBack.Service;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Xunit;

namespace APIBack.Tests.Integration;

/// <summary>Controller de chamados pelo pipeline MVC de verdade (filtros, binding, rotas). O servico de anexos e o real;
/// so a persistencia e trocada por um registro em memoria.</summary>
public sealed class ChamadosHttpTests
{
    private sealed class FakeStorage : IChamadoAnexoStorage
    {
        public int Envios;
        public Task<string> EnviarAsync(string publicId, string nomeArquivo, byte[] bytes, CancellationToken ct = default) { Envios++; return Task.FromResult("https://res/" + publicId); }
        public Task ExcluirAsync(string publicId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed record Chamada(string Via, Guid? Loja, long? Id, int Usuario, bool? DaZippy, string? Categoria, string? Texto, IReadOnlyList<ChamadoAnexoPreparado> Anexos);

    private sealed class FakeChamados(IChamadoAnexoService anexos) : IChamadosService
    {
        public List<Chamada> Chamadas { get; } = [];

        public async Task<ChamadoDto> AbrirAsync(Guid loja, int usuarioId, AbrirChamadoRequest request, IReadOnlyList<ArquivoEnviado>? arquivos = null, CancellationToken ct = default)
        {
            var prontos = await anexos.PrepararAsync(arquivos, ct);
            Chamadas.Add(new("abrir", loja, null, usuarioId, null, request.Categoria, request.Texto, prontos));
            return new ChamadoDto { Id = 1, EstabelecimentoId = loja, Categoria = request.Categoria ?? "", Titulo = request.Texto ?? "" };
        }

        public async Task ResponderAsync(long id, Guid? loja, int usuarioId, bool daZippy, string texto, IReadOnlyList<ArquivoEnviado>? arquivos = null, CancellationToken ct = default)
        {
            var prontos = await anexos.PrepararAsync(arquivos, ct);
            if (texto.Length == 0 && prontos.Count == 0) throw new ChamadoException(422, "Escreva a resposta.");
            Chamadas.Add(new("responder", loja, id, usuarioId, daZippy, null, texto, prontos));
        }

        public Task<IReadOnlyList<ChamadoDto>> ListarAsync(Guid? loja, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(ChamadoDto Chamado, IReadOnlyList<ChamadoMensagemDto> Mensagens)> ObterAsync(long id, Guid? loja, CancellationToken ct = default) => throw new NotSupportedException();
        public Task MudarStatusAsync(long id, string status, CancellationToken ct = default) => throw new NotSupportedException();
        public Task AvaliarAsync(long id, Guid loja, short nota, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static byte[] Png(int largura = 40, int altura = 30)
    {
        using var bitmap = new SKBitmap(largura, altura);
        bitmap.Erase(SKColors.Orange);
        using var imagem = SKImage.FromBitmap(bitmap);
        return imagem.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }

    private static ByteArrayContent Parte(byte[] bytes, string tipo = "image/png")
    {
        var parte = new ByteArrayContent(bytes);
        parte.Headers.ContentType = new MediaTypeHeaderValue(tipo);
        return parte;
    }

    private static async Task Com(Func<HttpClient, FakeChamados, Guid, Task> teste)
    {
        var loja = Guid.NewGuid();
        var anexos = new ChamadoAnexoService(new FakeStorage(), NullLogger<ChamadoAnexoService>.Instance);
        var fake = new FakeChamados(anexos);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IChamadosService>(fake);
        builder.Services.AddControllers(options => options.Filters.Add(new APIBack.Attributes.AuthorizeAttribute()))
            .AddApplicationPart(typeof(ChamadosController).Assembly);
        await using var app = builder.Build();
        // Identidades controladas pelo teste: nenhuma sessao ou credencial real.
        app.Use(async (context, next) =>
        {
            var ator = context.Request.Headers["X-Test-Actor"].ToString();
            if (ator != "")
            {
                context.Items["UserId"] = ator == "gestao" ? 1 : 7;
                if (ator == "loja") context.Items["EstabelecimentoId"] = loja;
                if (ator == "gestao") context.Items["IsSuperAdmin"] = true;
            }
            await next();
        });
        app.MapControllers();
        await app.StartAsync();
        try
        {
            var endereco = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(endereco) };
            await teste(client, fake, loja);
        }
        finally { await app.StopAsync(); }
    }

    private static HttpRequestMessage Pedido(string url, HttpContent conteudo, string? ator)
    {
        var pedido = new HttpRequestMessage(HttpMethod.Post, url) { Content = conteudo };
        if (ator is not null) pedido.Headers.Add("X-Test-Actor", ator);
        return pedido;
    }

    private static async Task<string> Mensagem(HttpResponseMessage resposta) =>
        JsonDocument.Parse(await resposta.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString() ?? "";

    [Fact]
    public Task JsonOpeningKeepsWorkingAndMultipartOpeningCarriesTheImages() => Com(async (client, fake, loja) =>
    {
        var json = await client.SendAsync(Pedido("/api/v2/chamados", JsonContent.Create(new { categoria = "duvida", texto = "Como emitir nota?" }), "loja"));
        Assert.Equal(HttpStatusCode.OK, json.StatusCode);
        var semArquivo = Assert.Single(fake.Chamadas);
        Assert.Equal(("abrir", loja, "duvida", "Como emitir nota?"), (semArquivo.Via, semArquivo.Loja, semArquivo.Categoria, semArquivo.Texto));
        Assert.Empty(semArquivo.Anexos);

        using var form = new MultipartFormDataContent { { new StringContent("parou"), "categoria" }, { new StringContent("Tela travou"), "texto" } };
        form.Add(Parte(Png()), "arquivos", "um.png");
        form.Add(Parte(Png(60, 20)), "arquivos", "dois.png");
        var resposta = await client.SendAsync(Pedido("/api/v2/chamados", form, "loja"));
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var corpo = JsonDocument.Parse(await resposta.Content.ReadAsStringAsync()).RootElement;
        Assert.True(corpo.GetProperty("success").GetBoolean());
        Assert.Equal(1, corpo.GetProperty("data").GetProperty("id").GetInt64());
        var comArquivos = fake.Chamadas[1];
        Assert.Equal(("parou", "Tela travou", loja), (comArquivos.Categoria, comArquivos.Texto, comArquivos.Loja));
        Assert.Equal(["um.png", "dois.png"], comArquivos.Anexos.Select(a => a.Nome));
    });

    [Fact]
    public Task MultipartWithoutImagesStillOpensTheTicket() => Com(async (client, fake, _) =>
    {
        using var form = new MultipartFormDataContent { { new StringContent("ideia"), "categoria" }, { new StringContent("Seria bom ter relatório"), "texto" } };
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Pedido("/api/v2/chamados", form, "loja"))).StatusCode);
        Assert.Empty(Assert.Single(fake.Chamadas).Anexos);
    });

    [Fact]
    public Task InvalidImagesAreRefusedWith422AndAClearMessageAndNothingIsSaved() => Com(async (client, fake, _) =>
    {
        using var pdf = new MultipartFormDataContent { { new StringContent("duvida"), "categoria" }, { new StringContent("Segue o arquivo"), "texto" } };
        pdf.Add(Parte("%PDF-1.4 conteudo"u8.ToArray(), "image/png"), "arquivos", "falso.png");
        var recusado = await client.SendAsync(Pedido("/api/v2/chamados", pdf, "loja"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, recusado.StatusCode);
        Assert.Equal("Só imagens PNG, JPG ou WEBP.", await Mensagem(recusado));

        using var quatro = new MultipartFormDataContent { { new StringContent("duvida"), "categoria" }, { new StringContent("Muitos prints"), "texto" } };
        for (var i = 0; i < 4; i++) quatro.Add(Parte(Png()), "arquivos", $"{i}.png");
        var demais = await client.SendAsync(Pedido("/api/v2/chamados", quatro, "loja"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, demais.StatusCode);
        Assert.Contains("no máximo 3", await Mensagem(demais));

        using var grande = new MultipartFormDataContent { { new StringContent("duvida"), "categoria" }, { new StringContent("Imagem enorme"), "texto" } };
        grande.Add(Parte(new byte[ChamadoAnexoService.MaxBytes + 1]), "arquivos", "enorme.png");
        var pesado = await client.SendAsync(Pedido("/api/v2/chamados", grande, "loja"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, pesado.StatusCode);
        Assert.Contains("5 MB", await Mensagem(pesado));
        Assert.Empty(fake.Chamadas);
    });

    [Fact]
    public Task StoreAndManagementCanReplyWithImagesOnlyOrTextOnly() => Com(async (client, fake, loja) =>
    {
        using var soImagem = new MultipartFormDataContent { { Parte(Png()), "arquivos", "erro.png" } };
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Pedido("/api/v2/chamados/5/mensagens", soImagem, "loja"))).StatusCode);
        var daLoja = Assert.Single(fake.Chamadas);
        Assert.Equal(("responder", (Guid?)loja, 5L, false, ""), (daLoja.Via, daLoja.Loja, daLoja.Id, daLoja.DaZippy, daLoja.Texto));
        Assert.Single(daLoja.Anexos);

        using var daGestao = new MultipartFormDataContent { { new StringContent("Veja o passo a passo"), "texto" } };
        daGestao.Add(Parte(Png()), "arquivos", "passo.png");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Pedido("/api/gestao/chamados/5/mensagens", daGestao, "gestao"))).StatusCode);
        var zippy = fake.Chamadas[1];
        Assert.Equal((true, (Guid?)null, "Veja o passo a passo"), (zippy.DaZippy, zippy.Loja, zippy.Texto));
        Assert.Single(zippy.Anexos);

        var json = await client.SendAsync(Pedido("/api/gestao/chamados/5/mensagens", JsonContent.Create(new { texto = "Só texto" }), "gestao"));
        Assert.Equal(HttpStatusCode.OK, json.StatusCode);
        Assert.Empty(fake.Chamadas[2].Anexos);

        using var vazio = new MultipartFormDataContent { { new StringContent("  "), "texto" } };
        var recusado = await client.SendAsync(Pedido("/api/v2/chamados/5/mensagens", vazio, "loja"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, recusado.StatusCode);
        Assert.Equal(3, fake.Chamadas.Count);
    });

    [Fact]
    public Task AuthenticationAndStoreIsolationApplyToMultipartToo() => Com(async (client, fake, _) =>
    {
        HttpContent Corpo()
        {
            var form = new MultipartFormDataContent { { new StringContent("Resposta"), "texto" } };
            form.Add(Parte(Png()), "arquivos", "x.png");
            return form;
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Pedido("/api/v2/chamados/5/mensagens", Corpo(), null))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Pedido("/api/gestao/chamados/5/mensagens", Corpo(), null))).StatusCode);
        // A loja nao entra no atendimento da Gestao...
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Pedido("/api/gestao/chamados/5/mensagens", Corpo(), "loja"))).StatusCode);
        // ...e a Gestao nao abre chamado como se fosse uma loja (sem loja selecionada).
        using var abrir = new MultipartFormDataContent { { new StringContent("duvida"), "categoria" }, { new StringContent("Teste da gestao"), "texto" } };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(Pedido("/api/v2/chamados", abrir, "gestao"))).StatusCode);
        Assert.Empty(fake.Chamadas);
    });
}
