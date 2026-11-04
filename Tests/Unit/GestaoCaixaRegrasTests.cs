using APIBack.Acontecimentos;
using APIBack.GestaoLogica.Caixa;
using Xunit;

namespace APIBack.Tests.Unit;

/// <summary>Contagem por cédula, diferença, troco que fica, movimentos de gaveta e justificativa.</summary>
public class GestaoCaixaRegrasTests
{
    [Fact]
    public void Conta_cedulas_e_moedas_e_recusa_contagem_invalida()
    {
        Assert.Equal(283.55m, CaixaRegras.TotalContado(new ContagemCaixa(new Dictionary<int, int> { [100] = 2, [50] = 1, [20] = 1, [10] = 1, [2] = 1 }, 1.55m)));
        Assert.Throws<GestaoException>(() => CaixaRegras.TotalContado(new ContagemCaixa(new Dictionary<int, int> { [3] = 1 }, 0)));
        Assert.Throws<GestaoException>(() => CaixaRegras.TotalContado(new ContagemCaixa(new Dictionary<int, int> { [10] = -1 }, 0)));
    }

    [Theory]
    [InlineData("PAUSADA", null, "pausada")]
    [InlineData("SEM_HORARIO", null, "horário de funcionamento")]
    [InlineData("FORA_HORARIO", "hoje às 18:00", "abre hoje às 18:00")]
    [InlineData("FORA_HORARIO", null, "Abra a loja antes de abrir o caixa")]
    [InlineData(null, null, "Abra a loja antes de abrir o caixa")]
    public void Loja_fechada_diz_o_motivo_e_manda_abrir_a_loja_primeiro(string? motivo, string? proxima, string trecho) =>
        Assert.Contains(trecho, CaixaRegras.MensagemLojaFechada(motivo, proxima));

    [Theory]
    // pago de verdade (online, balcão pago na hora): não cobra, mesmo com a forma combinada
    [InlineData("Pago", "Pix", false, null, false, "pago", "pix", null)]
    [InlineData("pago_online", "cartao_online", false, null, false, "pago", "cre", null)]
    // forma combinada NÃO prova recebimento: cobra no caixa, ou do motoboy quando ele está com o pedido
    [InlineData("Pendente", "Dinheiro", false, null, false, "a_receber", "din", "caixa")]
    [InlineData("Pendente", "Dinheiro", false, null, true, "a_receber", "din", "motoboy")]
    [InlineData(null, null, false, null, false, "a_receber", null, "caixa")]
    // ausência de informação nunca vira pago
    [InlineData("", "link", false, null, false, "a_receber", null, "caixa")]
    // iFood: o que o app cobrou é recebível externo; sobra a cobrar só se o snapshot disser que falta
    [InlineData("Pendente", null, true, 0.0, false, "recebivel_externo", null, "ifood")]
    [InlineData("Pendente", null, true, null, false, "recebivel_externo", null, "ifood")]
    [InlineData("Pendente", "Dinheiro", true, 20.0, true, "a_receber", "din", "motoboy")]
    public void Pedido_em_aberto_tem_situacao_financeira_propria(string? status, string? tipo, bool ifood, double? pendente, bool motoboy,
        string pagamento, string? forma, string? responsavel) =>
        Assert.Equal((pagamento, forma, responsavel),
            CaixaRegras.SituacaoDoPagamento(status, tipo, ifood, pendente is null ? null : (decimal)pendente, motoboy));

    [Theory]
    // forma combinada, pedido não pago: não prova recebimento
    [InlineData("Dinheiro", false, false)]
    [InlineData("Pix", false, false)]
    [InlineData("cartao_entrega", false, false)]
    // pedido pago (balcão pago na hora) ou pago online: recebido
    [InlineData("Dinheiro", true, true)]
    [InlineData("Pago online - Pix", false, true)]
    [InlineData("Pago online - cartao", false, true)]
    public void Venda_so_conta_como_recebida_se_o_pedido_esta_pago_ou_foi_pago_online(string tipo, bool pedidoPago, bool recebida)
    {
        var parte = Assert.Single(APIBack.GestaoLogica.Vendas.VendaRegras.Composicao(50m, tipo, null, null, pedidoPago));
        Assert.Equal(recebida, parte.Recebida);
        Assert.Equal(50m, parte.Valor);
        // A forma combinada continua informada (a tela mostra "pagar em dinheiro"), só não vira recebimento.
        Assert.NotNull(parte.Forma);
    }

    [Fact]
    public void Venda_sem_forma_nem_pagamento_conferido_nunca_vira_recebida_por_ausencia_de_informacao()
    {
        var parte = Assert.Single(APIBack.GestaoLogica.Vendas.VendaRegras.Composicao(50m, null, null, null, pedidoPago: false));
        Assert.Null(parte.Forma);
        Assert.False(parte.Recebida);
    }

    [Fact]
    public void Forma_da_linha_da_fita_vem_da_conta_e_venda_dividida_nao_vira_tudo_dinheiro()
    {
        var partes = new List<(string? Forma, decimal Valor)> { ("din", 66.90m), ("pix", 20m) };
        // A forma principal da venda é din, mas a linha de R$ 20 entrou na maquininha: não é dinheiro da gaveta.
        Assert.Equal("din", CaixaRegras.FormaDaLinha(FormaDestino.Gaveta, "din", partes, 66.90m));
        Assert.Equal("pix", CaixaRegras.FormaDaLinha(FormaDestino.Maquininha, "din", partes, 20m));
        Assert.Equal("ifood", CaixaRegras.FormaDaLinha(FormaDestino.Ifood, "din", partes, 50m));
        // Cartão e Pix sem parte que case com o valor: a principal vale se for de maquininha; senão Pix (nunca din).
        Assert.Equal("cre", CaixaRegras.FormaDaLinha(FormaDestino.Maquininha, "cre", new List<(string?, decimal)>(), 40.5m));
        Assert.Equal("pix", CaixaRegras.FormaDaLinha(FormaDestino.Maquininha, "din", new List<(string?, decimal)>(), 9m));
    }

    [Fact]
    public void Diferenca_justificativa_e_troco_que_fica()
    {
        Assert.Equal(-2m, CaixaRegras.Diferenca(25, 23));
        Assert.Throws<GestaoException>(() => CaixaRegras.ExigirJustificativa(-2, " "));
        CaixaRegras.ExigirJustificativa(0, null);
        Assert.Equal((10m, 13m), CaixaRegras.Repartir(23, 10));
        Assert.Equal((23m, 0m), CaixaRegras.Repartir(23, 50)); // não fica mais do que há
        Assert.True(CaixaRegras.MaquininhaConfere(96.5m, 96.505m));
    }

    [Fact]
    public void Movimento_nao_tira_mais_do_que_ha_na_gaveta()
    {
        Assert.Equal("SEM_SALDO", Assert.Throws<GestaoException>(() => CaixaRegras.ValidarMovimento(MovimentoCaixa.Sangria, 200, 192.9m)).Codigo);
        CaixaRegras.ValidarMovimento(MovimentoCaixa.Suprimento, 200, 0);
        Assert.Throws<GestaoException>(() => CaixaRegras.ValidarMovimento(MovimentoCaixa.Pagamento, 0, 10));
        Assert.Throws<GestaoException>(() => CaixaRegras.ValidarMovimento("troco", 1, 10));
    }
}
