using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using APIBack.Payments.Conexao;
using APIBack.Payments.Controllers;
using APIBack.Payments.Gestao;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace APIBack.Tests.Unit;

/// <summary>State/PKCE do OAuth, autorizacao da Gestao (so super admin, no servidor) e quem gerencia a conexao da loja.</summary>
public sealed class PagamentosGestaoOAuthTests
{
    [Fact]
    public void PkceS256SegueARfc7636()
    {
        // Vetor do apendice B da RFC 7636.
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", OAuthPkce.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
        var v = OAuthPkce.NovoVerifier();
        Assert.InRange(v.Length, 43, 128);
        Assert.NotEqual(v, OAuthPkce.NovoVerifier());
    }

    [Fact]
    public void StateEAleatorioComFormatoFixoESoOHashVaiProBanco()
    {
        var s = OAuthPkce.NovoState();
        Assert.True(OAuthPkce.StateValido(s));
        Assert.NotEqual(s, OAuthPkce.NovoState());
        Assert.Equal(64, OAuthPkce.HashState(s).Length);
        Assert.DoesNotContain(s, OAuthPkce.HashState(s));
        Assert.False(OAuthPkce.StateValido(null));
        Assert.False(OAuthPkce.StateValido(s[..42]));
        Assert.False(OAuthPkce.StateValido(s[..42] + "'"));
    }

    [Fact]
    public void UrlDeAutorizacaoLevaStateChallengeERedirect()
    {
        var url = OAuthPkce.UrlAutorizacao("123", "https://api.zippy/api/integracoes/mercadopago/callback", "st", "ch");
        Assert.StartsWith("https://auth.mercadopago.com.br/authorization?client_id=123", url);
        Assert.Contains("state=st", url);
        Assert.Contains("code_challenge=ch&code_challenge_method=S256", url);
        Assert.Contains("redirect_uri=https%3A%2F%2Fapi.zippy%2Fapi%2Fintegracoes%2Fmercadopago%2Fcallback", url);
    }

    private static ActionExecutingContext Contexto(int? usuario, bool superAdmin)
    {
        var http = new DefaultHttpContext();
        if (usuario != null) http.Items["UserId"] = usuario.Value;
        http.Items["IsSuperAdmin"] = superAdmin;
        return new ActionExecutingContext(new ActionContext(http, new RouteData(), new ActionDescriptor()), new List<IFilterMetadata>(),
            new Dictionary<string, object?>(), new object());
    }

    [Theory]
    [InlineData(null, false, 401)]
    [InlineData(null, true, 401)]
    [InlineData(7, false, 403)]
    public void GestaoRecusaQuemNaoESuperAdmin(int? usuario, bool superAdmin, int esperado)
    {
        var ctx = Contexto(usuario, superAdmin);
        new SomenteSuperAdminAttribute().OnActionExecuting(ctx);
        var r = Assert.IsType<ObjectResult>(ctx.Result);
        Assert.Equal(esperado, r.StatusCode);
    }

    [Fact]
    public void GestaoDeixaSuperAdminPassarSemCache()
    {
        var ctx = Contexto(1, true);
        new SomenteSuperAdminAttribute().OnActionExecuting(ctx);
        Assert.Null(ctx.Result);
        Assert.Equal("no-store", ctx.HttpContext.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public void TodaRotaDaGestaoPassaPeloFiltroENenhumaEAnonima()
    {
        var tipo = typeof(GestaoPagamentosController);
        Assert.NotNull(tipo.GetCustomAttribute<SomenteSuperAdminAttribute>());
        Assert.Null(tipo.GetCustomAttribute<AllowAnonymousAttribute>());
        var acoes = tipo.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).ToList();
        Assert.True(acoes.Count >= 18);
        Assert.All(acoes, m => Assert.Null(m.GetCustomAttribute<AllowAnonymousAttribute>()));
        // As rotas do contrato (§8.1) existem.
        var rotas = acoes.SelectMany(m => m.GetCustomAttributes<Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute>()).Select(a => a.Template).ToHashSet();
        foreach (var r in new[] { "configuracao", "verificar", "conexoes", "transacoes", "assinaturas", "reembolsos", "webhooks", "reconciliar", "politicas", "auditoria" })
            Assert.Contains(r, rotas);
    }

    [Theory]
    [InlineData("dono", "", false, true)]
    [InlineData("gerente_empresa", "", false, true)]
    [InlineData("", "gerente_estabelecimento", false, true)]
    [InlineData("", "gerente", false, true)]
    [InlineData("", "atendente", false, false)]
    [InlineData("colaborador", "funcionario", false, false)]
    [InlineData("", "", true, true)]
    public void SoDonoOuGerenteGerenciaAConexao(string empresa, string loja, bool superAdmin, bool pode)
    {
        var http = new DefaultHttpContext();
        http.Items["UserId"] = 9;
        http.Items["IsSuperAdmin"] = superAdmin;
        http.Items["TipoAcessoEmpresa"] = empresa;
        http.Items["TipoAcesso"] = loja;
        Assert.Equal(pode, ConexaoMercadoPagoAcesso.PodeGerenciar(http));
    }

    [Fact]
    public void ApenasOCallbackEAnonimo()
    {
        Assert.Null(typeof(MercadoPagoIntegracaoController).GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.NotNull(typeof(MercadoPagoCallbackController).GetCustomAttribute<AllowAnonymousAttribute>());
    }

    private static ConfiguracaoDto Config(Action<ConfiguracaoDto>? ajuste = null)
    {
        var c = new ConfiguracaoDto
        {
            AmbienteAtivo = "sandbox", Ambiente = "sandbox", ChaveMestraConfigurada = true,
            Aplicacao = new AplicacaoDto { Origem = "banco", TemClientSecret = true, TemWebhookSecret = true, ClientId = "1", RedirectUri = "https://x" },
            ContaZippy = new ContaZippyDto { TemToken = true, Estado = "ativa" }
        };
        ajuste?.Invoke(c);
        return c;
    }

    [Fact]
    public void AssistenteNaoDizConectadoSoPorqueExisteChave()
    {
        var etapas = PagamentosGestaoService.MontarEtapas(Config());
        Assert.Equal(8, etapas.Count);
        Assert.Equal("ok", etapas[0].Status);
        // Com todas as chaves, mas sem verificacao real: nada alem da etapa 1 esta ok.
        Assert.All(etapas.Skip(1), e => Assert.NotEqual("ok", e.Status));
        Assert.All(etapas.Where(e => e.Status != "ok"), e => Assert.False(string.IsNullOrWhiteSpace(e.Falta)));
    }

    private static VerificacaoDto Verificacao(bool desatualizada, params string[] falhas)
    {
        var nomes = new[] { "chave_mestra", "aplicacao", "callback", "webhook_segredo", "recebedor_zippy", "recebedor_pix", "webhook_recebido" };
        return new VerificacaoDto
        {
            Aprovada = falhas.Length == 0, Desatualizada = desatualizada,
            Etapas = nomes.Select(n => new EtapaVerificacao { Etapa = n, Ok = !falhas.Contains(n), Mensagem = falhas.Contains(n) ? $"falta {n}" : "ok" }).ToList()
        };
    }

    [Fact]
    public void AssistenteMostraExatamenteOQueFalta()
    {
        var etapas = PagamentosGestaoService.MontarEtapas(Config(c => c.UltimaVerificacao = Verificacao(false, "recebedor_pix")));
        Assert.Equal("ok", etapas[1].Status);
        Assert.Equal("erro", etapas[2].Status);
        Assert.Equal("falta recebedor_pix", etapas[2].Falta);
        Assert.Equal("bloqueada", etapas[5].Status);

        // Verificacao aprovada, mas credencial trocada depois: volta a exigir verificar.
        var velha = PagamentosGestaoService.MontarEtapas(Config(c => c.UltimaVerificacao = Verificacao(true)));
        Assert.NotEqual("ok", velha[1].Status);

        var pronto = PagamentosGestaoService.MontarEtapas(Config(c =>
        {
            c.UltimaVerificacao = Verificacao(false);
            c.AmbienteEstado = new AmbienteEstadoDto { Habilitado = true, LojasPodemConectar = true };
        }));
        Assert.All(pronto, e => Assert.Equal("ok", e.Status));
    }

    [Theory]
    [InlineData("https://api.zippy.com.br/x", true)]
    [InlineData("http://localhost:5000/x", true)]
    [InlineData("http://api.zippy.com.br/x", false)]
    [InlineData("javascript:alert(1)", false)]
    public void UrlsDoMercadoPagoPrecisamDeHttps(string url, bool ok) => Assert.Equal(ok, PagamentosGestaoService.UrlSegura(url));
}
