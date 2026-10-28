using APIBack.Acontecimentos;
using APIBack.GestaoLogica.Despesas;
using Xunit;

namespace APIBack.Tests.Unit;

/// <summary>Vencimento no mês, validação da conta fixa, limite de geração e lembretes.</summary>
public class GestaoRecorrenciaRegrasTests
{
    [Fact]
    public void Vencimento_cai_no_ultimo_dia_quando_o_mes_e_curto()
    {
        Assert.Equal(new DateOnly(2027, 2, 28), RecorrenciaRegras.Vencimento(new DateOnly(2027, 2, 1), 31));
        Assert.Equal(new DateOnly(2026, 10, 10), RecorrenciaRegras.Vencimento(new DateOnly(2026, 10, 1), 10));
    }

    [Fact]
    public void Valida_conta_e_mes_de_geracao()
    {
        Assert.Throws<GestaoException>(() => RecorrenciaRegras.Validar(new ContaFixaRequest("A", null, 10, 5)));
        Assert.Throws<GestaoException>(() => RecorrenciaRegras.Validar(new ContaFixaRequest("Aluguel", null, 0, 5)));
        Assert.Throws<GestaoException>(() => RecorrenciaRegras.Validar(new ContaFixaRequest("Aluguel", null, 10, 32)));
        Assert.Equal("Aluguel", RecorrenciaRegras.Validar(new ContaFixaRequest("  Aluguel ", " ", 10, 5)).Nome);
        RecorrenciaRegras.ValidarMesDeGeracao(new DateOnly(2026, 11, 1), new DateOnly(2026, 10, 28));
        Assert.Throws<GestaoException>(() => RecorrenciaRegras.ValidarMesDeGeracao(new DateOnly(2026, 12, 1), new DateOnly(2026, 10, 28)));
    }

    [Fact]
    public void Lembra_o_que_vence_logo_e_o_que_venceu_sem_pagar()
    {
        var hoje = new DateOnly(2026, 10, 28);
        OcorrenciaDto O(string nome, int dia, string estado, string? status = null) =>
            new(Guid.NewGuid(), nome, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 1).AddDays(dia - 1), 100, estado,
                estado == EstadoOcorrencia.Lancada ? Guid.NewGuid() : null, status);
        var lista = RecorrenciaRegras.Lembretes(new[]
        {
            O("Aluguel", 30, EstadoOcorrencia.Lancada, StatusLancamento.APagar),   // vence em 2 dias
            O("Luz", 20, EstadoOcorrencia.AguardandoValor),                        // venceu e ainda sem valor
            O("Internet", 25, EstadoOcorrencia.Lancada, StatusLancamento.Pago),    // paga: não lembra
            O("Contador", 31, EstadoOcorrencia.Prevista),                          // fora da janela de 2 dias
        }, new Dictionary<Guid, Guid>(), hoje, 2);
        Assert.Equal(new[] { ("Luz", true, -8), ("Aluguel", false, 2) }, lista.Select(l => (l.Nome, l.Vencida, l.DiasParaVencer)));
        Assert.Throws<GestaoException>(() => RecorrenciaRegras.Lembretes(Array.Empty<OcorrenciaDto>(), new Dictionary<Guid, Guid>(), hoje, 90));
    }
}
