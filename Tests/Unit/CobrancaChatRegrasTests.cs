using System;
using APIBack.Payments.Chat;
using APIBack.Payments.MercadoPago;
using Xunit;

namespace APIBack.Tests.Unit;

/// <summary>Regras puras da cobranca Pix do atendente (Payments/Chat/CobrancaChatRegras.cs).</summary>
public sealed class CobrancaChatRegrasTests
{
    private static AlvoCobranca Pedido(decimal? total, decimal recebido = 0m, string? statusPagamento = "Pendente") =>
        new() { PedidoId = 10, Total = total, Recebido = recebido, StatusPagamento = statusPagamento, Codigo = "10" };

    [Fact]
    public void VinculadaUsaSaldoDoServidorENuncaOValorDigitado()
    {
        Assert.Equal(80m, CobrancaChatRegras.ValorVinculada(Pedido(100m, 20m), null));
        Assert.Equal(80m, CobrancaChatRegras.ValorVinculada(Pedido(100m, 20m), 80m));
        var divergente = Assert.Throws<PagamentoException>(() => CobrancaChatRegras.ValorVinculada(Pedido(100m, 20m), 1m));
        Assert.Equal(CobrancaChatCodigos.ValorDivergente, divergente.Codigo);
        Assert.Equal(80m, divergente.Detalhes!["saldo"]);
    }

    [Theory]
    [InlineData("Pago", 0)]
    [InlineData("pago_online", 0)]
    [InlineData("Pendente", 100)]
    public void PedidoQuitadoNaoPodeSerCobrado(string status, decimal recebido)
    {
        var ex = Assert.Throws<PagamentoException>(() => CobrancaChatRegras.ValorVinculada(Pedido(100m, recebido, status), null));
        Assert.Equal(PagamentoCodigos.JaPago, ex.Codigo);
        Assert.Equal(409, ex.Http);
    }

    [Fact]
    public void PedidoSemValorNaoEPagavel() =>
        Assert.Equal(PagamentoCodigos.PedidoNaoPagavel, Assert.Throws<PagamentoException>(() => CobrancaChatRegras.ValorVinculada(Pedido(null), null)).Codigo);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1000000)]
    [InlineData(10.001)]
    public void ValorAvulsoInvalido(decimal valor) =>
        Assert.Equal(PagamentoCodigos.ValorInvalido, Assert.Throws<PagamentoException>(() => CobrancaChatRegras.ExigirValor(valor)).Codigo);

    [Fact]
    public void LimiteDeAlcada()
    {
        Assert.True(CobrancaChatRegras.ExigeAprovacao(150m, 100m, podeAprovar: false));
        Assert.False(CobrancaChatRegras.ExigeAprovacao(100m, 100m, podeAprovar: false));
        Assert.False(CobrancaChatRegras.ExigeAprovacao(150m, 100m, podeAprovar: true));
        Assert.False(CobrancaChatRegras.ExigeAprovacao(150m, null, podeAprovar: false));
    }

    [Fact]
    public void TipoDeduzidoDoPedido()
    {
        Assert.Equal(TiposCobranca.Vinculada, CobrancaChatRegras.Tipo(null, 3, null));
        Assert.Equal(TiposCobranca.Avulsa, CobrancaChatRegras.Tipo("avulsa", null, null));
        Assert.Throws<PagamentoException>(() => CobrancaChatRegras.Tipo("vinculada", null, null));
        Assert.Throws<PagamentoException>(() => CobrancaChatRegras.Tipo("avulsa", 3, null));
    }

    [Fact]
    public void JanelaDe24Horas()
    {
        var agora = DateTime.UtcNow;
        Assert.True(CobrancaChatRegras.JanelaAberta(agora.AddHours(1), "aberto", agora));
        Assert.False(CobrancaChatRegras.JanelaAberta(agora.AddHours(-1), "aberto", agora));
        Assert.False(CobrancaChatRegras.JanelaAberta(agora.AddHours(1), "fechado_agente", agora));
        Assert.False(CobrancaChatRegras.JanelaAberta(null, "aberto", agora));
    }

    [Fact]
    public void ParametrosDoModeloEFormatos()
    {
        Assert.Equal(new[] { "https://x/pix/t" }, CobrancaChatRegras.ParametrosModelo(1, "Taxa", 12.5m, "https://x/pix/t"));
        Assert.Equal(3, CobrancaChatRegras.ParametrosModelo(3, "Taxa", 12.5m, "l")!.Count);
        Assert.Null(CobrancaChatRegras.ParametrosModelo(4, "Taxa", 12.5m, "l"));
        Assert.Equal(2, CobrancaChatRegras.ContarVariaveis("Ola {{1}}, pague {{2}} ({{1}})"));

        var link = CobrancaChatRegras.Textos("link", "Taxa", 12.5m, null, "https://x/pix/t", "000201");
        Assert.Single(link);
        Assert.Contains("https://x/pix/t", link[0]);
        var ambos = CobrancaChatRegras.Textos("ambos", "Taxa", 12.5m, null, "https://x/pix/t", "000201");
        Assert.Equal("000201", ambos[^1]);
        Assert.Throws<PagamentoException>(() => CobrancaChatRegras.Formato("cartao"));
    }

    [Fact]
    public void AcoesDoBalao()
    {
        var d = new CobrancaChatDto { Status = StatusCobranca.Pago, Valor = 10m };
        CobrancaChatRegras.PreencherAcoes(d, podeAprovar: false, podeReembolsar: false, temSucessoraViva: false);
        Assert.False(d.PodeReembolsar);
        Assert.True(d.PodeSolicitarAprovacao);
        Assert.False(d.PodeCancelar);

        var aberta = new CobrancaChatDto { Status = StatusCobranca.Enviado, Valor = 10m, ExpiraEm = DateTime.UtcNow.AddMinutes(10) };
        CobrancaChatRegras.PreencherAcoes(aberta, false, false, temSucessoraViva: false);
        Assert.True(aberta.PodeEnviar && aberta.PodeCancelar && aberta.PodeReemitir);
        CobrancaChatRegras.PreencherAcoes(aberta, false, false, temSucessoraViva: true);
        Assert.False(aberta.PodeReemitir);
    }

    [Fact]
    public void MaquinaDeEstadosComAprovacao()
    {
        Assert.True(PagamentoStatusRules.PodeCobranca(StatusCobranca.AguardandoAprovacao, StatusCobranca.Criando));
        Assert.True(PagamentoStatusRules.PodeCobranca(StatusCobranca.AguardandoAprovacao, StatusCobranca.Cancelado));
        Assert.False(PagamentoStatusRules.PodeCobranca(StatusCobranca.AguardandoAprovacao, StatusCobranca.Pago));
        Assert.True(StatusCobranca.Aberta(StatusCobranca.AguardandoAprovacao));
        // Pix pago depois de cancelado/expirado volta a pago (pagamento tardio).
        Assert.Equal(StatusCobranca.Pago, PagamentoStatusRules.StatusDaCobranca(StatusCobranca.Cancelado, StatusIntent.Pago));
    }
}
