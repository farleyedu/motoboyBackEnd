using System.Reflection;
using APIBack.Attributes;
using APIBack.Controllers;
using APIBack.Social.Editor;
using APIBack.Social.Nucleo;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace APIBack.Tests.Unit.Social;

public sealed class SocialRendersTests
{
    private static SocialException Falha(Action acao) => Assert.Throws<SocialException>(acao);

    [Fact]
    public void Onde_gerar_padrao_e_navegador_e_maquina_precisa_do_id()
    {
        Assert.Equal("navegador", RendersRegras.Onde(null, null));
        Assert.Equal("maquina", RendersRegras.Onde("Maquina", 3));
        Assert.Equal(400, Falha(() => RendersRegras.Onde("maquina", null)).Status);
        Assert.Equal(400, Falha(() => RendersRegras.Onde("nuvem", null)).Status);
    }

    [Fact]
    public void Status_terminado_nao_volta_a_gerar()
    {
        Assert.Equal("pronto", RendersRegras.ProximoStatus("gerando", "pronto"));
        Assert.Equal("gerando", RendersRegras.ProximoStatus("gerando", null));
        Assert.Equal("pronto", RendersRegras.ProximoStatus("pronto", "pronto"));
        Assert.Equal(409, Falha(() => RendersRegras.ProximoStatus("pronto", "gerando")).Status);
        Assert.Equal(409, Falha(() => RendersRegras.ProximoStatus("cancelado", "pronto")).Status);
        Assert.Equal(400, Falha(() => RendersRegras.ProximoStatus("gerando", "pausado")).Status);
    }

    [Fact]
    public void Progresso_fica_entre_0_e_100()
    {
        Assert.Equal(0m, RendersRegras.Progresso(-4));
        Assert.Equal(100m, RendersRegras.Progresso(140));
        Assert.Equal(33.33m, RendersRegras.Progresso(33.3333m));
    }

    [Fact]
    public void Destino_qualidade_e_nome_sao_conferidos()
    {
        Assert.Equal("tiktok", RendersRegras.Destino("TikTok"));
        Assert.Equal("1080p", RendersRegras.Qualidade(null));
        Assert.Equal(400, Falha(() => RendersRegras.Destino("orkut")).Status);
        Assert.Equal(400, Falha(() => RendersRegras.Qualidade("8K")).Status);
        Assert.Equal("zippy-12-shorts-2026-11-26.mp4", RendersRegras.NomeArquivo(" zippy-12-shorts-2026-11-26.mp4 "));
        Assert.Equal(400, Falha(() => RendersRegras.NomeArquivo("../fora.mp4")).Status);
    }

    [Fact]
    public void Chave_do_r2_fica_no_prefixo_da_loja()
    {
        var loja = Guid.NewGuid();
        var id = Guid.NewGuid();
        Assert.StartsWith($"social/{loja:D}/renders/", RendersRegras.Chave(loja, id));
        Assert.EndsWith(".mp4", RendersRegras.Chave(loja, id));
    }

    [Fact]
    public void Rotas_exigem_modulo_e_permissao()
    {
        var t = typeof(SocialRendersController);
        Assert.NotNull(t.GetCustomAttribute<RequireModuleAttribute>());
        Assert.Equal("api/social/renders", t.GetCustomAttribute<RouteAttribute>()?.Template);
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            Assert.NotNull(m.GetCustomAttribute<RequirePermissionAttribute>());
    }
}
