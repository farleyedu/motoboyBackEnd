using System;
using System.Security.Cryptography;
using APIBack.Payments.MercadoPago;
using Xunit;

namespace APIBack.Tests.Unit;

public sealed class MercadoPagoNucleoTests
{
    private const string Segredo = "segredo-do-webhook";

    [Fact]
    public void AssinaturaValidaComManifestoDoMercadoPago()
    {
        var agora = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var ts = agora.ToUnixTimeSeconds().ToString();
        var v1 = MercadoPagoAssinatura.Assinar(Segredo, $"id:abc123;request-id:req-1;ts:{ts};");

        // data.id alfanumerico e assinado em minusculas.
        var r = MercadoPagoAssinatura.Validar($"ts={ts},v1={v1}", "req-1", "ABC123", Segredo, agora, 600);
        Assert.True(r.Valida);
        Assert.Equal(1_800_000_000, r.Ts);
    }

    [Theory]
    [InlineData("ts=1800000000,v1=00", "assinatura_invalida")]
    [InlineData("v1=abc", "assinatura_malformada")]
    [InlineData("", "assinatura_ausente")]
    public void AssinaturaRecusada(string header, string motivo)
    {
        var r = MercadoPagoAssinatura.Validar(header, "req-1", "1", Segredo, DateTimeOffset.FromUnixTimeSeconds(1_800_000_000), 600);
        Assert.False(r.Valida);
        Assert.Equal(motivo, r.Motivo);
    }

    [Fact]
    public void AssinaturaComTimestampVelhoERecusadaMesmoComHmacCerto()
    {
        var ts = "1800000000";
        var v1 = MercadoPagoAssinatura.Assinar(Segredo, MercadoPagoAssinatura.Manifesto("1", "req-1", ts));
        var r = MercadoPagoAssinatura.Validar($"ts={ts},v1={v1}", "req-1", "1", Segredo, DateTimeOffset.FromUnixTimeSeconds(1_800_000_000 + 3600), 600);
        Assert.False(r.Valida);
        Assert.Equal("timestamp_fora_da_janela", r.Motivo);

        // ts em milissegundos tambem e aceito.
        var ms = "1800000000000";
        var v1ms = MercadoPagoAssinatura.Assinar(Segredo, MercadoPagoAssinatura.Manifesto("1", "req-1", ms));
        Assert.True(MercadoPagoAssinatura.Validar($"ts={ms},v1={v1ms}", "req-1", "1", Segredo, DateTimeOffset.FromUnixTimeSeconds(1_800_000_010), 600).Valida);
    }

    [Fact]
    public void SegredoCifradoSoAbreNoMesmoContextoEComAMesmaChave()
    {
        var chave = RandomNumberGenerator.GetBytes(32);
        var p = new PagamentoSegredoProtector(chave);
        var id = Guid.NewGuid();
        var cifrado = p.Proteger("APP_USR-token", SegredoContexto.LojaAccessToken(id));

        Assert.DoesNotContain("APP_USR", cifrado);
        Assert.Equal("APP_USR-token", p.Revelar(cifrado, SegredoContexto.LojaAccessToken(id)));
        Assert.Null(p.Revelar(cifrado, SegredoContexto.LojaRefreshToken(id)));
        Assert.Null(p.Revelar(cifrado, SegredoContexto.LojaAccessToken(Guid.NewGuid())));
        Assert.Null(new PagamentoSegredoProtector(RandomNumberGenerator.GetBytes(32)).Revelar(cifrado, SegredoContexto.LojaAccessToken(id)));
        // Rotacao: a chave anterior ainda abre.
        Assert.Equal("APP_USR-token", new PagamentoSegredoProtector(RandomNumberGenerator.GetBytes(32), chave).Revelar(cifrado, SegredoContexto.LojaAccessToken(id)));
    }

    [Theory]
    [InlineData("pending", null, 0, StatusPagamento.Pendente)]
    [InlineData("approved", null, 0, StatusPagamento.Aprovado)]
    [InlineData("approved", null, 4, StatusPagamento.EstornadoParcial)]
    [InlineData("refunded", null, 10, StatusPagamento.Estornado)]
    [InlineData("cancelled", "expired", 0, StatusPagamento.Expirado)]
    [InlineData("cancelled", "by_collector", 0, StatusPagamento.Cancelado)]
    [InlineData("charged_back", null, 0, StatusPagamento.Contestado)]
    [InlineData("in_mediation", null, 0, StatusPagamento.EmMediacao)]
    [InlineData("rejected", "cc_rejected_other_reason", 0, StatusPagamento.Recusado)]
    public void TraduzStatusDoMercadoPago(string mp, string? detalhe, decimal estornado, string esperado) =>
        Assert.Equal(esperado, PagamentoStatusRules.Traduzir(mp, detalhe, 10m, estornado));

    [Fact]
    public void EventoForaDeOrdemNaoRetrocede()
    {
        var t1 = new DateTime(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);
        var t0 = t1.AddMinutes(-1);
        Assert.False(PagamentoStatusRules.DeveAplicar(StatusPagamento.Aprovado, t1, StatusPagamento.Pendente, t0, 0, 0));
        // Mesmo com data mais nova, aprovado nunca volta a pendente.
        Assert.False(PagamentoStatusRules.DeveAplicar(StatusPagamento.Aprovado, t0, StatusPagamento.Pendente, t1, 0, 0));
        Assert.True(PagamentoStatusRules.DeveAplicar(StatusPagamento.Pendente, t0, StatusPagamento.Aprovado, t1, 0, 0));
        // Mesma foto de novo (webhook duplicado): nada a aplicar.
        Assert.False(PagamentoStatusRules.DeveAplicar(StatusPagamento.Aprovado, t1, StatusPagamento.Aprovado, t1, 0, 0));
        Assert.True(PagamentoStatusRules.DeveAplicar(StatusPagamento.Aprovado, t1, StatusPagamento.Estornado, t1.AddMinutes(1), 0, 10));
    }

    [Fact]
    public void IntentNuncaSaiDaFamiliaPagaPorPixAntigoCancelado()
    {
        Assert.Equal(StatusIntent.Pago, PagamentoStatusRules.StatusDaIntent(StatusIntent.Pago, StatusPagamento.Cancelado, true));
        Assert.Equal(StatusIntent.AguardandoPagamento, PagamentoStatusRules.StatusDaIntent(StatusIntent.AguardandoPagamento, StatusPagamento.Cancelado, false));
        Assert.Equal(StatusIntent.Expirado, PagamentoStatusRules.StatusDaIntent(StatusIntent.AguardandoPagamento, StatusPagamento.Expirado, true));
        // Pago depois de expirado: o dinheiro entrou, vale o pago.
        Assert.Equal(StatusIntent.Pago, PagamentoStatusRules.StatusDaIntent(StatusIntent.Expirado, StatusPagamento.Aprovado, false));
        Assert.Equal(StatusIntent.Contestado, PagamentoStatusRules.StatusDaIntent(StatusIntent.Pago, StatusPagamento.Contestado, true));
    }

    [Fact]
    public void CobrancaDoChatSegueAMaquinaDeEstados()
    {
        Assert.Equal(StatusCobranca.Aguardando, PagamentoStatusRules.StatusDaCobranca(StatusCobranca.Criando, StatusIntent.AguardandoPagamento));
        Assert.Null(PagamentoStatusRules.StatusDaCobranca(StatusCobranca.Enviado, StatusIntent.AguardandoPagamento));
        Assert.Equal(StatusCobranca.Pago, PagamentoStatusRules.StatusDaCobranca(StatusCobranca.Enviado, StatusIntent.Pago));
        Assert.Equal(StatusCobranca.Reembolsado, PagamentoStatusRules.StatusDaCobranca(StatusCobranca.ReembolsoSolicitado, StatusIntent.Estornado));
        Assert.Null(PagamentoStatusRules.StatusDaCobranca(StatusCobranca.Reembolsado, StatusIntent.Pago));
        Assert.False(PagamentoStatusRules.PodeCobranca(StatusCobranca.Cancelado, StatusCobranca.Enviado));
        Assert.True(PagamentoStatusRules.PodeCobranca(StatusCobranca.Expirado, StatusCobranca.Pago));
    }

    [Fact]
    public void OrigemDecideAContaRecebedoraETokenErradoEBarrado()
    {
        Assert.Equal(ContextoCredencial.Loja, CredencialRegras.ContextoDaOrigem(OrigensPagamento.Cardapio));
        Assert.Equal(ContextoCredencial.Loja, CredencialRegras.ContextoDaOrigem(OrigensPagamento.Chat));
        Assert.Equal(ContextoCredencial.RecebedorZippy, CredencialRegras.ContextoDaOrigem(OrigensPagamento.Assinatura));
        Assert.Equal(ContextoCredencial.RecebedorZippy, CredencialRegras.ContextoDaOrigem(OrigensPagamento.CompraExtra));

        var loja = Guid.NewGuid();
        var daLoja = new CredencialRecebedora { Contexto = ContextoCredencial.Loja, IdEstabelecimento = loja, IdContaLoja = Guid.NewGuid(), MpUserId = 1 };
        var daZippy = new CredencialRecebedora { Contexto = ContextoCredencial.RecebedorZippy, IdContaZippy = Guid.NewGuid(), MpUserId = 2 };
        CredencialRegras.Exigir(daLoja, OrigensPagamento.Chat, loja);
        Assert.Throws<PagamentoException>(() => CredencialRegras.Exigir(daZippy, OrigensPagamento.Cardapio, loja));
        Assert.Throws<PagamentoException>(() => CredencialRegras.Exigir(daLoja, OrigensPagamento.Assinatura, loja));
        Assert.Throws<PagamentoException>(() => CredencialRegras.Exigir(daLoja, OrigensPagamento.Cardapio, Guid.NewGuid()));
        Assert.DoesNotContain("token", daLoja.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RetratoComOutroDonoOuOutroValorERejeitado()
    {
        var conta = Guid.NewGuid();
        var intent = new PagamentoIntent
        {
            Id = Guid.NewGuid(), IdEstabelecimento = Guid.NewGuid(), Ambiente = Ambientes.Sandbox, Origem = OrigensPagamento.Cardapio, ContaTipo = "loja",
            IdContaLoja = conta, Valor = 25.50m, ExternalReference = "zp_s_cardapio_x"
        };
        var cred = new CredencialRecebedora { Contexto = ContextoCredencial.Loja, Ambiente = Ambientes.Sandbox, IdEstabelecimento = intent.IdEstabelecimento, IdContaLoja = conta, MpUserId = 77 };
        MpPagamento Mp(long collector = 77, decimal valor = 25.50m, bool live = false, string referencia = "zp_s_cardapio_x") =>
            new() { Id = 1, Status = "approved", CollectorId = collector, TransactionAmount = valor, LiveMode = live, ExternalReference = referencia, CurrencyId = "BRL" };

        Assert.Null(PagamentoSincronizador.ValidarRetrato(intent, cred, Mp(), true));
        Assert.Equal("titularidade_divergente", PagamentoSincronizador.ValidarRetrato(intent, cred, Mp(collector: 99), true));
        Assert.Equal("valor_divergente", PagamentoSincronizador.ValidarRetrato(intent, cred, Mp(valor: 1m), true));
        Assert.Equal("live_mode_divergente", PagamentoSincronizador.ValidarRetrato(intent, cred, Mp(live: true), true));
        Assert.Equal("referencia_divergente", PagamentoSincronizador.ValidarRetrato(intent, cred, Mp(referencia: "zp_s_cardapio_y"), true));
    }

    [Fact]
    public void TokenPublicoEOpacoESoOHashEGuardado()
    {
        var token = TokenPublico.Gerar();
        Assert.True(TokenPublico.FormatoValido(token));
        Assert.False(TokenPublico.FormatoValido("../../etc"));
        Assert.Equal(64, TokenPublico.Hash(token).Length);
        Assert.NotEqual(token, TokenPublico.Hash(token));
        Assert.StartsWith("zp_p_chat_", PagamentoIntentSql.ExternalReference(Ambientes.Producao, OrigensPagamento.Chat, Guid.NewGuid()));
    }
}
