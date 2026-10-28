using APIBack.Acontecimentos;
using APIBack.GestaoLogica.Equipe;
using Xunit;

namespace APIBack.Tests.Unit;

/// <summary>Remuneração (mista, falta, noturno, comissão, adicionais), plano do livro e divisão do pagamento.</summary>
public class GestaoEquipeRegrasTests
{
    private static readonly RegraPagamento Motoboy = new(Diaria: 60, PorEntrega: 7);

    private static JornadaTurno J(string status, int entregas = 0, bool justificada = false, string entrada = "18:00", string saida = "23:00",
        PagamentoAvulso? avulso = null, decimal baseComissao = 0, params AdicionalTurno[] adicionais) =>
        new(entrada, saida, status, justificada, entregas, avulso, adicionais, baseComissao);

    [Fact]
    public void Motoboy_diaria_mais_entregas_mais_chuva_so_no_realizado()
    {
        var r = EquipeRegras.Calcular(Motoboy, J(StatusTurno.Realizado, 12, adicionais: new AdicionalTurno("Chuva", 10)));
        Assert.Equal((154m, 60m), (r.Valor, r.Previsto));
        Assert.Equal((60m, 84m, 10m), (r.Itens[0].Valor, r.Itens[1].Valor, r.Itens[2].Valor));
        Assert.StartsWith("12 entregas", r.Itens[1].Rotulo);
        Assert.Equal("Chuva", r.Itens[2].Rotulo);
        Assert.Equal(60m, EquipeRegras.Calcular(Motoboy, J(StatusTurno.Previsto, 12, adicionais: new AdicionalTurno("Chuva", 10))).Valor);
    }

    [Fact]
    public void Falta_sem_justificativa_zera_a_diaria_e_com_atestado_paga_a_base()
    {
        var sem = EquipeRegras.Calcular(Motoboy, J(StatusTurno.Falta, 5));
        Assert.Equal((0m, 60m), (sem.Valor, sem.Previsto));
        Assert.Equal(60m, EquipeRegras.Calcular(Motoboy, J(StatusTurno.Falta, 5, justificada: true)).Valor);
        Assert.Equal(0m, EquipeRegras.Calcular(Motoboy, J(StatusTurno.Cancelado)).Valor);
    }

    [Fact]
    public void Fixo_com_noturno_e_comissao_hora_avulsa_e_virada_da_meia_noite()
    {
        var fixo = new RegraPagamento(SalarioMensal: 2400, AdicionalNoturno: 15, ComissaoPercentual: 5);
        var r = EquipeRegras.Calcular(fixo, J(StatusTurno.Realizado, entrada: "18:00", saida: "00:00", baseComissao: 1000));
        Assert.Equal(80m + 15m + 50m, r.Valor);
        Assert.Equal(360, EquipeRegras.DuracaoMin("18:00", "00:00"));
        Assert.Equal(0, EquipeRegras.MinutosNoturnos("10:00", "16:00"));
        var hora = EquipeRegras.Calcular(Motoboy, J(StatusTurno.Realizado, 3, avulso: new PagamentoAvulso("hora", 20), entrada: "11:00", saida: "15:30"));
        Assert.Equal(90m, hora.Valor); // avulso substitui a regra: sem diária e sem entregas
    }

    [Fact]
    public void Normaliza_regra_pelo_vinculo()
    {
        var r = EquipeRegras.Normalizar(VinculoPessoa.Motoboy, new RegraPagamento(SalarioMensal: 9999, Diaria: 60, PorEntrega: 7));
        Assert.Equal(new RegraPagamento(Diaria: 60, PorEntrega: 7), r);
        Assert.Throws<GestaoException>(() => EquipeRegras.Normalizar(VinculoPessoa.Fixo, new RegraPagamento(Diaria: 60)));
        Assert.Throws<GestaoException>(() => EquipeRegras.Normalizar("socio", new RegraPagamento(PorTurno: 1)));
        Assert.Throws<GestaoException>(() => EquipeRegras.NormalizarEscala(new[] { new DiaPadrao(6, "18:00", "18:00") }));
    }

    [Fact]
    public void Plano_do_livro_publica_ajusta_estorna_ou_nada()
    {
        Assert.Equal(EquipeRegras.Plano.Publicar, EquipeRegras.PlanoDoLivro(false, 0, StatusTurno.Realizado, false, 154));
        Assert.Equal(EquipeRegras.Plano.Nada, EquipeRegras.PlanoDoLivro(false, 0, StatusTurno.Falta, false, 0));
        Assert.Equal(EquipeRegras.Plano.Ajustar, EquipeRegras.PlanoDoLivro(true, 154, StatusTurno.Realizado, false, 140));
        Assert.Equal(EquipeRegras.Plano.Estornar, EquipeRegras.PlanoDoLivro(true, 60, StatusTurno.Falta, false, 0));
        Assert.Equal(EquipeRegras.Plano.Nada, EquipeRegras.PlanoDoLivro(true, 60, StatusTurno.Falta, true, 60));
    }

    [Fact]
    public void Divide_pagamento_parcial_e_compensa_adiantamento_do_mais_antigo()
    {
        var a = new ItemDevido(Guid.NewGuid(), Guid.NewGuid(), "turno", new DateOnly(2026, 10, 23), "sex", 80, "a_pagar", new DateOnly(2026, 10, 1));
        var b = a with { LancamentoId = Guid.NewGuid(), Data = new DateOnly(2026, 10, 24), Valor = 60 };
        var (inteiros, parcial) = EquipeRegras.Dividir(new[] { b, a }, 100);
        Assert.Equal(a.LancamentoId, Assert.Single(inteiros).Id);
        Assert.Equal((b.LancamentoId, 20m, 40m), parcial);
        Assert.Throws<GestaoException>(() => EquipeRegras.Dividir(new[] { a }, 81));

        var adiantamento = new AdiantamentoDto(Guid.NewGuid(), new DateOnly(2026, 10, 20), 100, 0, 100);
        var comp = EquipeRegras.Compensar(new[] { adiantamento }, new[] { b, a });
        Assert.Equal(new[] { (a.LancamentoId, 80m), (b.LancamentoId, 20m) }, comp.Select(x => (x.LancamentoId, x.Valor)));
    }
}
