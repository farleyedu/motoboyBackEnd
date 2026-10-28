using APIBack.Acontecimentos;

namespace APIBack.GestaoLogica.Caixa;

/// <summary>Regras puras do caixa (as mesmas do caixaModelo.ts do Painel): contagem, diferença, troco que fica e movimentos.</summary>
public static class CaixaRegras
{
    public static readonly int[] Notas = { 200, 100, 50, 20, 10, 5, 2 };

    public static decimal TotalContado(ContagemCaixa c)
    {
        if (c.Moedas < 0 || (c.Notas ?? new Dictionary<int, int>()).Any(n => n.Value < 0)) throw Invalido("Contagem não pode ser negativa.");
        if ((c.Notas ?? new Dictionary<int, int>()).Keys.Any(k => !Notas.Contains(k))) throw Invalido("Cédula desconhecida.");
        return R2(Notas.Sum(v => v * (c.Notas?.GetValueOrDefault(v) ?? 0)) + c.Moedas);
    }

    /// <summary>Contado − esperado: negativo = faltou, positivo = sobrou.</summary>
    public static decimal Diferenca(decimal esperado, decimal contado) => R2(contado - esperado);

    /// <summary>Fechar exige justificativa quando a gaveta não bateu.</summary>
    public static void ExigirJustificativa(decimal diferenca, string? justificativa)
    {
        if (diferenca != 0 && string.IsNullOrWhiteSpace(justificativa))
            throw new GestaoException("CAMPO_INVALIDO", "A gaveta não bateu: diga o que aconteceu antes de fechar.", 422);
    }

    /// <summary>Quanto fica de troco (nunca mais do que há na gaveta) e quanto vai para o cofre.</summary>
    public static (decimal Fica, decimal Cofre) Repartir(decimal contado, decimal fica)
    {
        var trocoFica = R2(Math.Min(Math.Max(fica, 0), contado));
        return (trocoFica, R2(Math.Max(contado - trocoFica, 0)));
    }

    /// <summary>Sinal na gaveta: sangria, pagamento e devolução tiram; suprimento põe. Saída maior que a gaveta é recusada.</summary>
    public static int Sinal(string tipo) => tipo switch
    {
        MovimentoCaixa.Suprimento => 1,
        MovimentoCaixa.Sangria or MovimentoCaixa.Pagamento or MovimentoCaixa.Devolucao => -1,
        _ => throw Invalido("Movimento deve ser sang, sup, pag ou ret."),
    };

    public static void ValidarMovimento(string tipo, decimal valor, decimal gaveta)
    {
        if (valor <= 0 || decimal.Round(valor, 2) != valor) throw Invalido("Informe o valor (maior que zero, até 2 casas).");
        if (gaveta + Sinal(tipo) * valor < 0)
            throw new GestaoException("SEM_SALDO", $"Não há esse valor na gaveta. Hoje ela tem R$ {gaveta:N2}.", 422);
    }

    /// <summary>Maquininha "bate" quando o digitado é igual ao que as vendas somam (tolerância de 1 centavo).</summary>
    public static bool MaquininhaConfere(decimal digitado, decimal esperado) => Math.Abs(digitado - esperado) < 0.01m;

    public static decimal R2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    private static GestaoException Invalido(string m) => new("CAMPO_INVALIDO", m, 422);
}
