using APIBack.Acontecimentos;
using Xunit;

namespace APIBack.Tests.Unit;

/// <summary>Regras 1 a 5 do bloco comum da gestao, sem banco.</summary>
public class GestaoAcontecimentoRegrasTests
{
    private static readonly TimeZoneInfo Fuso = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");
    private static readonly DateTimeOffset Agora = new(2026, 10, 27, 15, 0, 0, TimeSpan.FromHours(-3));
    private static readonly ContaInfo Banco = new(Guid.NewGuid(), TipoConta.Banco, null, null, true);
    private static readonly ContaInfo Caixa = new(Guid.NewGuid(), TipoConta.Caixa, null, null, true);
    private static readonly ContaInfo Ifood = new(Guid.NewGuid(), TipoConta.Recebivel, null, null, true);
    private static readonly ContaInfo Cartao = new(Guid.NewGuid(), TipoConta.CartaoEmpresa, 25, 5, true);
    private static readonly Dictionary<Guid, ContaInfo> Contas = new[] { Banco, Caixa, Ifood, Cartao }.ToDictionary(c => c.Id);

    private static PublicarAcontecimentoRequest Pedido(string tipo = "despesa_manual", DateTimeOffset? fato = null,
        params LancamentoRequest[] lancamentos) => new()
    {
        ChaveIdempotencia = "k-" + Guid.NewGuid(), Tipo = tipo, Modulo = ModuloGestao.Financeiro, DataFato = fato ?? Agora,
        Descricao = "Teste", Lancamentos = lancamentos.ToList(),
    };

    private static AcontecimentoPlano Planejar(PublicarAcontecimentoRequest r, int? responsavel = 7) =>
        AcontecimentoRegras.PlanejarPublicacao(r, responsavel, Contas, Fuso, Agora);

    private static string Codigo(Action acao) => Assert.Throws<GestaoException>(acao).Codigo;

    // ---- regra 3: venda, receita, recebido e a receber; data do fato x mes de referencia

    [Fact]
    public void StatusNasceDoQueAconteceuDeVerdade()
    {
        var p = Planejar(Pedido("venda", null,
            new LancamentoRequest { Natureza = Natureza.Receita, Valor = 80, ContaId = Ifood.Id },
            new LancamentoRequest { Natureza = Natureza.Receita, Valor = 20, ContaId = Caixa.Id, DataPagamento = new DateOnly(2026, 10, 27) },
            new LancamentoRequest { Natureza = Natureza.Despesa, Valor = 10 }));
        Assert.Equal(new[] { StatusLancamento.AReceber, StatusLancamento.Recebido, StatusLancamento.APagar }, p.Lancamentos.Select(l => l.Status));
        Assert.Null(p.Lancamentos[0].DataPagamento);
        Assert.Equal(new DateOnly(2026, 10, 27), p.Lancamentos[1].DataPagamento);
    }

    [Fact]
    public void MesDeReferenciaPodeSerDiferenteDaDataDoFato()
    {
        var p = Planejar(Pedido("conta_luz", new DateTimeOffset(2026, 10, 3, 10, 0, 0, TimeSpan.FromHours(-3)),
            new LancamentoRequest { Natureza = Natureza.Despesa, Valor = 412.50m, MesReferencia = new DateOnly(2026, 9, 18) }));
        Assert.Equal(new DateOnly(2026, 10, 1), p.MesReferencia);
        Assert.Equal(new DateOnly(2026, 9, 1), p.Lancamentos[0].MesReferencia);
    }

    [Fact]
    public void DataDoFatoUsaOFusoDaLoja()
    {
        // 23h30 de 30/09 em Sao Paulo ja e 01/10 em UTC: continua sendo setembro para a loja.
        var p = Planejar(Pedido("venda", new DateTimeOffset(2026, 10, 1, 2, 30, 0, TimeSpan.Zero)));
        Assert.Equal(new DateOnly(2026, 9, 1), p.MesReferencia);
    }

    [Fact]
    public void CompraParceladaNaoSaiInteiraDoCaixa()
    {
        var p = Planejar(Pedido("compra", null,
            new LancamentoRequest { Natureza = Natureza.Despesa, Valor = 100m, Parcelas = 3, DataVencimento = new DateOnly(2026, 11, 10) }));
        Assert.Equal(3, p.Lancamentos.Count);
        Assert.All(p.Lancamentos, l => Assert.Equal(StatusLancamento.APagar, l.Status));
        Assert.All(p.Lancamentos, l => Assert.Equal(new DateOnly(2026, 10, 1), l.MesReferencia));
        Assert.Equal(100m, p.Lancamentos.Sum(l => l.Valor));
        Assert.Equal(new[] { 33.34m, 33.33m, 33.33m }, p.Lancamentos.Select(l => l.Valor));
        Assert.Equal(new DateOnly?[] { new(2026, 11, 10), new(2026, 12, 10), new(2027, 1, 10) }, p.Lancamentos.Select(l => l.DataVencimento));
        Assert.Equal(new[] { 1, 2, 3 }, p.Lancamentos.Select(l => l.Parcela));
    }

    [Fact]
    public void JaPagoNaoParcela()
    {
        Assert.Equal("CAMPO_INVALIDO", Codigo(() => Planejar(Pedido("compra", null, new LancamentoRequest
        { Natureza = Natureza.Despesa, Valor = 90, Parcelas = 3, ContaId = Banco.Id, DataPagamento = new DateOnly(2026, 10, 27) }))));
    }

    [Theory]
    [InlineData(2026, 10, 24, 2026, 11, 5)] // antes do fechamento (25): fatura que vence em novembro
    [InlineData(2026, 10, 25, 2026, 12, 5)] // no dia do fechamento: fatura seguinte
    [InlineData(2026, 12, 30, 2027, 2, 5)]
    public void CartaoDaEmpresaVaiParaAFatura(int y, int m, int d, int vy, int vm, int vd)
    {
        var (fatura, vencimento) = AcontecimentoRegras.Fatura(new DateOnly(y, m, d), 25, 5);
        Assert.Equal(new DateOnly(vy, vm, vd), vencimento);
        Assert.Equal(new DateOnly(vy, vm, 1), fatura);
    }

    [Fact]
    public void FaturaComVencimentoDepoisDoFechamentoNoMesmoMesEDiaQueNaoExiste()
    {
        Assert.Equal(new DateOnly(2027, 2, 28), AcontecimentoRegras.Fatura(new DateOnly(2027, 2, 2), 5, 31).Vencimento);
        Assert.Equal(new DateOnly(2027, 3, 31), AcontecimentoRegras.Fatura(new DateOnly(2027, 2, 10), 5, 31).Vencimento);
    }

    [Fact]
    public void CompraParceladaNoCartaoCaiUmaParcelaPorFatura()
    {
        var p = Planejar(Pedido("compra", new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.FromHours(-3)),
            new LancamentoRequest { Natureza = Natureza.Despesa, Valor = 300m, Parcelas = 3, ContaId = Cartao.Id }));
        Assert.All(p.Lancamentos, l => Assert.Equal(StatusLancamento.APagar, l.Status));
        Assert.Equal(new DateOnly?[] { new(2026, 11, 1), new(2026, 12, 1), new(2027, 1, 1) }, p.Lancamentos.Select(l => l.FaturaMes));
        Assert.Equal(new DateOnly?[] { new(2026, 11, 5), new(2026, 12, 5), new(2027, 1, 5) }, p.Lancamentos.Select(l => l.DataVencimento));
    }

    [Fact]
    public void CartaoNaoTiraDinheiroNaHora()
    {
        Assert.Equal("CAMPO_INVALIDO", Codigo(() => Planejar(Pedido("compra", null, new LancamentoRequest
        { Natureza = Natureza.Despesa, Valor = 50, ContaId = Cartao.Id, DataPagamento = new DateOnly(2026, 10, 27) }))));
        Assert.Equal("CAMPO_INVALIDO", Codigo(() => Planejar(Pedido("venda", null, new LancamentoRequest
        { Natureza = Natureza.Receita, Valor = 50, ContaId = Cartao.Id }))));
    }

    [Fact]
    public void TransferenciaNaoEReceitaNemDespesaEFechaEmZero()
    {
        var p = Planejar(Pedido("transferencia", null,
            new LancamentoRequest { Natureza = Natureza.Transferencia, Sentido = Sentido.Saida, Valor = 500, ContaId = Caixa.Id },
            new LancamentoRequest { Natureza = Natureza.Transferencia, Sentido = Sentido.Entrada, Valor = 500, ContaId = Banco.Id }));
        Assert.Equal(new[] { StatusLancamento.Pago, StatusLancamento.Recebido }, p.Lancamentos.Select(l => l.Status));

        Assert.Equal("CAMPO_INVALIDO", Codigo(() => Planejar(Pedido("transferencia", null,
            new LancamentoRequest { Natureza = Natureza.Transferencia, Sentido = Sentido.Saida, Valor = 500, ContaId = Caixa.Id },
            new LancamentoRequest { Natureza = Natureza.Transferencia, Sentido = Sentido.Entrada, Valor = 499, ContaId = Banco.Id }))));
        Assert.Equal("CAMPO_INVALIDO", Codigo(() => Planejar(Pedido("transferencia", null,
            new LancamentoRequest { Natureza = Natureza.Transferencia, Sentido = Sentido.Saida, Valor = 10, ContaId = Caixa.Id },
            new LancamentoRequest { Natureza = Natureza.Transferencia, Sentido = Sentido.Entrada, Valor = 10, ContaId = Caixa.Id }))));
        Assert.Equal("CAMPO_INVALIDO", Codigo(() => Planejar(Pedido("transferencia", null,
            new LancamentoRequest { Natureza = Natureza.Transferencia, Sentido = Sentido.Saida, Valor = 10, ContaId = Caixa.Id },
            new LancamentoRequest { Natureza = Natureza.Transferencia, Sentido = Sentido.Entrada, Valor = 10, ContaId = Banco.Id },
            new LancamentoRequest { Natureza = Natureza.Despesa, Valor = 5 }))));
    }

    [Fact]
    public void AporteERetiradaDeSocioFicamForaDaOperacao()
    {
        var p = Planejar(Pedido("aporte_socio", null,
            new LancamentoRequest { Natureza = Natureza.Aporte, Valor = 1000, ContaId = Banco.Id, DataPagamento = new DateOnly(2026, 10, 27) }));
        Assert.Equal(Sentido.Entrada, p.Lancamentos[0].Sentido);
        Assert.True(Natureza.ForaDaOperacao(Natureza.Aporte));
        Assert.True(Natureza.ForaDaOperacao(Natureza.Retirada));
        Assert.False(Natureza.ForaDaOperacao(Natureza.Receita));
        Assert.Equal("CAMPO_INVALIDO", Codigo(() => Planejar(Pedido("aporte_socio", null,
            new LancamentoRequest { Natureza = Natureza.Aporte, Sentido = Sentido.Saida, Valor = 1000, ContaId = Banco.Id }))));
    }

    [Fact]
    public void PagoPrecisaDeContaENaoPodeSerNoFuturo()
    {
        Assert.Equal("CAMPO_INVALIDO", Codigo(() => Planejar(Pedido("despesa", null,
            new LancamentoRequest { Natureza = Natureza.Despesa, Valor = 10, DataPagamento = new DateOnly(2026, 10, 27) }))));
        Assert.Equal("CAMPO_INVALIDO", Codigo(() => Planejar(Pedido("despesa", null,
            new LancamentoRequest { Natureza = Natureza.Despesa, Valor = 10, ContaId = Banco.Id, DataPagamento = new DateOnly(2026, 11, 20) }))));
        Assert.Equal("CAMPO_INVALIDO", Codigo(() => Planejar(Pedido("despesa", Agora.AddHours(2)))));
        Assert.Equal("CAMPO_INVALIDO", Codigo(() => Planejar(Pedido("despesa", null,
            new LancamentoRequest { Natureza = Natureza.Despesa, Valor = 10.005m }))));
    }

    [Fact]
    public void TipoReservadoSoNasceDoProprioLivro()
    {
        foreach (var tipo in TipoAcontecimento.Reservados)
            Assert.Equal("CAMPO_INVALIDO", Codigo(() => Planejar(Pedido(tipo))));
        Assert.Equal("CAMPO_INVALIDO", Codigo(() => Planejar(Pedido("Compra Grande"))));
    }

    // ---- regra 4: estoque nunca muda sem confirmacao humana; regra 8: perda e divergencia

    private static PublicarAcontecimentoRequest Estoque(string tipo, bool confirmado, params MovimentoRequest[] movimentos) => new()
    {
        ChaveIdempotencia = "e-" + Guid.NewGuid(), Tipo = tipo, Modulo = ModuloGestao.Estoque, DataFato = Agora, Descricao = "Estoque",
        Movimentos = movimentos.ToList(), ConfirmacaoHumana = confirmado,
    };

    private static MovimentoRequest Mov(string tipo, decimal qtd, string? motivo = null) =>
        new() { ItemId = Guid.NewGuid(), ItemNome = "Mussarela", Quantidade = qtd, Unidade = "g", Tipo = tipo, Motivo = motivo };

    [Fact]
    public void EstoqueSoMudaComConfirmacaoDeUmaPessoa()
    {
        Assert.Equal("CONFIRMACAO_HUMANA", Codigo(() => Planejar(Estoque("conferencia_item", false, Mov(TipoMovimento.Entrada, 5000)))));
        Assert.Equal("CONFIRMACAO_HUMANA", Codigo(() => Planejar(Estoque("conferencia_item", true, Mov(TipoMovimento.Entrada, 5000)), null)));
        var p = Planejar(Estoque("conferencia_item", true, Mov(TipoMovimento.Entrada, 5000)));
        Assert.Single(p.Movimentos);
    }

    [Fact]
    public void CompraExisteNoFinanceiroSemConferirItens()
    {
        // A compra entra so com lancamentos; a entrada de estoque vem depois, item a item, com confirmacao.
        var p = Planejar(Pedido("compra", null, new LancamentoRequest { Natureza = Natureza.Despesa, Valor = 230 }));
        Assert.Empty(p.Movimentos);
        Assert.Single(p.Lancamentos);
    }

    [Fact]
    public void PerdaNaoGeraSaidaDeCaixa()
    {
        var perda = Estoque(TipoAcontecimento.Perda, true, Mov(TipoMovimento.Perda, -300, "Queimou"));
        Assert.Empty(Planejar(perda).Lancamentos);
        perda.Lancamentos = new() { new LancamentoRequest { Natureza = Natureza.Despesa, Valor = 12, ContaId = Caixa.Id, DataPagamento = new DateOnly(2026, 10, 27) } };
        Assert.Equal("CAMPO_INVALIDO", Codigo(() => Planejar(perda)));
        Assert.Equal("CAMPO_INVALIDO", Codigo(() => Planejar(Estoque(TipoAcontecimento.Perda, true, Mov(TipoMovimento.Perda, -300)))));
        Assert.Equal("CAMPO_INVALIDO", Codigo(() => Planejar(Estoque("saida_producao", true, Mov(TipoMovimento.Perda, -300, "x")))));
    }

    [Fact]
    public void DivergenciaDeInventarioFicaComoDivergencia()
    {
        var p = Planejar(Estoque(TipoAcontecimento.Inventario, true, Mov(TipoMovimento.Divergencia, -120, "Contagem do mês")));
        Assert.Equal(TipoMovimento.Divergencia, p.Movimentos[0].Tipo);
        Assert.Equal("CAMPO_INVALIDO", Codigo(() => Planejar(Estoque(TipoAcontecimento.Inventario, true, Mov(TipoMovimento.Perda, -120, "x")))));
        Assert.Equal("CAMPO_INVALIDO", Codigo(() => Planejar(Estoque("ajuste_estoque", true, Mov(TipoMovimento.Divergencia, -120, "x")))));
    }

    [Fact]
    public void SinalDoMovimentoSegueOTipo()
    {
        Assert.Equal("CAMPO_INVALIDO", Codigo(() => Planejar(Estoque("conferencia_item", true, Mov(TipoMovimento.Entrada, -1)))));
        Assert.Equal("CAMPO_INVALIDO", Codigo(() => Planejar(Estoque("saida", true, Mov(TipoMovimento.Saida, 1)))));
        Assert.Equal("CAMPO_INVALIDO", Codigo(() => Planejar(Estoque("conferencia_item", true,
            new MovimentoRequest { ItemId = Guid.NewGuid(), ItemNome = "x", Quantidade = 1, Unidade = "kg", Tipo = TipoMovimento.Entrada }))));
    }

    // ---- regra 5: idempotencia e duplicidade

    [Fact]
    public void MesmoConteudoMesmoHash()
    {
        var a = Pedido("venda", null, new LancamentoRequest { Natureza = Natureza.Receita, Valor = 10 });
        a.ChaveIdempotencia = "x";
        var b = Pedido("venda", null, new LancamentoRequest { Natureza = Natureza.Receita, Valor = 10 });
        b.ChaveIdempotencia = "x";
        Assert.Equal(Planejar(a).Hash, Planejar(b).Hash);
        b.Lancamentos![0].Valor = 11;
        Assert.NotEqual(Planejar(a).Hash, Planejar(b).Hash);
    }

    [Fact]
    public void DocumentoComFormatacaoDiferenteEOMesmo()
    {
        Assert.Equal(AcontecimentoRegras.Documento("3526 1012.3456-78/0001"), AcontecimentoRegras.Documento("35261012345678 0001"));
        Assert.Null(AcontecimentoRegras.Documento("  "));
        Assert.Equal("CAMPO_INVALIDO", Codigo(() => Planejar(new PublicarAcontecimentoRequest
        { Tipo = "venda", Modulo = "financeiro", DataFato = Agora, Descricao = "sem chave" })));
    }

    [Theory]
    [InlineData(100, 3)]
    [InlineData(0.05, 3)]
    [InlineData(1234.56, 7)]
    public void ParcelasSomamOTotalExato(decimal total, int n)
    {
        var p = AcontecimentoRegras.Parcelar(total, n);
        Assert.Equal(total, p.Sum());
        Assert.All(p, v => Assert.Equal(decimal.Round(v, 2), v));
    }

    // ---- regra 2: nada se apaga; correcao e novo acontecimento; mes fechado so aceita ajuste

    [Fact]
    public void MesFechadoSoAceitaCorrecao()
    {
        var setembro = new HashSet<DateOnly> { new(2026, 9, 1) };
        var p = Planejar(Pedido("conta", null,
            new LancamentoRequest { Natureza = Natureza.Despesa, Valor = 10, MesReferencia = new DateOnly(2026, 9, 1) }));
        Assert.Equal("MES_FECHADO", Codigo(() => AcontecimentoRegras.ValidarMesesFechados(p, setembro)));
        AcontecimentoRegras.ValidarMesesFechados(p with { Correcao = Correcao.Ajuste }, setembro);
        AcontecimentoRegras.ValidarMesesFechados(Planejar(Pedido("conta")), setembro);
    }

    private static readonly AcontecimentoAtual Original = new(Guid.NewGuid(), "compra", ModuloGestao.Compras, null, new DateOnly(2026, 9, 1), false);

    private static LancamentoAtual Linha(decimal valor, string status = StatusLancamento.APagar, Guid? corrige = null, decimal liquidado = 0,
        Guid? conta = null, Guid? acontecimento = null) =>
        new(Guid.NewGuid(), acontecimento ?? Original.Id, 1, conta, Natureza.Despesa, Sentido.Saida, valor, status, status,
            new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 10), 1, 1, null, "insumos", null, "Compra", corrige, liquidado);

    [Fact]
    public void EstornoAnulaOEfeitoLiquidoComLinhasNegativas()
    {
        var original = Linha(200);
        var ajuste = Linha(-20, corrige: original.Id, acontecimento: Guid.NewGuid());
        var plano = AcontecimentoRegras.PlanejarEstorno(Original, new[] { original, ajuste }, Array.Empty<MovimentoAtual>(),
            Array.Empty<LiquidacaoAtual>(), new EstornoRequest { ChaveIdempotencia = "est", Motivo = "Fornecedor cancelou" }, 7, Fuso, Agora);
        var linha = Assert.Single(plano.Lancamentos);
        Assert.Equal(-180m, linha.Valor);
        Assert.Equal(original.Id, linha.CorrigeLancamentoId);
        Assert.Equal(new DateOnly(2026, 9, 1), linha.MesReferencia);
        Assert.Equal(Correcao.Estorno, plano.Correcao);
        Assert.Equal(Original.Id, plano.CorrigeId);
        Assert.Equal(180m, plano.ValorAnterior);
        Assert.Equal(0m, plano.ValorNovo);
        Assert.Equal(StatusLancamento.APagar, linha.Status);
    }

    [Fact]
    public void EstornoDeAlgoJaPagoDevolveODinheiroNaDataDoEstorno()
    {
        var pago = Linha(50, StatusLancamento.Pago, conta: Banco.Id);
        var plano = AcontecimentoRegras.PlanejarEstorno(Original, new[] { pago }, Array.Empty<MovimentoAtual>(), Array.Empty<LiquidacaoAtual>(),
            new EstornoRequest { ChaveIdempotencia = "e", Motivo = "Devolvido" }, 7, Fuso, Agora);
        Assert.Equal(StatusLancamento.Pago, plano.Lancamentos[0].Status);
        Assert.Equal(new DateOnly(2026, 10, 27), plano.Lancamentos[0].DataPagamento);
    }

    [Fact]
    public void EstornoTemRegras()
    {
        var linhas = new[] { Linha(10) };
        var req = new EstornoRequest { ChaveIdempotencia = "e", Motivo = "Errado" };
        Assert.Equal("JA_ESTORNADO", Codigo(() => AcontecimentoRegras.PlanejarEstorno(Original with { Estornado = true }, linhas,
            Array.Empty<MovimentoAtual>(), Array.Empty<LiquidacaoAtual>(), req, 7, Fuso, Agora)));
        Assert.Equal("CAMPO_INVALIDO", Codigo(() => AcontecimentoRegras.PlanejarEstorno(Original with { Correcao = Correcao.Ajuste }, linhas,
            Array.Empty<MovimentoAtual>(), Array.Empty<LiquidacaoAtual>(), req, 7, Fuso, Agora)));
        Assert.Equal("CAMPO_INVALIDO", Codigo(() => AcontecimentoRegras.PlanejarEstorno(Original, linhas, Array.Empty<MovimentoAtual>(),
            Array.Empty<LiquidacaoAtual>(), new EstornoRequest { ChaveIdempotencia = "e", Motivo = "x" }, 7, Fuso, Agora)));
        Assert.Equal("SEM_RESPONSAVEL", Codigo(() => AcontecimentoRegras.PlanejarEstorno(Original, linhas, Array.Empty<MovimentoAtual>(),
            Array.Empty<LiquidacaoAtual>(), req, null, Fuso, Agora)));
        var movimento = new MovimentoAtual(Guid.NewGuid(), Original.Id, 1, Guid.NewGuid(), "Farinha", 5000, "g", TipoMovimento.Entrada,
            new DateOnly(2026, 9, 1), null);
        Assert.Equal("CONFIRMACAO_HUMANA", Codigo(() => AcontecimentoRegras.PlanejarEstorno(Original, linhas, new[] { movimento },
            Array.Empty<LiquidacaoAtual>(), req, 7, Fuso, Agora)));
    }

    [Fact]
    public void AjusteGuardaValorAnteriorNovoEMotivo()
    {
        var original = Linha(100);
        var plano = AcontecimentoRegras.PlanejarAjusteLancamento(Original, original, Array.Empty<LancamentoAtual>(),
            new AjusteRequest { ChaveIdempotencia = "a", Motivo = "Cupom tinha desconto", LancamentoId = original.Id, ValorNovo = 92.40m },
            7, Fuso, Agora);
        Assert.Equal(-7.60m, plano.Lancamentos[0].Valor);
        Assert.Equal(100m, plano.ValorAnterior);
        Assert.Equal(92.40m, plano.ValorNovo);
        Assert.Equal("Cupom tinha desconto", plano.Motivo);
        Assert.Equal(7, plano.ResponsavelId);
        var correcao = Linha(-7.60m, corrige: original.Id);
        Assert.Equal("CAMPO_INVALIDO", Codigo(() => AcontecimentoRegras.PlanejarAjusteLancamento(Original, correcao, Array.Empty<LancamentoAtual>(),
            new AjusteRequest { ChaveIdempotencia = "a", Motivo = "Outro", ValorNovo = 1 }, 7, Fuso, Agora)));
        Assert.Equal("CAMPO_INVALIDO", Codigo(() => AcontecimentoRegras.PlanejarAjusteLancamento(Original, original, new[] { correcao },
            new AjusteRequest { ChaveIdempotencia = "a", Motivo = "Igual", ValorNovo = 92.40m }, 7, Fuso, Agora)));
    }

    [Fact]
    public void LiquidacaoPagaOValorLiquidoUmaVezSo()
    {
        var original = Linha(100);
        var ajuste = Linha(-10, corrige: original.Id);
        var plano = AcontecimentoRegras.PlanejarLiquidacao(original, new[] { ajuste }, Banco, null, Original,
            new LiquidarRequest { ChaveIdempotencia = "p", LancamentoId = original.Id, ContaId = Banco.Id }, 7, Fuso, Agora);
        Assert.Equal(90m, plano.Liquidacoes.Sum(q => q.Valor));
        Assert.Equal(2, plano.Liquidacoes.Count);
        Assert.Equal(TipoAcontecimento.Pagamento, plano.Tipo);

        Assert.Equal("DUPLICADO", Codigo(() => AcontecimentoRegras.PlanejarLiquidacao(original with { Liquidado = 100 }, Array.Empty<LancamentoAtual>(),
            Banco, null, Original, new LiquidarRequest { ChaveIdempotencia = "p2" }, 7, Fuso, Agora)));
        Assert.Equal("CAMPO_INVALIDO", Codigo(() => AcontecimentoRegras.PlanejarLiquidacao(original, Array.Empty<LancamentoAtual>(), Ifood, null,
            Original, new LiquidarRequest { ChaveIdempotencia = "p3" }, 7, Fuso, Agora)));
        Assert.Equal("CAMPO_INVALIDO", Codigo(() => AcontecimentoRegras.PlanejarLiquidacao(original, Array.Empty<LancamentoAtual>(), Banco, Cartao,
            Original, new LiquidarRequest { ChaveIdempotencia = "p4" }, 7, Fuso, Agora)));
        Assert.Equal("CAMPO_INVALIDO", Codigo(() => AcontecimentoRegras.PlanejarLiquidacao(original, new[] { Linha(-100, corrige: original.Id) },
            Banco, null, Original, new LiquidarRequest { ChaveIdempotencia = "p5" }, 7, Fuso, Agora)));
    }

    [Fact]
    public void SoFechaMesQueJaTerminou()
    {
        var p = AcontecimentoRegras.PlanejarFechamento(new DateOnly(2026, 9, 1), new FecharMesRequest { ChaveIdempotencia = "f" }, 7, Fuso, Agora);
        Assert.Equal(TipoAcontecimento.FechamentoMes, p.Tipo);
        Assert.Equal("2026-09", p.OrigemId);
        Assert.Equal("CAMPO_INVALIDO", Codigo(() => AcontecimentoRegras.PlanejarFechamento(new DateOnly(2026, 10, 1),
            new FecharMesRequest { ChaveIdempotencia = "f" }, 7, Fuso, Agora)));
        Assert.Equal("SEM_RESPONSAVEL", Codigo(() => AcontecimentoRegras.PlanejarFechamento(new DateOnly(2026, 9, 1),
            new FecharMesRequest { ChaveIdempotencia = "f" }, null, Fuso, Agora)));
    }
}
