using System;
using System.Linq;
using System.Text.Json;
using APIBack.Payments.Assinaturas;
using Xunit;

namespace APIBack.Tests.Unit;

/// <summary>Uma prova para cada linha das tabelas de "Regras da assinatura" (docs/assinatura/PROMPT-1-servidor.md).</summary>
public sealed class AssinaturaAcessoTests
{
    private static readonly DateTimeOffset Agora = new(2026, 11, 26, 15, 0, 0, TimeSpan.Zero);
    private static readonly DividaDto Deve = new(349m, Agora.AddDays(-31), new[] { Guid.NewGuid() });

    private static FatosAssinatura EmDia => new()
    {
        SituacaoLoja = "ativo", Status = StatusAssinatura.Ativa, CobrancaDoPeriodo = RegrasDeAcesso.StatusPeriodoPago
    };
    private static FatosAssinatura Devendo => EmDia with
    {
        Divida = Deve, TemCobrancaAPagar = true, CobrancaDoPeriodo = RegrasDeAcesso.StatusPeriodoAberto
    };
    private static FatosAssinatura Aguardando => new()
    {
        SituacaoLoja = "ativo", Status = StatusAssinatura.Pendente, TemCobrancaAPagar = true, CobrancaDoPeriodo = RegrasDeAcesso.StatusPeriodoAberto
    };
    private static FatosAssinatura Encerrada => new()
    {
        SituacaoLoja = "ativo", Status = StatusAssinatura.Encerrada, EncerramentoMotivo = Encerramentos.FimDoPeriodo
    };
    private static FatosAssinatura Teste => new() { SituacaoLoja = "teste", TesteTerminaEm = Agora.AddDays(5) };

    private static SituacaoAssinatura Avaliar(FatosAssinatura f) => RegrasDeAcesso.Avaliar(f, Agora);

    private static void Pode(params AcaoDto[] acoes) => Assert.All(acoes, a => { Assert.True(a.Pode, a.Motivo); Assert.Null(a.Codigo); });

    private static void Recusa(AcaoDto a, string codigo)
    {
        Assert.False(a.Pode);
        Assert.Equal(codigo, a.Codigo);
        Assert.False(string.IsNullOrWhiteSpace(a.Motivo));
    }

    // ---------- O que o cliente pode fazer: em dia x devendo ----------

    [Fact]
    public void EmDiaTrocaCompraCancelaEUsaCartao()
    {
        var s = Avaliar(EmDia);
        Assert.Equal(EstadosAssinatura.EmDia, s.Estado);
        Assert.Equal(MesesVigentes.Pago, s.MesVigente);
        Pode(s.Acoes.Upgrade, s.Acoes.Downgrade, s.Acoes.ComprarMotoboys, s.Acoes.ComprarModulo, s.Acoes.Cancelar, s.Acoes.CartaoAutomatico);
        Recusa(s.Acoes.Assinar, AssinaturaCodigos.JaExiste);
        // "Fatura já gerada": sem fatura, não há o que pagar.
        Assert.False(s.Acoes.Pagar.Pode);
        Pode(Avaliar(EmDia with { TemCobrancaAPagar = true }).Acoes.Pagar);
    }

    [Fact]
    public void DevendoPagaCompraMotoboysECancela()
    {
        var s = Avaliar(Devendo);
        Assert.Equal(EstadosAssinatura.Devendo, s.Estado);
        Assert.Equal(MesesVigentes.Atrasado, s.MesVigente);
        Pode(s.Acoes.Pagar, s.Acoes.ComprarMotoboys, s.Acoes.Cancelar);
    }

    [Fact]
    public void DevendoNaoTrocaNemCompraModulo()
    {
        var a = Avaliar(Devendo with { TrocaMarcada = true }).Acoes;
        Recusa(a.Upgrade, AssinaturaCodigos.Devendo);
        Recusa(a.Downgrade, AssinaturaCodigos.Devendo);
        Recusa(a.ComprarModulo, AssinaturaCodigos.ModuloDevendo);
        Recusa(a.DesfazerTroca, AssinaturaCodigos.Devendo);
        Recusa(a.CartaoAutomatico, AssinaturaCodigos.Devendo);
        Recusa(Avaliar(Devendo with { Cancelada = true }).Acoes.Retomar, AssinaturaCodigos.Devendo);
    }

    [Fact]
    public void AnualValendoViraMensalSoPelaGestaoEmDiaOuDevendo()
    {
        var anual = EmDia with { Periodicidade = Periodicidades.Anual };
        Recusa(Avaliar(anual).Acoes.TrocaParaMensal, AssinaturaCodigos.AnualSoGestao);
        Recusa(Avaliar(Devendo with { Periodicidade = Periodicidades.Anual }).Acoes.TrocaParaMensal, AssinaturaCodigos.AnualSoGestao);
        // Um ano já pago à frente também está valendo.
        Recusa(Avaliar(EmDia with { AnualPagoAFrente = true }).Acoes.TrocaParaMensal, AssinaturaCodigos.AnualSoGestao);
        // Anual ainda não pago: a loja troca.
        Pode(Avaliar(Aguardando with { Periodicidade = Periodicidades.Anual }).Acoes.TrocaParaMensal);
    }

    [Theory]
    [InlineData(StatusAssinatura.Pendente)]
    [InlineData(StatusAssinatura.Encerrada)]
    [InlineData(null)]
    public void ExtraSoComPlanoAtivo(string? status)
    {
        var a = Avaliar(new FatosAssinatura { SituacaoLoja = "ativo", Status = status }).Acoes;
        Recusa(a.ComprarMotoboys, AssinaturaCodigos.ExtraSemPlano);
        Recusa(a.ComprarModulo, AssinaturaCodigos.ExtraSemPlano);
    }

    // ---------- Por situação ----------

    [Fact]
    public void SemAssinaturaSoAssina()
    {
        var s = Avaliar(new FatosAssinatura { SituacaoLoja = "ativo" });
        Assert.Equal(EstadosAssinatura.SemAssinatura, s.Estado);
        Assert.Equal(MesesVigentes.SemCobranca, s.MesVigente);
        Pode(s.Acoes.Assinar);
        Assert.All(new[] { s.Acoes.Upgrade, s.Acoes.Downgrade, s.Acoes.Retomar, s.Acoes.Cancelar, s.Acoes.CartaoAutomatico },
            a => Recusa(a, AssinaturaCodigos.SemAssinatura));
    }

    [Fact]
    public void NoTesteAssinaETrocaLivremente()
    {
        var sem = Avaliar(Teste);
        Assert.Equal(EstadosAssinatura.Teste, sem.Estado);
        Pode(sem.Acoes.Assinar);
        var escolhido = Avaliar(Teste with { Status = StatusAssinatura.Pendente, TemCobrancaAPagar = true, CobrancaDoPeriodo = "aberta" });
        Assert.Equal(EstadosAssinatura.Teste, escolhido.Estado);
        Assert.Equal(MesesVigentes.EmAberto, escolhido.MesVigente);
        Pode(escolhido.Acoes.Upgrade, escolhido.Acoes.Downgrade, escolhido.Acoes.Pagar, escolhido.Acoes.Cancelar);
        // Teste vencido sem assinatura: nada trava, só volta a ser "sem assinatura".
        Assert.Equal(EstadosAssinatura.SemAssinatura, Avaliar(Teste with { TesteTerminaEm = Agora.AddDays(-3) }).Estado);
    }

    [Fact]
    public void AguardandoOPrimeiroPagamentoPagaTrocaOuDesiste()
    {
        var s = Avaliar(Aguardando);
        Assert.Equal(EstadosAssinatura.AguardandoPagamento, s.Estado);
        Assert.Equal(MesesVigentes.EmAberto, s.MesVigente);
        Pode(s.Acoes.Pagar, s.Acoes.Upgrade, s.Acoes.Downgrade, s.Acoes.Cancelar);
        Recusa(s.Acoes.ComprarMotoboys, AssinaturaCodigos.ExtraSemPlano);
    }

    [Fact]
    public void TrocaMarcadaPodeSerDesfeita()
    {
        Pode(Avaliar(EmDia with { TrocaMarcada = true }).Acoes.DesfazerTroca);
        Assert.False(Avaliar(EmDia).Acoes.DesfazerTroca.Pode);
        // Próxima fatura já paga com o plano novo: não há mais o que desfazer antes dela.
        Assert.False(Avaliar(EmDia with { TrocaMarcada = true, ProximaPagaComATroca = true }).Acoes.DesfazerTroca.Pode);
    }

    [Fact]
    public void CanceladaComPeriodoPagoRetomaAntesDeTrocar()
    {
        var s = Avaliar(EmDia with { Cancelada = true });
        Assert.Equal(EstadosAssinatura.CanceladaVigente, s.Estado);
        Pode(s.Acoes.Retomar, s.Acoes.ComprarMotoboys);
        Recusa(s.Acoes.Upgrade, AssinaturaCodigos.CanceladaRetome);
        Recusa(s.Acoes.Downgrade, AssinaturaCodigos.CanceladaRetome);
        Recusa(s.Acoes.CartaoAutomatico, AssinaturaCodigos.CanceladaRetome);
        Assert.False(s.Acoes.Cancelar.Pode);
    }

    [Fact]
    public void EncerradaAssinaDeNovoSemRetomarNemTrocar()
    {
        var s = Avaliar(Encerrada);
        Assert.Equal(EstadosAssinatura.Encerrada, s.Estado);
        Pode(s.Acoes.Assinar);
        Assert.False(s.Acoes.Retomar.Pode);
        Assert.False(s.Acoes.Upgrade.Pode);
        Assert.False(s.Acoes.Downgrade.Pode);
        // Vinda do anual, sem dívida, assina o que quiser.
        Pode(Avaliar(Encerrada with { Periodicidade = Periodicidades.Anual }).Acoes.Assinar);
    }

    [Fact]
    public void EncerradaComDividaPagaEPodeAssinarDeNovo()
    {
        var s = Avaliar(Encerrada with { Divida = Deve, TemCobrancaAPagar = true });
        Assert.Equal(EstadosAssinatura.Devendo, s.Estado);
        Pode(s.Acoes.Assinar, s.Acoes.Pagar);
    }

    [Fact]
    public void QuemDesistiuAntesDePagarVoltaASemAssinatura()
    {
        var desistiu = Encerrada with { EncerramentoMotivo = Encerramentos.AntesDoPagamento };
        Assert.Equal(EstadosAssinatura.SemAssinatura, Avaliar(desistiu).Estado);
        Assert.Equal(EstadosAssinatura.Teste, Avaliar(desistiu with { SituacaoLoja = "teste", TesteTerminaEm = Agora.AddDays(1) }).Estado);
    }

    [Fact]
    public void CortesiaESemCobranca() =>
        Assert.Equal(MesesVigentes.SemCobranca, Avaliar(EmDia with { CobrancaDoPeriodo = null }).MesVigente);

    // ---------- dívida ----------

    private static MensalidadeAberta Aberta(int diasAtras, decimal valor = 349m, decimal divida = 0, int? dividaDias = null)
    {
        var hoje = FusoComercial.Data(Agora);
        return new MensalidadeAberta(Guid.NewGuid(), valor, FusoComercial.InicioDoDia(hoje.AddDays(-diasAtras)), divida,
            dividaDias.HasValue ? FusoComercial.InicioDoDia(hoje.AddDays(-dividaDias.Value)) : null);
    }

    [Fact]
    public void DividaComecaNoDiaSeguinteAoVencimento()
    {
        Assert.Null(Divida.De(new[] { Aberta(-3), Aberta(0) }, Agora));
        var d = Divida.De(new[] { Aberta(1), Aberta(31, 149m), Aberta(-5) }, Agora)!;
        Assert.Equal(498m, d.Valor);
        Assert.Equal(2, d.Cobrancas.Count);
        Assert.Equal(FusoComercial.InicioDoDia(FusoComercial.Data(Agora).AddDays(-31)), d.Desde);
    }

    [Fact]
    public void VoltaComDividaDeveSoAParteAntiga()
    {
        // 349 de dívida + 549 do primeiro período, para pagar hoje: a dívida são os 349, desde a data antiga.
        var d = Divida.De(new[] { Aberta(0, 898m, divida: 349m, dividaDias: 40) }, Agora)!;
        Assert.Equal(349m, d.Valor);
        Assert.Equal(349m, Divida.De(new[] { Aberta(10, 898m, divida: 349m, dividaDias: 40) }, Agora)!.Valor);
    }

    // ---------- contrato ----------

    [Fact]
    public void AcoesSaemComAsChavesDoContrato()
    {
        var json = JsonSerializer.Serialize(Avaliar(EmDia).Acoes, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(new[] { "assinar", "pagar", "upgrade", "downgrade", "trocaParaMensal", "cancelar", "retomar", "desfazerTroca", "comprarMotoboys", "comprarModulo", "cartaoAutomatico" },
            doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(new[] { "pode", "motivo", "codigo" }, doc.RootElement.GetProperty("assinar").EnumerateObject().Select(p => p.Name).ToArray());
    }

    [Fact]
    public void PainelMantemOsCamposAntigosEGanhaOsNovos()
    {
        var campos = typeof(PainelAssinaturaDto).GetProperties().Select(p => p.Name).ToHashSet();
        foreach (var antigo in new[] { "Assinatura", "Situacao", "Planos", "Extras", "Compras", "Solicitacoes", "Avisos", "Checkout", "PodeGerenciar", "ToleranciaDias" })
            Assert.Contains(antigo, campos);
        foreach (var novo in new[] { "Estado", "Acoes", "MesVigente", "Divida", "Cartao", "Bloqueio" })
            Assert.Contains(novo, campos);
        Assert.NotNull(typeof(CobrancaDto).GetProperty("ExpiraEm"));
        Assert.NotNull(typeof(CobrancaDto).GetProperty("ContaComoDivida"));
    }
}
