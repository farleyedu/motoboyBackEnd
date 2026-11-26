using System.Reflection;
using System.Text.Json;
using APIBack.Attributes;
using APIBack.Controllers;
using APIBack.Social.Editor;
using APIBack.Social.Fila;
using APIBack.Social.Nucleo;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.RateLimiting;
using Xunit;

namespace APIBack.Tests.Unit.Social;

public sealed class SocialFilaTests
{
    private static SocialException Falha(Action acao) => Assert.Throws<SocialException>(acao);

    [Fact]
    public void Codigo_tem_6_digitos_e_o_token_so_vai_ao_banco_como_hash()
    {
        for (var i = 0; i < 200; i++) Assert.Matches("^[0-9]{6}$", FilaRegras.NovoCodigo());
        var token = FilaRegras.NovoToken();
        Assert.StartsWith("zsm_", token);
        Assert.NotEqual(token, FilaRegras.NovoToken());
        Assert.Matches("^[0-9a-f]{64}$", FilaRegras.Hash(token));
        Assert.Equal(FilaRegras.Hash(token), FilaRegras.Hash(token));
        Assert.Equal("012345", FilaRegras.Codigo(" 012 345 "));
        foreach (var ruim in new[] { null, "", "12345", "1234567", "12a456" })
            Assert.Equal("CODIGO_INVALIDO", Falha(() => FilaRegras.Codigo(ruim)).Codigo);
    }

    [Fact]
    public void Online_acesso_e_tentativas()
    {
        var agora = DateTime.UtcNow;
        Assert.True(FilaRegras.EstaOnline(agora.AddSeconds(-89), agora));
        Assert.False(FilaRegras.EstaOnline(agora.AddSeconds(-91), agora));
        Assert.False(FilaRegras.EstaOnline(null, agora));
        Assert.True(FilaRegras.PodeUsar(1, false, 1));
        Assert.True(FilaRegras.PodeUsar(1, true, 2));
        Assert.False(FilaRegras.PodeUsar(1, false, 2));
        Assert.True(FilaRegras.VaiRepetir(true, 2));
        Assert.False(FilaRegras.VaiRepetir(true, 3));
        Assert.False(FilaRegras.VaiRepetir(false, 1));
        Assert.Equal("padrao", FilaRegras.Tema("roxo"));
        Assert.Equal("gta6", FilaRegras.Tema("gta6"));
    }

    [Fact]
    public void Dados_do_corte_saem_como_o_navegador_monta()
    {
        // sem momentos salvos (o exemplo antigo do backend): a base, com o momento forte
        var base_ = FilaRegras.Dados(7, 10m, 50m, 22.5m, """{"exemplo":true,"eventos":["explosao"]}""");
        Assert.Equal("7", (string?)base_["id"]);
        Assert.Equal(40m, (decimal)base_["dur"]!);
        Assert.Empty(base_["marks"]!.AsArray());
        Assert.Null(base_["eventos"]);
        Assert.Equal(22.5m, (decimal)base_["forte"]!);

        // com momentos: o que foi salvo vale, o id é sempre o do corte e o forte salvo não é trocado
        var salvo = FilaRegras.Dados(7, 10m, 50m, 22.5m, """{"id":"1","dur":41,"marks":[{"k":"riso","s":3}],"forte":3,"radio":[1,2]}""");
        Assert.Equal("7", (string?)salvo["id"]);
        Assert.Equal(41m, (decimal)salvo["dur"]!);
        Assert.Equal(3m, (decimal)salvo["forte"]!);
        Assert.Equal(2, salvo["radio"]!.AsArray().Count);
        Assert.Empty(salvo["falas"]!.AsArray());
    }

    [Fact]
    public void Acha_os_arquivos_citados_em_qualquer_lugar_da_receita()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var receita = JsonDocument.Parse($$$$"""
            {"v":1,"wm":{"arquivoId":"{{{{a}}}}"},"camadas":[{"origem":{"de":"arquivo","arquivoId":"{{{{b}}}}"}}],
             "narr":{"blocos":[{"versoes":[{"arquivoId":"{{{{c}}}}"},{"arquivoId":"{{{{a}}}}"},{"arquivoId":null},{"arquivoId":"local-1"}]}]}}
            """).RootElement;
        Assert.Equal(new[] { a, b, c }, FilaRegras.ArquivosDaReceita(receita));
    }

    [Fact]
    public void Original_vem_do_banco_e_o_nome_nao_aceita_pasta()
    {
        var o = FilaRegras.Original(4, false, 735m, " gameplay.mp4 ", 0);
        Assert.Equal("gameplay.mp4", o.Nome);
        Assert.Null(o.Bytes);
        Assert.Equal(735m, o.Ini);
        Assert.Equal(400, Falha(() => FilaRegras.Original(4, false, 0, @"D:\a.mp4", 1)).Status);
        Assert.Equal(400, Falha(() => FilaRegras.Original(4, false, 0, "../a.mp4", 1)).Status);
    }

    [Fact]
    public void Render_de_maquina_pelo_painel_so_cancela()
    {
        RendersRegras.SoCancelar(new("cancelado", null, null, null, null, null, null));
        foreach (var pedido in new AtualizarRenderRequest[]
                 {
                     new("pronto", null, 10, null, null, null, null), new(null, 50, null, null, null, null, null),
                     new("cancelado", 10, null, null, null, null, null)
                 })
            Assert.Equal("RENDER_DE_MAQUINA", Falha(() => RendersRegras.SoCancelar(pedido)).Codigo);
    }

    [Fact]
    public void Rotas_do_painel_pedem_permissao_e_as_da_maquina_nao_pedem_login()
    {
        var painel = typeof(SocialMaquinasController);
        Assert.Equal("api/social/maquinas", painel.GetCustomAttribute<RouteAttribute>()!.Template);
        Assert.NotNull(painel.GetCustomAttribute<RequireModuleAttribute>());
        Assert.Null(painel.GetCustomAttribute<AllowAnonymousAttribute>());
        foreach (var rota in Rotas(painel))
            Assert.NotNull(rota.GetCustomAttribute<RequirePermissionAttribute>());

        var worker = typeof(SocialWorkerController);
        Assert.Equal("api/social/worker", worker.GetCustomAttribute<RouteAttribute>()!.Template);
        Assert.NotNull(worker.GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.Equal(new[] { "pareamento", "sinal", "trabalhos/proximo", "trabalhos/{id:guid}/concluir", "trabalhos/{id:guid}/falhar",
                "trabalhos/{id:guid}/progresso" },
            Rotas(worker).Select(m => m.GetCustomAttribute<HttpMethodAttribute>()!.Template!).OrderBy(t => t, StringComparer.Ordinal));
        // só o pareamento chega sem token: é a única com limite por minuto
        var limitada = Assert.Single(Rotas(worker), m => m.GetCustomAttribute<EnableRateLimitingAttribute>() is not null);
        Assert.Equal("pareamento", limitada.GetCustomAttribute<HttpMethodAttribute>()!.Template);
    }

    private static MethodInfo[] Rotas(Type t) => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
        .Where(m => m.GetCustomAttribute<HttpMethodAttribute>() is not null).ToArray();
}
