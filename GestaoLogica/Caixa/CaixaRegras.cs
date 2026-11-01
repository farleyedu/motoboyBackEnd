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

/// <summary>Regras puras do acerto do motoboy (6C5): de onde vem cada parte, o que se espera em dinheiro e quando o lote fecha.</summary>
public static class AcertoRegras
{
    /// <summary>
    /// Parte da venda pela conta do livro: dinheiro a receber na conta "com motoboys" se conta e devolve; Pix recebido na maquininha veio
    /// da integração; cartão a receber na maquininha precisa da conferência do caixa; recebível do iFood não passa pela gaveta.
    /// </summary>
    public static string Classificar(string? contaTipo, string? contaSubtipo, string status) => (contaTipo, contaSubtipo, status) switch
    {
        (TipoConta.Recebivel, APIBack.GestaoLogica.Vendas.VendaDoPedido.SubtipoMotoboys, StatusLancamento.AReceber) => ParteAcerto.Dinheiro,
        (TipoConta.Recebivel, "ifood", _) => ParteAcerto.Ifood,
        (TipoConta.Recebivel, "maquininha", StatusLancamento.AReceber) => ParteAcerto.CartaoManual,
        (TipoConta.Recebivel, "maquininha", _) => ParteAcerto.Integracao,
        _ => ParteAcerto.Outro,
    };

    /// <summary>Entregue − esperado (negativo = faltou). Não bateu exige motivo de pelo menos 3 letras.</summary>
    public static decimal Diferenca(decimal esperado, decimal entregue, string? motivo)
    {
        if (entregue < 0 || decimal.Round(entregue, 2) != entregue)
            throw new GestaoException("CAMPO_INVALIDO", "Valor entregue: zero ou mais, até 2 casas.", 422);
        var dif = CaixaRegras.R2(entregue - esperado);
        if (dif != 0 && (motivo?.Trim().Length ?? 0) < 3)
            throw new GestaoException("CAMPO_INVALIDO", "O valor não bateu: diga o motivo antes de confirmar.", 422);
        return dif;
    }

    /// <summary>conferido quando não sobra nada com o motoboy; parcial quando algum pedido ficou para outro lote.</summary>
    public static string Situacao(int pendentesDepois) => pendentesDepois == 0 ? "conferido" : "parcial";

    public static string Numero(int n) => $"L-{n:0000}";
}

/// <summary>Regras puras da venda de balcão pelo Caixa (6C5).</summary>
public static class BalcaoRegras
{
    /// <summary>Texto da forma gravado no pedido: é o que a venda do livro lê (VendaRegras.Forma).</summary>
    public static string TipoPagamento(string? forma) => forma?.Trim() switch
    {
        FormaCaixa.Dinheiro => "Dinheiro",
        FormaCaixa.Pix => "Pix",
        FormaCaixa.Debito => "Débito",
        FormaCaixa.Credito => "Crédito",
        _ => throw new GestaoException("CAMPO_INVALIDO", "Forma: din, pix, deb ou cre.", 422),
    };

    public static string Chave(string? chave)
    {
        var limpa = chave?.Trim();
        if (string.IsNullOrEmpty(limpa) || limpa.Length > 100) throw new GestaoException("CAMPO_INVALIDO", "Envie a chave de idempotência.", 422);
        return limpa;
    }

    /// <summary>Dinheiro: informe quanto recebeu (o troco sai da diferença). Outras formas não levam valor recebido.</summary>
    public static decimal? ValorRecebido(string forma, decimal? valor)
    {
        if (forma.Trim() != FormaCaixa.Dinheiro) return null;
        if (valor is not { } v || v <= 0 || decimal.Round(v, 2) != v)
            throw new GestaoException("CAMPO_INVALIDO", "Informe quanto recebeu em dinheiro (até 2 casas).", 422);
        return v;
    }
}
