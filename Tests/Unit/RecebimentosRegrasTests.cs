using APIBack.Payments.MercadoPago;
using APIBack.Payments.Provedores;
using APIBack.Payments.Recebimentos;
using Xunit;

namespace APIBack.Tests.Unit;

public sealed class RecebimentosRegrasTests
{
    private const string Ip = ProvedoresPagamento.InfinitePay;
    private const string Mp = ProvedoresPagamento.MercadoPago;

    [Fact]
    public void PagoInfinitePaySoComConfirmacaoDoServidor()
    {
        Assert.Equal(SituacaoRecebimento.Pago, RecebimentosRegras.Situacao(Ip, StatusIntent.Pago, true, false, 0, 0));
        Assert.Equal(SituacaoRecebimento.NaoVerificado, RecebimentosRegras.Situacao(Ip, StatusIntent.Pago, false, false, 0, 0));
    }

    [Fact]
    public void MercadoPagoPagoNaoDependeDoCheckout()
    {
        Assert.Equal(SituacaoRecebimento.Pago, RecebimentosRegras.Situacao(Mp, StatusIntent.Pago, false, false, 0, 0));
    }

    [Fact]
    public void AvisoOuPendenciaSemPagamentoEhNaoVerificadoNuncaPago()
    {
        Assert.Equal(SituacaoRecebimento.NaoVerificado, RecebimentosRegras.Situacao(Ip, StatusIntent.AguardandoPagamento, false, false, 1, 0));
        Assert.Equal(SituacaoRecebimento.NaoVerificado, RecebimentosRegras.Situacao(Ip, StatusIntent.Expirado, false, false, 0, 2));
    }

    [Fact]
    public void EstornoInformadoNaoViraEstornado()
    {
        Assert.Equal(SituacaoRecebimento.EstornoInformado, RecebimentosRegras.Situacao(Ip, StatusIntent.Pago, true, true, 0, 0));
        Assert.Equal(SituacaoRecebimento.Estornado, RecebimentosRegras.Situacao(Ip, StatusIntent.Estornado, true, false, 0, 0));
        // Parcial confirmado e outro informado depois: o que importa e o que ainda falta confirmar.
        Assert.Equal(SituacaoRecebimento.EstornoInformado, RecebimentosRegras.Situacao(Ip, StatusIntent.EstornadoParcial, true, true, 0, 0));
    }

    [Fact]
    public void AbertoSemSinalAguardaEFechadoEncerra()
    {
        Assert.Equal(SituacaoRecebimento.Aguardando, RecebimentosRegras.Situacao(Ip, StatusIntent.AguardandoPagamento, false, false, 0, 0));
        Assert.Equal(SituacaoRecebimento.Encerrado, RecebimentosRegras.Situacao(Ip, StatusIntent.Expirado, false, false, 0, 0));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    [InlineData("abc", "••••")]
    [InlineData("abcd", "••••")]
    [InlineData("tx-1234567890", "••••7890")]
    public void MascaraSoMostraOsUltimosQuatro(string? id, string? esperado) => Assert.Equal(esperado, RecebimentosRegras.Mascarar(id));

    [Theory]
    [InlineData("http_503", "http_503")]
    [InlineData("nao_confirmado", "nao_confirmado")]
    [InlineData("Bearer abc.def/ghi", "erro_interno")]
    [InlineData("chave=segredo", "erro_interno")]
    [InlineData(null, null)]
    public void ErroSoSaiSeForCodigoCurto(string? erro, string? esperado) => Assert.Equal(esperado, RecebimentosRegras.ErroSeguro(erro));

    [Fact]
    public void ConfirmarSomaEDefineOStatus()
    {
        Assert.Equal((30m, StatusIntent.EstornadoParcial), RecebimentosRegras.AposConfirmar(100m, 0m, 30m));
        Assert.Equal((100m, StatusIntent.Estornado), RecebimentosRegras.AposConfirmar(100m, 30m, 70m));
    }

    [Fact]
    public void ConfirmarNaoPassaDoPago()
    {
        var erro = Assert.Throws<PagamentoException>(() => RecebimentosRegras.AposConfirmar(100m, 30m, 70.01m));
        Assert.Equal(PagamentoCodigos.ValorInvalido, erro.Codigo);
    }

    [Theory]
    [InlineData(0, "Cliente desistiu", "k")]
    [InlineData(10.123, "Cliente desistiu", "k")]
    [InlineData(10, "ok", "k")]
    [InlineData(10, "Cliente desistiu", "")]
    public void InformeInvalidoEhRecusado(decimal valor, string motivo, string chave) =>
        Assert.Throws<PagamentoException>(() => RecebimentosRegras.ValidarInforme(valor, motivo, null, chave));

    [Fact]
    public void ConfirmacaoExigeEvidencia()
    {
        Assert.Throws<PagamentoException>(() => RecebimentosRegras.ValidarEvidencia("  "));
        RecebimentosRegras.ValidarEvidencia("Comprovante 123 conferido no app");
    }
}
