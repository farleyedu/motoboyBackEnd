using System.Text.Json.Nodes;
using APIBack.Mesas.Nucleo;
using APIBack.Mesas.Pedidos;
using APIBack.Mesas.Sessao;
using Xunit;
using R = APIBack.Mesas.Pedidos.PedidosRegras;

namespace APIBack.Tests.Unit.Mesas.Pedidos;

/// <summary>Regras puras do P08: roteamento, aprovação, quem leva, autonomia, passos do item, divisão e cartões da cozinha.</summary>
public sealed class PedidosRegrasTests
{
    private static readonly Guid Picanha = Guid.NewGuid(), Chope = Guid.NewGuid(), CatBebidas = Guid.NewGuid(), CatPratos = Guid.NewGuid();

    private static JsonObject Cfg(string? json = null) =>
        MesasConfigRegras.Efetiva(json is null ? null : (JsonObject)JsonNode.Parse(json)!);

    private static JsonObject CfgBar() => Cfg($$"""{ "rotas": { "categorias": { "{{CatBebidas}}": "bar", "{{CatPratos}}": "cozinha" } } }""");

    [Fact]
    public void Rotear_sem_regra_vai_para_producao_geral_e_categoria_manda_para_o_setor()
    {
        Assert.Equal("producao_geral", R.Rotear(Cfg(), Picanha, CatPratos).Single().EstacaoId);
        Assert.Equal("bar", R.Rotear(CfgBar(), Chope, CatBebidas).Single().EstacaoId);
        Assert.Equal("cozinha", R.Rotear(CfgBar(), Picanha, CatPratos).Single().EstacaoId);
    }

    [Fact]
    public void Estacao_desligada_ou_setor_que_nao_existe_cai_na_producao_geral()
    {
        // chapa vem desligada no padrão
        var cfg = Cfg($$"""{ "rotas": { "produtos": { "{{Picanha}}": "chapa" } } }""");
        Assert.Equal("producao_geral", R.Rotear(cfg, Picanha, CatPratos)[0].EstacaoId);
        var semBar = Cfg($$"""{ "preparo": { "bar": { "existe": false } }, "rotas": { "categorias": { "{{CatBebidas}}": "bar" } } }""");
        Assert.Equal("producao_geral", R.Rotear(semBar, Chope, CatBebidas)[0].EstacaoId);
    }

    [Fact]
    public void Setor_sem_painel_continua_recebendo_o_item_e_marca_sem_painel()
    {
        var cfg = Cfg($$"""{ "preparo": { "bar": { "existe": true, "painel": false, "via": "garcom" } }, "rotas": { "categorias": { "{{CatBebidas}}": "bar" } } }""");
        Assert.Equal("bar", R.Rotear(cfg, Chope, CatBebidas)[0].EstacaoId);
        Assert.False(R.TemPainel(cfg, "bar"));
        Assert.True(R.TemPainel(cfg, "cozinha"));
        Assert.True(R.TemPainel(cfg, "producao_geral"));
    }

    [Fact]
    public void Etapas_do_produto_vencem_e_passam_pela_estacao_valida()
    {
        var cfg = Cfg($$"""{ "rotas": { "inativas": { "chapa": false }, "etapas": { "{{Picanha}}": [ { "estacaoId": "chapa", "nome": "Grelhar" }, { "estacaoId": "cozinha", "nome": "Montar" } ] } } }""");
        var e = R.Rotear(cfg, Picanha, CatPratos);
        Assert.Equal(new[] { "chapa", "cozinha" }, e.Select(x => x.EstacaoId));
        Assert.Equal("Grelhar", e[0].Nome);
    }

    [Theory]
    [InlineData("garcom", "cozinha", 50, false)]          // a equipe lança: já aprovado
    [InlineData("cliente", "bar", 50, false)]            // r1: bebida do cliente vai direto
    [InlineData("cliente", "cozinha", 50, true)]         // r2: pedido do celular passa pelo garçom
    [InlineData("gestao", "cozinha", 200, false)]
    public void Aprovacao_por_regras_primeira_que_casa_decide(string origem, string estacao, decimal preco, bool exige)
    {
        Assert.Equal(exige, R.RegraAprovacao(Cfg(), origem, estacao, CatPratos, Picanha, preco).Exige);
    }

    [Fact]
    public void Aprovacao_modos_direto_e_garcom()
    {
        Assert.False(R.RegraAprovacao(Cfg("""{ "aprovacao": { "modo": "direto" } }"""), "cliente", "cozinha", null, Picanha, 10).Exige);
        Assert.True(R.RegraAprovacao(Cfg("""{ "aprovacao": { "modo": "garcom" } }"""), "cliente", "bar", null, Picanha, 10).Exige);
    }

    [Fact]
    public void Quem_leva_garcom_cliente_depende_e_config_antiga()
    {
        Assert.Equal("garcom", R.QuemLeva(Cfg(), Chope, "bar", false));
        Assert.Equal("cliente", R.QuemLeva(Cfg("""{ "retirada": { "modo": "balcao" } }"""), Chope, "bar", false));
        var depende = Cfg($$"""{ "retirada": { "modo": "depende", "estacoes": { "bar": "cliente" }, "produtos": { "{{Chope}}": "garcom" } } }""");
        Assert.Equal("garcom", R.QuemLeva(depende, Chope, "bar", false));           // produto vence a estação
        Assert.Equal("cliente", R.QuemLeva(depende, Guid.NewGuid(), "bar", false));
        Assert.Equal("cliente", R.QuemLeva(Cfg("""{ "retirada": { "modo": null, "ativa": true, "estacoes": { "bar": true } } }"""), Chope, "bar", false));
        Assert.Equal("cliente", R.QuemLeva(Cfg("""{ "retirada": { "clienteEscolhe": true } }"""), Chope, "bar", true));
        Assert.Equal("garcom", R.QuemLeva(Cfg(), Chope, "bar", true));              // escolher buscar só vale se a casa deixa
    }

    /// <summary>Matriz de autonomia (§5): padrão da loja × papel × motivo.</summary>
    [Theory]
    [InlineData("garcom", "editarAntes", false, SessaoRegras.Autonomia.Pode)]
    [InlineData("garcom", "cancelarAntes", false, SessaoRegras.Autonomia.ExigeMotivo)]
    [InlineData("garcom", "cancelarAntes", true, SessaoRegras.Autonomia.Pode)]
    [InlineData("garcom", "itemDepois", true, SessaoRegras.Autonomia.PedeGerente)]
    [InlineData("garcom", "pratoVoltou", false, SessaoRegras.Autonomia.ExigeMotivo)]
    [InlineData("garcom", "cortesia", true, SessaoRegras.Autonomia.PedeGerente)]
    [InlineData("garcom", "itemOutraMesa", false, SessaoRegras.Autonomia.Pode)]
    [InlineData("garcom", "soltarPrincipal", false, SessaoRegras.Autonomia.Pode)]
    [InlineData("gerente", "itemDepois", false, SessaoRegras.Autonomia.Pode)]
    [InlineData("dono", "cortesia", false, SessaoRegras.Autonomia.Pode)]
    public void Matriz_de_autonomia_padrao(string papel, string regra, bool motivo, SessaoRegras.Autonomia esperado)
    {
        var valor = R.Texto(MesasConfigRegras.Ler(Cfg(), "autonomiaGarcom.regras." + regra));
        Assert.Equal(esperado, SessaoRegras.Decidir(papel, valor, motivo));
    }

    [Theory]
    [InlineData("rascunho", "editarAntes", "cancelarAntes")]
    [InlineData("aguardando_aprovacao", "editarAntes", "cancelarAntes")]
    [InlineData("em_fila", "editarAntes", "cancelarAntes")]
    [InlineData("em_preparo", "itemDepois", "itemDepois")]
    [InlineData("pronto", "itemDepois", "itemDepois")]
    [InlineData("entregue", "itemDepois", "itemDepois")]
    public void Antes_ou_depois_da_cozinha_comecar(string estado, string editar, string cancelar)
    {
        Assert.Equal(editar, R.ChaveEditar(estado));
        Assert.Equal(cancelar, R.ChaveCancelar(estado));
    }

    [Fact]
    public void Cortesia_ate_o_limite_o_garcom_da_sozinho()
    {
        Assert.False(R.CortesiaSozinho(Cfg(), 5m));                                          // limite 0: sempre a regra
        var cfg = Cfg("""{ "autonomiaGarcom": { "limiteCortesia": 15 } }""");
        Assert.True(R.CortesiaSozinho(cfg, 15m));
        Assert.False(R.CortesiaSozinho(cfg, 15.01m));
    }

    [Fact]
    public void Passo_pronto_com_duas_etapas_segue_para_a_proxima_estacao_e_depois_fica_pronto()
    {
        var it = new ItemPasso { Estado = "em_preparo", EstacaoId = "chapa", Etapas = { new() { EstacaoId = "chapa" }, new() { EstacaoId = "cozinha" } } };
        R.PassoPronto(it, "garcom");
        Assert.Equal(("em_fila", 1, "cozinha"), (it.Estado, (int)it.EtapaAtual, it.EstacaoId));
        Assert.True(it.Etapas[0].Feita);
        it.Estado = "em_preparo";
        R.PassoPronto(it, "garcom");
        Assert.Equal("pronto", it.Estado);
    }

    [Fact]
    public void Cliente_busca_vira_aguardando_retirada_e_desfazer_volta_um_passo()
    {
        var it = new ItemPasso { Estado = "em_fila", EstacaoId = "bar", Etapas = { new() { EstacaoId = "bar" } } };
        R.PassoPronto(it, "cliente");
        Assert.Equal(("aguardando_retirada", "bar"), (it.Estado, it.RetiradaEstacao));
        R.Reverter(it, "em_fila", 0);
        Assert.Equal("em_fila", it.Estado);
        Assert.False(it.Etapas[0].Feita);
        Assert.Null(it.RetiradaEstacao);
    }

    [Fact]
    public void Tudo_pronto_fecha_todas_as_etapas()
    {
        var it = new ItemPasso { Estado = "em_fila", EstacaoId = "chapa", Etapas = { new() { EstacaoId = "chapa" }, new() { EstacaoId = "cozinha" } } };
        R.TudoPronto(it, "garcom");
        Assert.Equal("pronto", it.Estado);
        Assert.All(it.Etapas, e => Assert.True(e.Feita));
    }

    [Theory]
    [InlineData(10, 1)]
    [InlineData(10, 3)]
    [InlineData(89.90, 7)]
    [InlineData(0.01, 3)]
    public void Divisao_do_item_soma_100_por_cento_e_o_total(double total, int partes)
    {
        var t = (decimal)total;
        var p = R.Repartir(t, partes);
        Assert.Equal(100m, p.Sum(x => x.Percentual));
        Assert.Equal(Math.Round(t, 2), p.Sum(x => x.Valor));
        Assert.All(p, x => Assert.True(x.Valor >= 0));
    }

    [Theory]
    [InlineData(18, 18, "verde")]
    [InlineData(27, 18, "amarelo")]
    [InlineData(28, 18, "vermelho")]
    [InlineData(8, 5, "vermelho")]
    public void Cor_pelo_tempo_esperado_do_prato(int min, int esperado, string cor) => Assert.Equal(cor, R.Cor(min, esperado));

    [Fact]
    public void Pedido_misto_vira_um_cartao_na_cozinha_e_outro_no_bar_e_o_principal_espera_a_entrada()
    {
        var agora = DateTime.UtcNow;
        var ped = Guid.NewGuid();
        CozinhaLinha L(string nome, string estacao, string tipo) => new()
        {
            ItemId = Guid.NewGuid(), PedidoId = ped, MesaNumero = "12", Nome = nome, EstacaoId = estacao, TipoPrato = tipo, Estado = "em_fila",
            Qtd = 1, CriadoEm = agora.AddMinutes(-3), TFila = agora.AddMinutes(-3), PedidoCriadoEm = agora.AddMinutes(-3), SegurarPrincipal = true
        };
        var linhas = new[] { L("Bruschetta", "cozinha", "entrada"), L("Picanha", "cozinha", "principal"), L("Chope", "bar", "outro") };
        var cozinha = CozinhaRegras.Cartoes(linhas, "cozinha", agora, Cfg());
        var bar = CozinhaRegras.Cartoes(linhas, "bar", agora, Cfg());
        Assert.Single(cozinha);
        Assert.Single(bar);
        Assert.Equal(2, cozinha[0].Itens.Count);
        Assert.Equal("novos", cozinha[0].Coluna);
        Assert.Contains(cozinha[0].Itens, i => i.Nome == "Picanha" && i.EsperandoEntrada);
        Assert.Equal("bar", cozinha[0].Outros.Single().EstacaoId);
        Assert.False(bar[0].Itens[0].EsperandoEntrada);

        // A entrada saiu: o principal não espera mais.
        linhas[0].Estado = "pronto"; linhas[0].TPronto = agora;
        Assert.DoesNotContain(CozinhaRegras.Cartoes(linhas, "cozinha", agora, Cfg())[0].Itens, i => i.EsperandoEntrada);
    }

    [Fact]
    public void Pronto_separa_garcom_leva_de_cliente_busca_e_vermelho_sobe()
    {
        var agora = DateTime.UtcNow;
        var p1 = Guid.NewGuid(); var p2 = Guid.NewGuid(); var p3 = Guid.NewGuid();
        var linhas = new List<CozinhaLinha>
        {
            new() { ItemId = Guid.NewGuid(), PedidoId = p1, Nome = "Chope", EstacaoId = "bar", Estado = "aguardando_retirada", Qtd = 1, CriadoEm = agora, TPronto = agora },
            new() { ItemId = Guid.NewGuid(), PedidoId = p1, Nome = "Caipirinha", EstacaoId = "bar", Estado = "pronto", Qtd = 1, CriadoEm = agora, TPronto = agora },
            new() { ItemId = Guid.NewGuid(), PedidoId = p2, ProdutoId = Guid.NewGuid(), Nome = "Suco", EstacaoId = "bar", Estado = "em_fila", Qtd = 1, CriadoEm = agora.AddMinutes(-2), TFila = agora.AddMinutes(-2) },
            new() { ItemId = Guid.NewGuid(), PedidoId = p3, ProdutoId = Guid.NewGuid(), Nome = "Água", EstacaoId = "bar", Estado = "em_fila", Qtd = 1, CriadoEm = agora.AddMinutes(-1), TFila = agora.AddMinutes(-9) },
        };
        var c = CozinhaRegras.Cartoes(linhas, "bar", agora, Cfg());
        Assert.Equal(p3, c[0].PedidoId);                       // 9 min no bar (5 esperado) = vermelho, no topo
        Assert.Contains(c, x => x.PedidoId == p1 && x.Faixa == "garcom");
        Assert.Contains(c, x => x.PedidoId == p1 && x.Faixa == "cliente");
        Assert.Equal(new[] { 1m, 1m }, CozinhaRegras.Total(linhas, "bar").Select(t => t.Qtd));
    }

    [Theory]
    [InlineData("entradas", "entrada")]
    [InlineData("Pratos principais", "principal")]
    [InlineData("pizzas", "principal")]
    [InlineData("bebidas", "outro")]
    public void Tipo_do_prato_pela_categoria(string slug, string tipo) => Assert.Equal(tipo, R.TipoPrato(slug));

    [Theory]
    [InlineData("garcom", "WGD", true)]
    [InlineData("cozinha", "WGD", false)]
    [InlineData("cozinha", "KGD", true)]
    [InlineData("caixa", "WGD", false)]
    [InlineData("caixa", "WGDX", true)]
    public void Papeis_por_comando(string papel, string letras, bool pode) => Assert.Equal(pode, R.Pode(papel, letras));
}
