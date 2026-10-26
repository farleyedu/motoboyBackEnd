using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    [Theory]
    [InlineData("created", "created", 0, StatusPagamento.Pendente)]
    [InlineData("action_required", "waiting_transfer", 0, StatusPagamento.Pendente)]
    [InlineData("action_required", "waiting_capture", 0, StatusPagamento.Autorizado)]
    [InlineData("processing", "in_process", 0, StatusPagamento.EmProcesso)]
    [InlineData("in_review", "in_review", 0, StatusPagamento.EmProcesso)]
    [InlineData("processed", "accredited", 0, StatusPagamento.Aprovado)]
    [InlineData("processed", "partially_refunded", 4, StatusPagamento.EstornadoParcial)]
    [InlineData("processed", "partially_refunded", 0, StatusPagamento.EstornadoParcial)]
    [InlineData("refunded", "refunded", 10, StatusPagamento.Estornado)]
    [InlineData("canceled", "canceled", 0, StatusPagamento.Cancelado)]
    [InlineData("canceled", "expired", 0, StatusPagamento.Expirado)]
    [InlineData("expired", "expired", 0, StatusPagamento.Expirado)]
    [InlineData("failed", "expired", 0, StatusPagamento.Expirado)]
    [InlineData("failed", "cc_rejected_other_reason", 0, StatusPagamento.Recusado)]
    [InlineData("charged_back", "in_process", 0, StatusPagamento.Contestado)]
    [InlineData("charged_back", "settled", 0, StatusPagamento.Contestado)]
    [InlineData("charged_back", "reimbursed", 0, StatusPagamento.Aprovado)]
    public void TraduzStatusDaOrdersApi(string mp, string? detalhe, decimal estornado, string esperado) =>
        Assert.Equal(esperado, PagamentoStatusRules.Traduzir(mp, detalhe, 10m, estornado));

    [Theory]
    [InlineData("failed", "insufficient_amount", "cc_rejected_insufficient_amount")]
    [InlineData("failed", "card_insufficient_amount", "cc_rejected_insufficient_amount")]
    [InlineData("failed", "bad_filled_card_data", "cc_rejected_bad_filled_other")]
    [InlineData("failed", "required_call_for_authorize", "cc_rejected_call_for_authorize")]
    [InlineData("failed", "rejected_by_issuer", "cc_rejected_other_reason")]
    [InlineData("failed", "expired", "expired")]
    [InlineData("processed", "accredited", "accredited")]
    public void RecusaDaOrdersViraCodigoQueOFrontJaConhece(string status, string detalhe, string esperado) =>
        Assert.Equal(esperado, PagamentoStatusRules.DetalheCompativel(status, detalhe));

    [Fact]
    public void OrderDoPixViraORetratoInternoComIdNumericoDoReferenceId()
    {
        const string json = @"{""id"":""ORD01J67CQQ"",""type"":""online"",""status"":""action_required"",""status_detail"":""waiting_transfer"",
            ""external_reference"":""zp_s_cardapio_x"",""total_amount"":""50.00"",""user_id"":""77"",""created_date"":""2026-11-02T10:00:00Z"",
            ""last_updated_date"":""2026-11-02T10:00:01Z"",""transactions"":{""payments"":[{""id"":""PAY01J67"",""reference_id"":""1234567890"",
            ""status"":""action_required"",""status_detail"":""waiting_transfer"",""amount"":""50.00"",
            ""payment_method"":{""id"":""pix"",""type"":""bank_transfer"",""ticket_url"":""https://mp/t"",""qr_code"":""00020126pix"",""qr_code_base64"":""iVBOR""}}]}}";
        var mp = JsonSerializer.Deserialize<MpOrder>(json)!.ParaPagamento(liveModePadrao: true);

        Assert.Equal(1234567890L, mp.Id);
        Assert.Equal("ORD01J67CQQ", mp.OrderId);
        Assert.Equal(77, mp.CollectorId);
        Assert.Equal(50m, mp.TransactionAmount);
        Assert.True(mp.LiveMode);
        Assert.Equal("pix", mp.PaymentMethodId);
        Assert.Equal("00020126pix", mp.TransactionData!.QrCode);
        Assert.Equal(StatusPagamento.Pendente, PagamentoStatusRules.Traduzir(mp.Status, mp.StatusDetail, mp.TransactionAmount, mp.TransactionAmountRefunded));
    }

    [Fact]
    public void OrderEstornadaTrazOValorEstornadoPelosRefunds()
    {
        const string json = @"{""id"":""ORD1"",""status"":""processed"",""status_detail"":""partially_refunded"",""total_amount"":""10.00"",""user_id"":""77"",
            ""transactions"":{""payments"":[{""id"":""PAY1"",""reference_id"":""55"",""status"":""processed"",""status_detail"":""partially_refunded"",""amount"":""10.00"",
            ""payment_method"":{""id"":""master"",""type"":""credit_card"",""installments"":3}}],
            ""refunds"":[{""id"":""REF1"",""reference_id"":""900"",""amount"":""4.00"",""status"":""processed""},{""id"":""REF2"",""amount"":""1.00"",""status"":""failed""}]}}";
        var mp = JsonSerializer.Deserialize<MpOrder>(json)!.ParaPagamento(false);

        Assert.Equal(4m, mp.TransactionAmountRefunded);
        Assert.Equal(900, Assert.Single(mp.Refunds!, r => r.Status == "approved").Id);
        Assert.Equal(3, mp.Installments);
        Assert.Equal(StatusPagamento.EstornadoParcial, PagamentoStatusRules.Traduzir(mp.Status, mp.StatusDetail, mp.TransactionAmount, mp.TransactionAmountRefunded));
    }

    [Fact]
    public void SemReferenceIdOIdNumericoSaiDeUmHashEstavel()
    {
        var a = MpOrder.IdNumerico(null, "PAY01J67CQQ");
        Assert.True(a > 0);
        Assert.Equal(a, MpOrder.IdNumerico("", "PAY01J67CQQ"));
        Assert.NotEqual(a, MpOrder.IdNumerico(null, "PAY01J67CQR"));
    }

    [Fact]
    public void CorpoDaCriacaoDaOrderUsaValoresEmTextoEOsCamposDaOrdersApi()
    {
        var pedido = new MpCriarOrderRequest { TotalAmount = MpCriarOrderRequest.Moeda(50m), ExternalReference = "zp_s_cardapio_x" };
        pedido.Pagamento.Amount = pedido.TotalAmount;
        pedido.Pagamento.PaymentMethod = new MpOrderMetodoRequest { Id = "master", Type = "credit_card", Token = "tok", Installments = 2 };
        var corpo = JsonNode.Parse(JsonSerializer.Serialize(pedido))!;

        Assert.Equal("online", corpo["type"]!.GetValue<string>());
        Assert.Equal("automatic", corpo["processing_mode"]!.GetValue<string>());
        Assert.Equal("50.00", corpo["total_amount"]!.GetValue<string>());
        Assert.Equal("50.00", corpo["transactions"]!["payments"]![0]!["amount"]!.GetValue<string>());
        Assert.Equal("tok", corpo["transactions"]!["payments"]![0]!["payment_method"]!["token"]!.GetValue<string>());
        Assert.Null(corpo["transactions"]!["payments"]![0]!["expiration_time"]);
        Assert.Null(corpo["marketplace_fee"]);
    }

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
