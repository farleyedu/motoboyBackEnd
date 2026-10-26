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
    [InlineData(-1, "em_dia", 0)]     // ainda nao venceu
    [InlineData(0, "em_atraso", 0)]   // venceu hoje: tolerancia
    [InlineData(3, "em_atraso", 3)]
    [InlineData(6, "em_atraso", 6)]
    [InlineData(7, "suspensa", 7)]    // passou a tolerancia de 7 dias
    [InlineData(20, "suspensa", 20)]
    public void InadimplenciaComToleranciaDeSeteDias(int diasDepois, string esperado, int atraso)
    {
        var venc = D(2026, 4, 1);
        var r = Inadimplencia.Avaliar(venc, venc.AddDays(diasDepois), 7);
        Assert.Equal(esperado, r.Financeiro);
        Assert.Equal(atraso, r.DiasAtraso);
    }

    [Fact]
    public void SemDividaEstaEmDiaEToleranciaConfiguravel()
    {
        Assert.Equal(EstadoFinanceiro.EmDia, Inadimplencia.Avaliar(null, D(2026, 4, 1), 7).Financeiro);
        Assert.Equal(EstadoFinanceiro.Suspensa, Inadimplencia.Avaliar(D(2026, 4, 1), D(2026, 4, 4), 3).Financeiro);
        Assert.Equal(EstadoFinanceiro.Suspensa, Inadimplencia.Avaliar(D(2026, 4, 1), D(2026, 4, 1), 0).Financeiro);
    }

    [Fact]
    public void AvisosNoVencimentoNoTerceiroDiaENaVesperaDoBloqueio()
    {
        var id = Guid.NewGuid();
        var venc = D(2026, 4, 1);
        var politica = new PoliticaInadimplencia(7, new[] { 3, 1 }, new[] { 1, 3, 6 });
        string[] Tipos(int dia) => Inadimplencia.AvisosDevidos(id, "Plano", 149m, venc, venc.AddDays(dia), politica).Select(a => a.Chave.Split(':', 2)[1]).ToArray();

        Assert.Contains("antes:3", Tipos(-3));
        Assert.Contains("vencimento", Tipos(0));
        Assert.Contains("atraso:3", Tipos(3));
        Assert.Contains("antes_bloqueio", Tipos(6));
        // Depois de suspensa nao ha mais aviso de atraso (o aviso de suspensao sai pela transicao).
        Assert.DoesNotContain("atraso:3", Tipos(8));
        Assert.DoesNotContain("antes_bloqueio", Tipos(8));
        // Chaves estaveis: rodar de novo no mesmo dia devolve as mesmas (o banco ignora repetidas).
        Assert.Equal(Tipos(3), Tipos(3));
        // O 3o dia sai mesmo se a politica nao listar.
        var sem3 = new PoliticaInadimplencia(7, Array.Empty<int>(), Array.Empty<int>());
        Assert.Contains(Inadimplencia.AvisosDevidos(id, "Plano", 149m, venc, venc.AddDays(3), sem3), a => a.Chave.EndsWith("atraso:3"));
    }

    [Theory]
    [InlineData("/api/pedido", "POST", true)]                          // pedido novo para
    [InlineData("/api/v2/delivery/pedidos", "POST", true)]
    [InlineData("/api/pedido", "GET", false)]                          // historico continua
    [InlineData("/api/v2/comercial/cobrancas/x/pagar", "POST", false)] // pagar continua
    [InlineData("/api/v2/chamados", "POST", false)]                    // suporte continua
    [InlineData("/api/v2/delivery/pedidos/10/entregue", "POST", false)]// entrega iniciada termina
    [InlineData("/api/v2/delivery/pedidos/10/saiu", "POST", false)]
    [InlineData("/api/v2/motoboys/me/session/stops/current/deliver", "POST", false)]
    [InlineData("/hubs/delivery/negotiate", "POST", false)]
    [InlineData("/api/v2/delivery/configuracoes", "PUT", false)]
    public void SuspensaoParaSoOperacaoNova(string path, string metodo, bool bloqueia) =>
        Assert.Equal(bloqueia, AcessoFinanceiroRules.Bloqueia(path, metodo, APIBack.Service.SituacaoComercialRules.PermitidoQuandoBloqueado(path, metodo)));

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
