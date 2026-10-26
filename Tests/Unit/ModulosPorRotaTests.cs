using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using APIBack.Middleware;
using APIBack.Service;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace APIBack.Tests.Unit;

public class ModulosPorRotaTests
{
    private static readonly Guid Store = Guid.NewGuid();

    [Theory]
    [InlineData("/api/v2/delivery/ifood/alerts", "iFood")]
    [InlineData("/api/v2/ifood/store/connection", "iFood")]
    [InlineData("/api/v2/delivery/impressao/estacoes", "Impressao")]
    [InlineData("/api/v2/delivery/rider-work/resumo", "Remuneracao")]
    [InlineData("/api/v2/delivery/cardapio-pedidos", "CardapioWeb")]
    [InlineData("/api/v2/simulador/pedidos", "Simulador")]
    [InlineData("/api/delivery/simulator/start", "Simulador")]
    [InlineData("/api/v2/delivery/pedidos", "Pedidos")]
    [InlineData("/api/v2/delivery/pedidos/versao", "Pedidos")]
    [InlineData("/api/pedido", "Pedidos")]
    [InlineData("/api/v2/clientes", "Clientes")]
    [InlineData("/api/v2/inteligencia/ingredientes", "Inteligencia")]
    [InlineData("/api/crm/financeiro", "Financeiro")]
    [InlineData("/api/v2/atendimento/canal-central", "WhatsApp")]
    [InlineData("/api/cardapio/9f1c4c5e-8a42-4a76-9a39-111111111111/produtos", "Cardapio")]
    [InlineData("/api/configuracoes/servicos", "Servicos")]
    [InlineData("/api/configuracoes/9f1c4c5e-8a42-4a76-9a39-111111111111/faq", "FAQ")]
    [InlineData("/api/configuracoes/disponibilidade/config", "Disponibilidade")]
    public void Familias_de_rota_exigem_o_modulo_da_loja(string path, string modulo) =>
        Assert.Equal(modulo, ModulosPorRota.Exigido(path));

    [Theory]
    [InlineData("/api/auth/me")]
    [InlineData("/api/me/estabelecimentos")]
    [InlineData("/api/gestao/estabelecimentos")]
    [InlineData("/api/admin/usuarios")]
    [InlineData("/api/public/rastreio/abc")]
    [InlineData("/api/webhooks/asaas")]
    [InlineData("/api/v2/comercial/situacao")]
    [InlineData("/api/v2/motoboys/me/session/queue")]
    [InlineData("/api/motoboys/me/work")]
    [InlineData("/api/v2/atendimento/chat/threads")]
    [InlineData("/api/cardapio/web/qualquer-loja")]
    [InlineData("/api/cardapio/9f1c4c5e-8a42-4a76-9a39-111111111111/atencao-motoboy")]
    [InlineData("/api/configuracoes/estabelecimento")]
    [InlineData("/wa/webhook")]
    [InlineData("/hubs/delivery")]
    [InlineData("")]
    public void Rotas_fora_da_tabela_nao_exigem_modulo(string path) =>
        Assert.Null(ModulosPorRota.Exigido(path));

    // Familias que ficam abertas de proposito (acesso ao sistema, gestao Zippy, app do motoboy, webhooks/publicas, configuracao
    // da loja, ou que ja tem [RequireModule]/[RequirePermission] proprios). Controller novo que nao cair em ModulosPorRota nem aqui
    // falha este teste: decida o modulo (ModulosPorRota) ou registre a rota aqui com o motivo.
    private static readonly string[] AbertasDeProposito =
    {
        "wa", "api/auth", "api/estabelecimentos", "api/gestao", "api/admin", "api/usuario", "api/public", "api/webhooks",
        "api/v2/comercial", "api/v2/motoboys", "api/motoboys", "api/motoboy", "api/v2/atendimento/chat",
        "api/migration", "api/localizacao", "api/entregas", "api/v2/pagamentos", "api/configuracoes", "api/delivery",
        "api/cardapio/web", "api/cardapio/{estabelecimentoid:guid}/atencao-motoboy", "api/v2/ifood/config"
    };

    [Fact]
    public void Todo_controller_tem_modulo_na_tabela_ou_esta_aberto_de_proposito()
    {
        var sem = new List<string>();
        var total = 0;
        foreach (var type in typeof(ModulosPorRota).Assembly.GetTypes().Where(t => typeof(Microsoft.AspNetCore.Mvc.ControllerBase).IsAssignableFrom(t) && !t.IsAbstract))
        {
            var nome = type.Name.EndsWith("Controller") ? type.Name[..^"Controller".Length] : type.Name;
            var rotas = type.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.RouteAttribute), true).Cast<Microsoft.AspNetCore.Mvc.RouteAttribute>().Select(r => r.Template)
                .Concat(type.GetMethods().SelectMany(m => m.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute), true)
                    .Cast<Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute>().Select(h => h.Template ?? string.Empty).Where(t => t.StartsWith("api/", StringComparison.OrdinalIgnoreCase))))
                .Select(t => t.Replace("[controller]", nome, StringComparison.OrdinalIgnoreCase).Trim('/').ToLowerInvariant())
                .Where(t => t.Length > 0 && t != "api");
            foreach (var rota in rotas.Distinct())
            {
                total++;
                if (ModulosPorRota.Exigido("/" + rota) is null && !AbertasDeProposito.Any(a => rota == a || rota.StartsWith(a + "/", StringComparison.Ordinal)))
                    sem.Add($"{type.Name}: /{rota}");
            }
        }
        Assert.True(total > 40, $"A varredura de controllers achou so {total} rotas: o teste ficou vazio.");
        Assert.True(sem.Count == 0, "Rotas sem modulo definido (ModulosPorRota) nem liberacao explicita:\n" + string.Join("\n", sem));
    }

    [Theory]
    [InlineData(null, "warn")]
    [InlineData("", "warn")]
    [InlineData("qualquer", "warn")]
    [InlineData("OFF", "off")]
    [InlineData(" Enforce ", "enforce")]
    public void Modo_desconhecido_cai_em_warn(string? configurado, string esperado) =>
        Assert.Equal(esperado, ModulosPorRota.Modo(configurado));

    [Fact]
    public async Task Enforce_bloqueia_loja_sem_o_modulo_com_o_mesmo_contrato_do_atributo()
    {
        var (context, nextCalled) = await Run("/api/v2/delivery/ifood/alerts", "enforce", new[] { "Pedidos" });
        Assert.False(nextCalled);
        Assert.Equal(403, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        using var json = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal("MODULO_INATIVO", json.RootElement.GetProperty("code").GetString());
        Assert.Equal("iFood", json.RootElement.GetProperty("modulo").GetString());
    }

    [Fact]
    public async Task Warn_so_registra_e_deixa_passar()
    {
        var (context, nextCalled) = await Run("/api/v2/delivery/ifood/alerts", "warn", new[] { "Pedidos" });
        Assert.True(nextCalled);
        Assert.Equal(200, context.Response.StatusCode);
    }

    [Fact]
    public async Task Off_desliga_a_checagem()
    {
        var (_, nextCalled) = await Run("/api/v2/delivery/ifood/alerts", "off", Array.Empty<string>());
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task Loja_com_o_modulo_passa_mesmo_em_enforce()
    {
        var (_, nextCalled) = await Run("/api/v2/delivery/ifood/alerts", "enforce", new[] { "Pedidos", "iFood" });
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task Loja_em_teste_tem_tudo_liberado()
    {
        var (_, nextCalled) = await Run("/api/v2/delivery/ifood/alerts", "enforce", Array.Empty<string>(), SituacoesComerciais.Teste);
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task Super_admin_nao_tem_privilegio_e_chamada_sem_loja_passa()
    {
        // Super admin com uma loja no contexto recebe a mesma trava do modulo que qualquer usuario.
        var (_, superAdmin) = await Run("/api/v2/delivery/ifood/alerts", "enforce", Array.Empty<string>(), superAdmin: true);
        Assert.False(superAdmin);
        var (_, semLoja) = await Run("/api/v2/delivery/ifood/alerts", "enforce", Array.Empty<string>(), store: false);
        Assert.True(semLoja);
    }
    private static async Task<(DefaultHttpContext Context, bool NextCalled)> Run(
        string path, string modo, string[] modulos, string situacao = SituacoesComerciais.Ativo, bool superAdmin = false, bool store = true)
    {
        var context = new DefaultHttpContext();
        if (store) context.Items["EstabelecimentoId"] = Store;
        context.Items["EstabelecimentoModulosAtivos"] = new List<string>(modulos);
        if (superAdmin) context.Items["IsSuperAdmin"] = true;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        var nextCalled = false;
        var middleware = new ModuloPorRotaMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
        var service = new Mock<ISituacaoComercialService>();
        service.Setup(s => s.ObterAsync(Store, context.RequestAborted))
            .ReturnsAsync(new SituacaoComercialDto(Store, situacao, null, null, null, 3, 3, 3, 10, null, null));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ModuleEnforcement"] = modo }).Build();
        await middleware.InvokeAsync(context, service.Object, configuration, NullLogger<ModuloPorRotaMiddleware>.Instance);
        return (context, nextCalled);
    }
}
