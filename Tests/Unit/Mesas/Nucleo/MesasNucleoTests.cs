using System.Text.Json.Nodes;
using APIBack.Attributes;
using APIBack.Controllers.Mesas;
using APIBack.Mesas.Nucleo;
using APIBack.Model.Gestao;
using APIBack.Security;
using APIBack.Service;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace APIBack.Tests.Unit.Mesas.Nucleo;

public class MesasPermissoesTests
{
    private static Func<string, string, bool> Tem(params string[] acoes) => (modulo, acao) => modulo == "Mesas" && acoes.Contains(acao);

    [Theory]
    [InlineData("garcom", "garcom")]
    [InlineData("gerente_salao", "gerente")]
    [InlineData("cozinha", "cozinha")]
    [InlineData("bar", "cozinha")]
    [InlineData("caixa", "caixa")]
    [InlineData(null, null)]
    public void Funcao_da_equipe_vira_papel(string? funcao, string? papel) =>
        Assert.Equal(papel, MesasPermissoes.Papel(Tem(), false, "", funcao));

    [Fact]
    public void Dono_e_gerente_do_painel_vencem_a_funcao()
    {
        Assert.Equal("dono", MesasPermissoes.Papel(Tem(), false, "dono", "garcom"));
        Assert.Equal("dono", MesasPermissoes.Papel(Tem(), true, "", null));
        Assert.Equal("gerente", MesasPermissoes.Papel(Tem("configurar"), false, "", "garcom"));
        Assert.Equal("caixa", MesasPermissoes.Papel(Tem("caixa"), false, "", null));
    }

    [Theory]
    [InlineData("garcom", "ver", true)]
    [InlineData("garcom", "operar", true)]
    [InlineData("garcom", "caixa", true)]
    [InlineData("garcom", "cozinha", false)]
    [InlineData("garcom", "gerir", false)]
    [InlineData("garcom", "configurar", false)]
    [InlineData("cozinha", "cozinha", true)]
    [InlineData("cozinha", "operar", false)]
    [InlineData("cozinha", "reservas", false)]
    [InlineData("caixa", "caixa", true)]
    [InlineData("caixa", "gerir", false)]
    [InlineData("gerente", "configurar", true)]
    [InlineData("gerente", "gerir", true)]
    [InlineData("dono", "configurar", true)]
    [InlineData(null, "ver", false)]
    [InlineData("gerente", "inventada", false)]
    public void Tabela_de_permissoes(string? papel, string acao, bool pode) => Assert.Equal(pode, MesasPermissoes.Pode(papel, acao));

    [Fact]
    public void Ligar_o_modulo_e_so_do_dono()
    {
        Assert.True(MesasPermissoes.PodeLigar("dono"));
        Assert.False(MesasPermissoes.PodeLigar("gerente"));
        Assert.False(MesasPermissoes.PodeLigar("garcom"));
    }

    [Fact]
    public void Catalogo_de_permissoes_tem_o_modulo_Mesas() =>
        Assert.Equal(MesasPermissoes.Acoes, EstablishmentPermissions.Catalog()["Mesas"]);
}

public class MesasModuloTests
{
    [Theory]
    [InlineData("/api/v2/mesas/config", "Mesas")]
    [InlineData("/api/v2/mesas/equipe/1/funcao", "Mesas")]
    [InlineData("/api/v2/equipe/ponto", null)]
    [InlineData("/api/me/funcoes", null)]
    [InlineData("/api/public/mesas/abc", null)]
    public void Rota_exige_o_modulo(string path, string? modulo) => Assert.Equal(modulo, ModulosPorRota.Exigido(path));

    [Fact]
    public void Modulo_Mesas_vai_e_volta_do_banco()
    {
        Assert.Contains("MESAS", EstabelecimentoModuleMapper.ToDatabaseModules(new[] { "Mesas" }, "restaurante"));
        Assert.Contains("Mesas", EstabelecimentoModuleMapper.ToUiModules("Loja", new[] { "MESAS" }));
    }

    [Fact]
    public void Loja_sem_o_modulo_recebe_MODULO_INATIVO()
    {
        var atributo = typeof(MesasConfigController).GetCustomAttributes(typeof(RequireModuleAttribute), true)
            .Cast<RequireModuleAttribute>().Single(a => a.Modulo == "Mesas");

        static AuthorizationFilterContext Contexto(params string[] modulos)
        {
            var http = new DefaultHttpContext();
            http.Items["EstabelecimentoModulosAtivos"] = modulos.ToList();
            return new AuthorizationFilterContext(new ActionContext(http, new RouteData(), new ActionDescriptor()), new List<IFilterMetadata>());
        }

        var sem = Contexto("Pedidos", "Cardapio");
        atributo.OnAuthorization(sem);
        var resultado = Assert.IsType<JsonResult>(sem.Result);
        Assert.Equal(403, resultado.StatusCode);
        Assert.Contains("MODULO_INATIVO", System.Text.Json.JsonSerializer.Serialize(resultado.Value));

        var com = Contexto("Pedidos", "Mesas");
        atributo.OnAuthorization(com);
        Assert.Null(com.Result);
    }
}

public class MesasConfigRegrasTests
{
    [Fact]
    public void Padroes_do_contrato()
    {
        var c = MesasConfigRegras.Efetiva(null);
        Assert.Equal("regras", MesasConfigRegras.Ler(c, "aprovacao.modo")!.GetValue<string>());
        Assert.Equal(4, MesasConfigRegras.Ler(c, "aprovacao.escalonarMin")!.GetValue<int>());
        Assert.Equal(18, MesasConfigRegras.Ler(c, "estacoes.cozinha")!.GetValue<int>());
        Assert.Equal(20, MesasConfigRegras.Ler(c, "servico.distribuicao.retencaoPct")!.GetValue<int>());
        Assert.Equal(100, MesasConfigRegras.Ler(c, "gorjeta.repasse.diretoPct")!.GetValue<int>());
        Assert.Equal("gerente", MesasConfigRegras.Ler(c, "autonomiaGarcom.regras.desconto")!.GetValue<string>());
        Assert.Equal(4, MesasConfigRegras.Ler(c, "taxas")!.AsArray().Count);
        Assert.Null(MesasConfigRegras.Ler(c, "aprovacao.escalonarPara"));
    }

    [Fact]
    public void Gravado_mescla_com_o_padrao()
    {
        var gravado = new JsonObject();
        MesasConfigRegras.Gravar(gravado, "gorjeta.sugestoes", new JsonArray(10, 12));
        var c = MesasConfigRegras.Efetiva(gravado);
        Assert.Equal("[10,12]", MesasConfigRegras.Ler(c, "gorjeta.sugestoes")!.ToJsonString());
        Assert.True(MesasConfigRegras.Ler(c, "gorjeta.ativa")!.GetValue<bool>());
        Assert.Equal("{\"gorjeta\":{\"sugestoes\":[10,12]}}", gravado.ToJsonString());
    }

    [Theory]
    [InlineData("aprovacao.modo", "\"direto\"")]
    [InlineData("filaCliente", "\"nada\"")]
    [InlineData("alertas.atrasoMin", "0")]
    [InlineData("estacoes.pizzaria", "12")]
    [InlineData("rotas.nomes", "{\"forno\":\"Pizzaria\"}")]
    [InlineData("aprovacao.escalonarPara", "\"g-marina\"")]
    [InlineData("reservas.mesasReservaveis", "[\"m1\",\"m2\"]")]
    [InlineData("gorjeta.repasse.fundo.funcoes", "[\"garcom\",\"cozinha\"]")]
    [InlineData("taxas", "[{\"id\":\"tx-servico\",\"tipo\":\"servico\",\"rotulo\":\"Serviço\",\"percentual\":12,\"ativa\":true,\"quemLibera\":\"garcom\"}]")]
    [InlineData("autonomiaGarcom.regras.desconto", "\"motivo\"")]
    [InlineData("entrega", "{\"fechaSozinho\":false,\"minutos\":30}")]
    [InlineData("ativo", "true")]
    public void Valores_validos(string chave, string json) => Assert.Null(MesasConfigRegras.Validar(chave, JsonNode.Parse(json)));

    [Theory]
    [InlineData("", "1")]
    [InlineData("inventada", "1")]
    [InlineData("aprovacao.inventada", "1")]
    [InlineData("aprovacao.modo", "\"qualquer\"")]
    [InlineData("aprovacao.modo", "3")]
    [InlineData("alertas.atrasoMin", "-1")]
    [InlineData("servico.distribuicao.retencaoPct", "40")]
    [InlineData("gorjeta.repasse.fundo.funcoes", "[\"motoboy\"]")]
    [InlineData("autonomiaGarcom.regras.desconto", "\"talvez\"")]
    [InlineData("entrega", "{\"fechaSozinho\":false,\"extra\":1}")]
    [InlineData("pedidoPeloCelular", "\"sim\"")]
    [InlineData("ativo", "\"sim\"")]
    public void Valores_invalidos(string chave, string json) => Assert.NotNull(MesasConfigRegras.Validar(chave, JsonNode.Parse(json)));
}
