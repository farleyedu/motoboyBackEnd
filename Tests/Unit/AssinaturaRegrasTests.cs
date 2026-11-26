using System;
using System.Linq;
using APIBack.Payments.Assinaturas;
using Xunit;

namespace APIBack.Tests.Unit;

public sealed class AssinaturaRegrasTests
{
    private static DateOnly D(int a, int m, int d) => new(a, m, d);

    [Theory]
    [InlineData(2026, 1, 31, 1, 2026, 2, 28)]   // janeiro 31 -> fevereiro 28
    [InlineData(2028, 1, 31, 1, 2028, 2, 29)]   // bissexto
    [InlineData(2026, 1, 31, 2, 2026, 3, 31)]   // volta ao 31 (nao encadeia o 28)
    [InlineData(2026, 1, 31, 3, 2026, 4, 30)]
    [InlineData(2026, 1, 30, 1, 2026, 2, 28)]
    [InlineData(2026, 3, 31, 11, 2027, 2, 28)]  // atravessa o ano
    [InlineData(2026, 12, 15, 1, 2027, 1, 15)]
    [InlineData(2028, 2, 29, 12, 2029, 2, 28)]  // anual de um 29/02
    [InlineData(2028, 2, 29, 48, 2032, 2, 29)]  // e volta ao 29 no bissexto seguinte
    public void SomarMesesRespeitaFimDeMesEBissexto(int a, int m, int d, int meses, int ea, int em, int ed) =>
        Assert.Equal(D(ea, em, ed), CicloDatas.Somar(D(a, m, d), meses));

    [Fact]
    public void CiclosMensaisDe31NaoDerivam()
    {
        var ancora = D(2026, 1, 31);
        var fins = Enumerable.Range(1, 6).Select(n => CicloDatas.Ciclo(ancora, n, Periodicidades.Mensal).Fim).ToArray();
        Assert.Equal(new[] { D(2026, 2, 28), D(2026, 3, 31), D(2026, 4, 30), D(2026, 5, 31), D(2026, 6, 30), D(2026, 7, 31) }, fins);
        // Cada ciclo comeca onde o anterior terminou: nenhum dia perdido ou pago duas vezes.
        for (var n = 1; n < 6; n++)
            Assert.Equal(CicloDatas.Ciclo(ancora, n, Periodicidades.Mensal).Fim, CicloDatas.Ciclo(ancora, n + 1, Periodicidades.Mensal).Inicio);
        Assert.Equal(28, CicloDatas.Dias(D(2026, 1, 31), D(2026, 2, 28)));
        Assert.Equal(29, CicloDatas.Dias(D(2028, 1, 31), D(2028, 2, 29)));
    }

    [Fact]
    public void CicloAnualTemDozeMeses()
    {
        var (ini, fim) = CicloDatas.Ciclo(D(2026, 10, 26), 2, Periodicidades.Anual);
        Assert.Equal(D(2027, 10, 26), ini);
        Assert.Equal(D(2028, 10, 26), fim);
        Assert.Equal(366, CicloDatas.Dias(ini, fim)); // passa por 29/02/2028
    }

    [Fact]
    public void InicioDoDiaEmSaoPaulo()
    {
        var instante = FusoComercial.InicioDoDia(D(2026, 11, 1));
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 3, 0, 0, TimeSpan.Zero), instante);
        Assert.Equal(D(2026, 11, 1), FusoComercial.Data(instante));
        // 01h UTC ainda e o dia anterior em Sao Paulo.
        Assert.Equal(D(2026, 10, 31), FusoComercial.Data(new DateTimeOffset(2026, 11, 1, 1, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void UpgradeCobraDiferencaProporcional()
    {
        // Ciclo de 30 dias (01/04 a 01/05), faltando 10: (249 - 149) x 10/30 = 33,33.
        var p = Proporcional.Calcular(149m, "mensal", 249m, "mensal", D(2026, 4, 1), D(2026, 5, 1), D(2026, 4, 21), mesmoPlano: false);
        Assert.Equal(TiposMudanca.Upgrade, p.Tipo);
        Assert.Equal(10, p.DiasRestantes);
        Assert.Equal(30, p.DiasCiclo);
        Assert.Equal(33.33m, p.ValorAgora);
        Assert.Equal("agora", p.Aplicacao);
        Assert.NotEmpty(p.Explicacao);
    }

    [Fact]
    public void UpgradeEmFevereiroUsaOsDiasReaisDoCiclo()
    {
        // 31/01 -> 28/02 tem 28 dias; faltando 14: 200 x 14/28 = 100,00.
        var p = Proporcional.Calcular(149m, "mensal", 349m, "mensal", D(2026, 1, 31), D(2026, 2, 28), D(2026, 2, 14), false);
        Assert.Equal(28, p.DiasCiclo);
        Assert.Equal(100.00m, p.ValorAgora);
    }

    [Fact]
    public void UpgradeNoUltimoDiaSaiDeGraca()
    {
        var p = Proporcional.Calcular(149m, "mensal", 249m, "mensal", D(2026, 4, 1), D(2026, 5, 1), D(2026, 5, 1), false);
        Assert.Equal(0, p.DiasRestantes);
        Assert.Equal(0m, p.ValorAgora);
        // Abaixo de R$ 1: nao gera Pix.
        var pequeno = Proporcional.Calcular(149m, "mensal", 150m, "mensal", D(2026, 4, 1), D(2026, 5, 1), D(2026, 4, 2), false);
        Assert.Equal(0m, pequeno.ValorAgora);
    }

    [Fact]
    public void DowngradeFicaParaOFimDoPeriodo()
    {
        var p = Proporcional.Calcular(349m, "mensal", 149m, "mensal", D(2026, 4, 1), D(2026, 5, 1), D(2026, 4, 10), false);
        Assert.Equal(TiposMudanca.Downgrade, p.Tipo);
        Assert.Equal(0m, p.ValorAgora);
        Assert.Equal("fim_do_periodo", p.Aplicacao);
        Assert.Equal(D(2026, 5, 1), p.AplicaEm);
        // Anual para mensal tambem espera o fim do ano pago.
        var anual = Proporcional.Calcular(1490m, "anual", 249m, "mensal", D(2026, 1, 1), D(2027, 1, 1), D(2026, 6, 1), false);
        Assert.Equal(TiposMudanca.Downgrade, anual.Tipo);
    }

    [Fact]
    public void MensalParaAnualDescontaOQueSobrou()
    {
        // 149 x 15/30 = 74,50 de credito; 1490 - 74,50 = 1415,50.
        var p = Proporcional.Calcular(149m, "mensal", 1490m, "anual", D(2026, 4, 1), D(2026, 5, 1), D(2026, 4, 16), true);
        Assert.Equal(TiposMudanca.ParaAnual, p.Tipo);
        Assert.Equal(74.50m, p.Credito);
        Assert.Equal(1415.50m, p.ValorAgora);
    }

    [Fact]
    public void MesmoPlanoNaoMuda()
    {
        var p = Proporcional.Calcular(149m, "mensal", 149m, "mensal", D(2026, 4, 1), D(2026, 5, 1), D(2026, 4, 16), true);
        Assert.Equal(TiposMudanca.Nenhuma, p.Tipo);
    }

    [Theory]
    [InlineData(-1, "em_dia", 0)]     // ainda não venceu
    [InlineData(0, "em_dia", 0)]      // vence hoje: ainda está no prazo
    [InlineData(1, "em_atraso", 1)]   // devendo a partir do dia seguinte
    [InlineData(30, "em_atraso", 30)] // trinta dias: continua só em atraso, nada suspende
    [InlineData(400, "em_atraso", 400)]
    public void DevendoSoAPartirDoDiaSeguinte(int diasDepois, string esperado, int atraso)
    {
        var venc = D(2026, 4, 1);
        var r = Inadimplencia.Avaliar(venc, venc.AddDays(diasDepois));
        Assert.Equal(esperado, r.Financeiro);
        Assert.Equal(atraso, r.DiasAtraso);
        Assert.Equal(EstadoFinanceiro.EmDia, Inadimplencia.Avaliar(null, venc).Financeiro);
    }

    [Fact]
    public void AvisosInformamSemAmeacar()
    {
        var id = Guid.NewGuid();
        var venc = D(2026, 4, 1);
        var politica = new PoliticaInadimplencia(7, new[] { 3, 1 }, new[] { 1, 3, 30 });
        var todos = Enumerable.Range(-3, 45).SelectMany(d => Inadimplencia.AvisosDevidos(id, "Plano", 149m, venc, venc.AddDays(d), politica)).ToList();
        string[] Chaves(int dia) => Inadimplencia.AvisosDevidos(id, "Plano", 149m, venc, venc.AddDays(dia), politica).Select(a => a.Chave.Split(':', 2)[1]).ToArray();

        Assert.Contains("antes:3", Chaves(-3));
        Assert.Contains("vencimento", Chaves(0));
        Assert.Contains("atraso:3", Chaves(3));
        Assert.Contains("atraso:30", Chaves(30));
        Assert.All(todos, a =>
        {
            Assert.DoesNotContain("suspen", a.Mensagem, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("bloque", a.Mensagem, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("param", a.Mensagem, StringComparison.OrdinalIgnoreCase);
        });
        // Chaves estaveis: rodar de novo no mesmo dia devolve as mesmas (o banco ignora repetidas).
        Assert.Equal(Chaves(3), Chaves(3));
    }

    [Fact]
    public void ExemploDoUpgradeDe349Para549ComMeioMes()
    {
        var p = Proporcional.Calcular(349m, "mensal", 549m, "mensal", D(2026, 4, 1), D(2026, 5, 1), D(2026, 4, 16), false);
        Assert.Equal(100.00m, p.ValorAgora);
    }

    [Fact]
    public void ProximaFaturaPagaSomaADiferencaDelaNoUpgrade()
    {
        // 100 do resto do mês + 200 da próxima fatura, paga a R$ 349.
        var p = Proporcional.Calcular(349m, "mensal", 549m, "mensal", D(2026, 4, 1), D(2026, 5, 1), D(2026, 4, 16), false, proximoPago: 349m);
        Assert.Equal(300.00m, p.ValorAgora);
    }

    [Fact]
    public void AnualDePlanoMenorEsperaOFimDoMesPago()
    {
        var menor = Proporcional.Calcular(349m, "mensal", 1490m, "anual", D(2026, 4, 1), D(2026, 5, 1), D(2026, 4, 16), false, precoMensalNovo: 149m);
        Assert.Equal(TiposMudanca.Downgrade, menor.Tipo);
        Assert.Equal(D(2026, 5, 1), menor.AplicaEm);
        // O mesmo plano no anual, ou um maior, começa no pagamento com desconto dos dias não usados.
        var maior = Proporcional.Calcular(149m, "mensal", 3490m, "anual", D(2026, 4, 1), D(2026, 5, 1), D(2026, 4, 16), false, precoMensalNovo: 349m);
        Assert.Equal(TiposMudanca.ParaAnual, maior.Tipo);
        Assert.Equal(3415.50m, maior.ValorAgora);
    }

    [Fact]
    public void AnualParaOutroAnualUsaOsDiasDoAno()
    {
        var sobe = Proporcional.Calcular(1490m, "anual", 3490m, "anual", D(2026, 1, 1), D(2027, 1, 1), D(2026, 7, 2), false);
        Assert.Equal(TiposMudanca.Upgrade, sobe.Tipo);
        Assert.Equal(1002.74m, sobe.ValorAgora); // 2000 x 183/365
        var desce = Proporcional.Calcular(3490m, "anual", 1490m, "anual", D(2026, 1, 1), D(2027, 1, 1), D(2026, 7, 2), false);
        Assert.Equal(TiposMudanca.Downgrade, desce.Tipo);
        Assert.Equal(D(2027, 1, 1), desce.AplicaEm);
    }

    [Fact]
    public void SemMesPagoNaoHaCreditoNoAnual()
    {
        var p = Proporcional.Calcular(149m, "mensal", 1490m, "anual", D(2026, 4, 1), D(2026, 5, 1), D(2026, 4, 1), true, cicloPago: false);
        Assert.Equal(0m, p.Credito);
        Assert.Equal(1490m, p.ValorAgora);
    }

    [Fact]
    public void PrecoAnualZeroNaoTemOpcaoAnual()
    {
        Assert.Null(new PlanoCatalogoRow { Preco = 149m, PrecoAnual = 0m }.PrecoDe(Periodicidades.Anual));
        Assert.Equal(1490m, new PlanoCatalogoRow { Preco = 149m, PrecoAnual = 1490m }.PrecoDe(Periodicidades.Anual));
    }

    [Fact]
    public void ReferenciaDoPreapprovalNaoColideComONucleo()
    {
        var id = Guid.NewGuid();
        var r = PreapprovalReferencia.Gerar("sandbox", id);
        Assert.StartsWith("zpa_s_", r);
        Assert.False(APIBack.Payments.MercadoPago.PagamentoIntentSql.ReferenciaNossa(r));
        Assert.Equal(id, PreapprovalReferencia.Assinatura(r));
        Assert.Null(PreapprovalReferencia.Assinatura("zp_s_assinatura_" + id.ToString("N")));
    }
}
