using APIBack.Acontecimentos;
using APIBack.GestaoLogica.Catalogo;
using APIBack.GestaoLogica.Compras;
using APIBack.GestaoLogica.Financeiro;
using Xunit;

namespace APIBack.Tests.Unit;

/// <summary>Integração 6B — Catálogo e Financeiro: regras puras (finalidade, conversão, de-para, compra à mão, a pagar).</summary>
public class GestaoCatalogoRegrasTests
{
    private static readonly DateTimeOffset Out = new(2026, 10, 26, 14, 0, 0, TimeSpan.FromHours(-3));

    [Fact]
    public void Finalidades_sem_vazias_nem_repetidas_por_acento_ou_caixa()
    {
        var f = CatalogoRegras.NormalizarFinalidades(["  Delivery ", "delivery", "", null, "Salão", "salao", "Montagem  da   casa"]);
        Assert.Equal(["Delivery", "Salão", "Montagem da casa"], f);
        Assert.Empty(CatalogoRegras.NormalizarFinalidades(null));
    }

    [Theory]
    [InlineData("un", "un")]
    [InlineData("kg", "g")]
    [InlineData("L", "ml")]
    [InlineData("ml", "ml")]
    [InlineData("cx", null)]
    public void Unidade_da_tela_vira_unidade_de_estoque(string tela, string? estoque) =>
        Assert.Equal(estoque, CatalogoRegras.UnidadeBaseDe(tela));

    [Theory]
    [InlineData("1 PC = 2 kg", "g", 2000)]
    [InlineData("1 CX = 12 un", "un", 12)]
    [InlineData("2 PCT = 100 un", "un", 50)]
    [InlineData("1 FD = 1,5 L", "ml", 1500)]
    [InlineData("900 ml", "ml", 900)]
    public void Conversao_do_cupom_vira_fator(string texto, string unidade, decimal fator) =>
        Assert.Equal(fator, CatalogoRegras.FatorDaConversao(texto, unidade));

    [Theory]
    [InlineData("1 PC = 2 kg", "un")] // unidade do estoque não combina
    [InlineData("um pacote", "g")]
    [InlineData("", "g")]
    public void Conversao_que_nao_da_para_entender_e_null(string texto, string unidade) =>
        Assert.Null(CatalogoRegras.FatorDaConversao(texto, unidade));

    [Fact]
    public void Texto_da_conversao_usa_kg_e_L_quando_cabe()
    {
        Assert.Equal("1 PC = 2 kg", CatalogoRegras.ConversaoTexto("PC", 2000, "g"));
        Assert.Equal("1 PCT = 50 un", CatalogoRegras.ConversaoTexto("PCT", 50, "un"));
        Assert.Equal("1 UN = 900 ml", CatalogoRegras.ConversaoTexto("UN", 900, "ml"));
    }

    [Fact]
    public void De_para_mostra_sem_vinculo_primeiro_conta_usos_por_mes_e_respeita_qualquer_fornecedor()
    {
        var doc1 = Guid.NewGuid();
        var doc2 = Guid.NewGuid();
        var muss = Guid.NewGuid();
        var linhas = new List<CatalogoRegras.LinhaLida>
        {
            new(doc1, 1, Out.AddMonths(-1), "111", "Atacadão", "QJO MUSS FAT 2KG", "QJO MUSS FAT 2KG"),
            new(doc2, 3, Out, "222", "Laticínios Minas", "QJO MUSS FAT 2KG", "QJO MUSS FAT 2KG"),
            new(doc2, 4, Out, "222", "Laticínios Minas", "DESENGORD 500ML", "DESENGORD 500ML"),
            new(doc2, 5, Out, "222", "Laticínios Minas", "SACO LIXO 100L", "SACO LIXO 100L"),
        };
        // Vínculo para qualquer fornecedor (cnpj ''): cobre o mesmo nome dos dois fornecedores.
        var vinculos = new List<CatalogoRegras.VinculoLido>
        {
            new(Guid.NewGuid(), "", "QJO MUSS FAT 2KG", "QJO MUSS FAT 2KG", "PC", muss, "Muçarela", Guid.NewGuid(), "validado", "global",
                "g", 2000, Out.AddMonths(-2), "Marina"),
        };
        var nao = new List<CatalogoRegras.NaoControlarLido> { new("222", "SACO LIXO 100L", "SACO LIXO 100L", Out, "Marina") };

        var dePara = CatalogoRegras.MontarDePara(vinculos, linhas, nao);

        Assert.Equal(3, dePara.Count);
        Assert.Equal("DESENGORD 500ML", dePara[0].NomeNoCupom); // sem vínculo e sem "não controlar": precisa de atenção
        Assert.Null(dePara[0].InsumoId);
        Assert.Equal(CatalogoRegras.IdDeNome("222", "DESENGORD 500ML"), dePara[0].Id);
        Assert.Equal((doc2, 4), (dePara[0].DocumentoId!.Value, dePara[0].Linha!.Value));

        var mussarela = dePara.Single(d => d.InsumoId == muss);
        Assert.Equal(2, mussarela.UsosTotal);
        Assert.Equal(1, mussarela.UsosPorMes["2026-10"]);
        Assert.Equal(1, mussarela.UsosPorMes["2026-09"]);
        Assert.Equal((OrigemCatalogo.Global, CuradoriaLoja.Validado, "1 PC = 2 kg"), (mussarela.Origem, mussarela.Curadoria, mussarela.Conversao));
        Assert.Equal(doc2, mussarela.DocumentoId); // última compra

        Assert.True(dePara.Single(d => d.NomeChave == "SACO LIXO 100L").NaoControlar);
    }

    [Fact]
    public void De_para_mostra_nao_controlar_de_nome_que_nao_apareceu_em_compra()
    {
        var dePara = CatalogoRegras.MontarDePara([], [], [new("", "GAS P13", "GÁS P13", Out, null)]);
        var item = Assert.Single(dePara);
        Assert.True(item.NaoControlar);
        Assert.Equal(0, item.UsosTotal);
    }

    [Fact]
    public void Produto_novo_da_loja_fica_novo_ate_a_curadoria_validar()
    {
        var v = new CatalogoRegras.VinculoLido(Guid.NewGuid(), "111", "TEMPERO CASA", "TEMPERO CASA", "UN", Guid.NewGuid(), "Tempero",
            Guid.NewGuid(), "candidato", "novo", "un", 1, Out, null);
        var d = Assert.Single(CatalogoRegras.MontarDePara([v], [], []));
        Assert.Equal((OrigemCatalogo.Novo, CuradoriaLoja.EmAnalise), (d.Origem, d.Curadoria));
        var validado = Assert.Single(CatalogoRegras.MontarDePara([v with { StatusGlobal = "validado" }], [], []));
        Assert.Equal((OrigemCatalogo.Global, CuradoriaLoja.Validado), (validado.Origem, validado.Curadoria));
    }

    [Fact]
    public void Busca_prefere_quem_comeca_com_o_texto()
    {
        Assert.True(CatalogoRegras.Pontuar("muss", "Mussarela fatiada") > CatalogoRegras.Pontuar("muss", "Queijo mussarela"));
        Assert.Equal(1m, CatalogoRegras.Pontuar("bacon", "Bacon"));
    }

    // ------------------------------------------------------------------ compra à mão

    private static CompraManualRequest Manual(params CompraManualItem[] itens) => new("k1", "Hortifruti do Zé", null,
        new DateOnly(2026, 10, 26), null, itens, FormaCompra.APagar, null, Vencimento: new DateOnly(2026, 11, 10));

    [Fact]
    public void Compra_a_mao_vira_documento_manual_com_total_dos_itens()
    {
        var fuso = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");
        var lido = ComprasManualRegras.Montar(Manual(new CompraManualItem("Tomate", 3.5m, "kg", 7.60m), new CompraManualItem("Alface", 10, null, 3.9m, 1)),
            new DateOnly(2026, 10, 29), fuso);
        Assert.Equal("manual", lido.Origem);
        Assert.Equal(2, lido.Itens.Count);
        Assert.Equal(("KG", 26.60m), (lido.Itens[0].Unidade, lido.Itens[0].ValorTotal));
        Assert.Equal(("UN", 38.00m), (lido.Itens[1].Unidade, lido.Itens[1].ValorTotal));
        Assert.Equal(64.60m, lido.Total);
        Assert.Equal(new DateOnly(2026, 10, 26), DateOnly.FromDateTime(lido.DataEmissao!.Value.UtcDateTime));
        Assert.Null(lido.Fornecedor.Cnpj);
    }

    [Theory]
    [InlineData(1, "2026-10-30", "futuro")]
    [InlineData(0, "2026-10-26", "quantidade")]
    public void Compra_a_mao_invalida_nao_grava_nada(decimal quantidade, string data, string trecho)
    {
        var req = Manual(new CompraManualItem("Tomate", quantidade, "kg", 7.6m)) with { Data = DateOnly.Parse(data) };
        var ex = Assert.Throws<GestaoException>(() => ComprasManualRegras.Montar(req, new DateOnly(2026, 10, 29), TimeZoneInfo.Utc));
        Assert.Contains(trecho, ex.Message);
    }

    [Fact]
    public void Compra_a_mao_paga_exige_conta_e_sem_parcelas()
    {
        var req = Manual(new CompraManualItem("Tomate", 1, "kg", 7.6m)) with { Forma = FormaCompra.Pago };
        Assert.Throws<GestaoException>(() => ComprasManualRegras.ValidarPagamento(req));
        Assert.Throws<GestaoException>(() => ComprasManualRegras.ValidarPagamento(req with { ContaId = Guid.NewGuid(), Parcelas = 2 }));
        ComprasManualRegras.ValidarPagamento(req with { ContaId = Guid.NewGuid() });
    }

    // ------------------------------------------------------------------ financeiro

    private static LancamentoDto Lanc(decimal valor, DateOnly? vencimento, Guid? corrige = null, Guid? id = null) => new(id ?? Guid.NewGuid(),
        Guid.NewGuid(), 1, null, "despesa", "saida", valor, "a_pagar", "a_pagar", new DateOnly(2026, 7, 1), vencimento, null, 1, 1, null,
        "insumos", null, null, corrige, false);

    [Fact]
    public void A_pagar_conta_atrasado_de_qualquer_mes_e_o_liquido_das_correcoes()
    {
        var hoje = new DateOnly(2026, 10, 29);
        var antiga = Guid.NewGuid();
        var lancs = new[]
        {
            Lanc(300, new DateOnly(2026, 7, 10), id: antiga), // atrasada há mais de três meses
            Lanc(-100, null, corrige: antiga),                 // ajuste: ficou 200
            Lanc(50, new DateOnly(2026, 10, 30)),
            Lanc(80, new DateOnly(2026, 11, 20)),               // fora dos 7 dias
        };
        var r = FinanceiroRegras.APagar(lancs, hoje);
        Assert.Equal((200m, 1), (r.Atrasado, r.QuantidadeAtrasada));
        Assert.Equal((50m, 1), (r.Proximos7, r.QuantidadeProximos7));
    }

    [Fact]
    public void Para_onde_foi_agrupa_como_a_tela()
    {
        var r = FinanceiroRegras.ParaOndeFoi([
            new() { Valor = 186.40m, Categoria = "insumos", Tipo = "compra", Modulo = "compras" },
            new() { Valor = 1200, Categoria = "Aluguel", Tipo = "despesa", Modulo = "despesas" },
            new() { Valor = 90, Categoria = "taxas", Tipo = "despesa", Modulo = "financeiro" },
            new() { Valor = 140, Categoria = null, Tipo = "turno", Modulo = "equipe" },
            new() { Valor = -40, Categoria = "insumos", Tipo = "compra", Modulo = "compras" },
        ]);
        Assert.Equal(["fixas", "compras", "equipe", "taxas"], r.Grupos.Select(g => g.Chave));
        Assert.Equal(146.40m, r.Grupos.Single(g => g.Chave == "compras").Valor);
        Assert.Equal(1576.40m, r.Total);
    }
}
