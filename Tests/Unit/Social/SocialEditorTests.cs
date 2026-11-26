using System.Reflection;
using System.Text.Json;
using APIBack.Attributes;
using APIBack.Controllers;
using APIBack.Options;
using APIBack.Security;
using APIBack.Social.Arquivos;
using APIBack.Social.Editor;
using APIBack.Social.Nucleo;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Npgsql;
using Xunit;

namespace APIBack.Tests.Unit.Social;

public sealed class SocialEditorTests
{
    private static JsonElement Json(string texto) => JsonDocument.Parse(texto).RootElement.Clone();

    private static SocialException Falha(Action acao) => Assert.Throws<SocialException>(acao);

    [Fact]
    public void Receita_confere_so_o_envelope()
    {
        Assert.Contains("\"lay\"", EditorRegras.Receita(7, Json("""{"v":1,"corteId":7,"lay":{"modo":"cima"}}""")));
        Assert.NotNull(EditorRegras.Receita(7, Json("""{"v":1,"corteId":"7"}""")));
        Assert.Equal(400, Falha(() => EditorRegras.Receita(7, Json("""{"corteId":7}"""))).Status);
        Assert.Equal(400, Falha(() => EditorRegras.Receita(7, Json("""{"v":0,"corteId":7}"""))).Status);
        Assert.Equal(400, Falha(() => EditorRegras.Receita(7, Json("""{"v":1,"corteId":8}"""))).Status);
        Assert.Equal(400, Falha(() => EditorRegras.Receita(7, Json("""[1,2]"""))).Status);
    }

    [Fact]
    public void Receita_e_dados_tem_limite_de_tamanho()
    {
        var grande = new string('a', EditorRegras.LimiteReceitaBytes);
        var receita = Falha(() => EditorRegras.Receita(7, Json($$"""{"v":1,"corteId":7,"x":"{{grande}}"}""")));
        Assert.Equal(413, receita.Status);
        Assert.Equal("MUITO_GRANDE", receita.Codigo);

        Assert.NotNull(EditorRegras.Dados(Json($$"""{"x":"{{grande}}"}""")));
        var muito = new string('a', EditorRegras.LimiteDadosBytes);
        Assert.Equal(413, Falha(() => EditorRegras.Dados(Json($$"""{"x":"{{muito}}"}"""))).Status);
        Assert.Equal(400, Falha(() => EditorRegras.Dados(Json("\"texto\""))).Status);
    }

    [Fact]
    public void Ids_dos_cortes()
    {
        Assert.Equal(new long[] { 3, 1, 2 }, EditorRegras.Ids(" 3,1, 2,3 "));
        Assert.Equal(400, Falha(() => EditorRegras.Ids("1,x")).Status);
        Assert.Equal(400, Falha(() => EditorRegras.Ids("")).Status);
        Assert.Equal(400, Falha(() => EditorRegras.Ids(string.Join(',', Enumerable.Range(1, 51)))).Status);
    }

    private static readonly string Hash = new('a', 64);

    [Fact]
    public void Arquivo_so_imagem_ou_audio_ate_25_MB()
    {
        var ok = ArquivosRegras.Validar(new("imagem", "logo.png", "image/png", 1000, Hash.ToUpperInvariant()));
        Assert.Equal(".png", ok.Extensao);
        Assert.Equal(Hash, ok.Hash);
        Assert.Equal("upload", ok.Origem);
        Assert.Equal("voz_ia", ArquivosRegras.Validar(new("audio", "fala.mp3", "audio/mpeg", 10, Hash, "voz_ia")).Origem);

        Assert.Equal(413, Falha(() => ArquivosRegras.Validar(new("audio", "a.mp3", "audio/mpeg", ArquivosRegras.LimiteBytes + 1, Hash))).Status);
        Assert.Equal(400, Falha(() => ArquivosRegras.Validar(new("video", "a.mp4", "video/mp4", 10, Hash))).Status);
        Assert.Equal(400, Falha(() => ArquivosRegras.Validar(new("imagem", "a.svg", "image/svg+xml", 10, Hash))).Status);
        Assert.Equal(400, Falha(() => ArquivosRegras.Validar(new("imagem", "a.mp3", "audio/mpeg", 10, Hash))).Status);
        Assert.Equal(400, Falha(() => ArquivosRegras.Validar(new("imagem", "a.png", "image/png", 10, "123"))).Status);
        Assert.Equal(400, Falha(() => ArquivosRegras.Validar(new("imagem", "a.png", "image/png", 0, Hash))).Status);
        Assert.Equal(400, Falha(() => ArquivosRegras.Validar(new("imagem", "a.png", "image/png", 10, Hash, "outra"))).Status);
    }

    [Fact]
    public void Sem_credencial_do_R2_responde_503_e_nao_quebra()
    {
        var r2 = new R2Armazenamento(Microsoft.Extensions.Options.Options.Create(new SocialR2Options { Endpoint = "https://x", Bucket = "b" }));
        Assert.False(r2.Configurado);
        var erro = Falha(() => r2.UrlParaEnviar("social/x/a.png", "image/png", 10, TimeSpan.FromMinutes(1)));
        Assert.Equal(503, erro.Status);
        Assert.Equal("ARMAZENAMENTO_INDISPONIVEL", erro.Codigo);

        // O serviço recusa antes de ir ao banco.
        using var fonte = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=nada");
        var servico = new SocialArquivosService(fonte, r2);
        var assinc = Assert.ThrowsAsync<SocialException>(() =>
            servico.CriarAsync(Guid.NewGuid(), 1, new("imagem", "a.png", "image/png", 10, Hash), CancellationToken.None)).Result;
        Assert.Equal(503, assinc.Status);
    }

    [Fact]
    public void Com_credencial_gera_url_pre_assinada_com_prefixo_da_loja()
    {
        using var r2 = new R2Armazenamento(Microsoft.Extensions.Options.Options.Create(new SocialR2Options
        {
            Endpoint = "https://conta.r2.cloudflarestorage.com", Bucket = "zippy-social", AccessKey = "AK", SecretKey = "SK"
        }));
        var loja = Guid.NewGuid();
        var key = ArmazenamentoChaves.Arquivo(loja, Guid.NewGuid(), ".png");
        Assert.True(ArmazenamentoChaves.DaLoja(loja, key));
        Assert.False(ArmazenamentoChaves.DaLoja(Guid.NewGuid(), key));
        var url = r2.UrlParaEnviar(key, "image/png", 10, TimeSpan.FromMinutes(15));
        Assert.StartsWith("https://", url);
        Assert.Contains("zippy-social", url);
        Assert.Contains("X-Amz-Signature", url);
        Assert.Contains("X-Amz-Signature", r2.UrlParaLer(key, TimeSpan.FromMinutes(10)));
    }

    private static AuthorizationFilterContext Contexto(params string[] modulos)
    {
        var http = new DefaultHttpContext();
        http.Items["EstabelecimentoModulosAtivos"] = modulos.ToList();
        return new AuthorizationFilterContext(new ActionContext(http, new RouteData(), new ActionDescriptor()), new List<IFilterMetadata>());
    }

    [Fact]
    public void Maquina_do_corte_so_mostra_nome_para_quem_pode_ver()
    {
        var agora = new DateTime(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);
        Assert.Null(EditorRegras.Maquina(null, null, null, false, null, 1, agora));
        Assert.Equal(new MaquinaDoCorteDto(5, "PC da Ana", true, true),
            EditorRegras.Maquina(5, 1, "PC da Ana", false, agora.AddSeconds(-10), 1, agora));
        Assert.Equal(new MaquinaDoCorteDto(5, "PC da Ana", false, false),
            EditorRegras.Maquina(5, 1, "PC da Ana", true, agora.AddHours(-1), 2, agora));
        Assert.Equal(new MaquinaDoCorteDto(null, null, null, false),
            EditorRegras.Maquina(5, 1, "PC da Ana", false, agora, 2, agora));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task Exemplo_responde_404_fora_do_desenvolvimento(string ambiente)
    {
        var controller = new SocialEditorController(null!, null!, new Ambiente(ambiente));
        Assert.IsType<NotFoundObjectResult>(await controller.Exemplo());
    }

    [Fact]
    public async Task Exemplo_em_desenvolvimento_segue_pedindo_login()
    {
        var controller = new SocialEditorController(null!, null!, new Ambiente("Development"))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        Assert.IsType<UnauthorizedObjectResult>(await controller.Exemplo());
    }

    private sealed class Ambiente(string nome) : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = nome;
        public string ApplicationName { get; set; } = "testes";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    [Fact]
    public void Rota_sem_o_modulo_Social_recebe_403()
    {
        var atributo = typeof(SocialEditorController).GetCustomAttributes<RequireModuleAttribute>(true).Single();
        Assert.Equal("Social", atributo.Modulo);

        var sem = Contexto("Pedidos", "Mesas");
        atributo.OnAuthorization(sem);
        var resultado = Assert.IsType<JsonResult>(sem.Result);
        Assert.Equal(403, resultado.StatusCode);
        Assert.Contains("MODULO_INATIVO", JsonSerializer.Serialize(resultado.Value));

        var com = Contexto("SOCIAL");
        atributo.OnAuthorization(com);
        Assert.Null(com.Result);
    }

    [Fact]
    public void Toda_rota_tem_permissao_e_o_catalogo_tem_as_quatro()
    {
        var acoes = typeof(SocialEditorController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes().Any(a => a is Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute)).ToList();
        Assert.Equal(12, acoes.Count);
        Assert.All(acoes, m =>
        {
            var p = Assert.Single(m.GetCustomAttributes<RequirePermissionAttribute>());
            Assert.Equal("Social", p.Modulo);
            Assert.Contains(p.Acao, SocialPermissoes.Acoes);
        });
        Assert.Equal(SocialPermissoes.Acoes.OrderBy(a => a), EstablishmentPermissions.Catalog()["Social"].OrderBy(a => a));
    }
}
