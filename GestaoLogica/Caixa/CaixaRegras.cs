using APIBack.Acontecimentos;

namespace APIBack.GestaoLogica.Caixa;

/// <summary>
/// Eixo financeiro do pedido ainda em operação (independente de estar na cozinha, em rota ou na retirada). <c>pago</c>: o pedido diz que
/// foi pago (online, balcão pago na hora…); <c>a_receber</c>: falta cobrar; <c>recebivel_externo</c>: o iFood cobrou e repassa à loja.
/// </summary>
public static class PagamentoDoPedido
{
    public const string Pago = "pago", AReceber = "a_receber", RecebivelExterno = "recebivel_externo";
    public const string ResponsavelCaixa = "caixa", ResponsavelMotoboy = "motoboy", ResponsavelIfood = "ifood";
}

/// <summary>Regras puras do caixa (as mesmas do caixaModelo.ts do Painel): contagem, diferença, troco que fica e movimentos.</summary>
public static class CaixaRegras
{
    /// <summary>
    /// Situação financeira de um pedido que ainda não foi concluído (não existe venda no livro): só o que o pedido afirma. A forma
    /// combinada nunca prova recebimento; ausência de informação vira "a receber", nunca "pago". iFood: o que o app cobrou é recebível
    /// externo; sobra a cobrar na entrega (<paramref name="ifoodPendente"/> &gt; 0) quando o cliente paga parte à loja.
    /// </summary>
    public static (string Pagamento, string? FormaPrevista, string? Responsavel) SituacaoDoPagamento(string? statusPagamento,
        string? tipoPagamento, bool ifood, decimal? ifoodPendente, bool comMotoboy)
    {
        var forma = APIBack.GestaoLogica.Vendas.VendaRegras.Forma(tipoPagamento).Forma;
        if (ifood)
            return ifoodPendente is > 0
                ? (PagamentoDoPedido.AReceber, forma, comMotoboy ? PagamentoDoPedido.ResponsavelMotoboy : PagamentoDoPedido.ResponsavelCaixa)
                : (PagamentoDoPedido.RecebivelExterno, null, PagamentoDoPedido.ResponsavelIfood);
        if (APIBack.Service.DeliveryCompletionRules.IsPaid(statusPagamento)) return (PagamentoDoPedido.Pago, forma, null);
        return (PagamentoDoPedido.AReceber, forma, comMotoboy ? PagamentoDoPedido.ResponsavelMotoboy : PagamentoDoPedido.ResponsavelCaixa);
    }

    public static readonly int[] Notas = { 200, 100, 50, 20, 10, 5, 2 };

    public static decimal TotalContado(ContagemCaixa c)
    {
        if (c.Moedas < 0 || (c.Notas ?? new Dictionary<int, int>()).Any(n => n.Value < 0)) throw Invalido("Contagem não pode ser negativa.");
        if ((c.Notas ?? new Dictionary<int, int>()).Keys.Any(k => !Notas.Contains(k))) throw Invalido("Cédula desconhecida.");
        return R2(Notas.Sum(v => v * (c.Notas?.GetValueOrDefault(v) ?? 0)) + c.Moedas);
    }

    /// <summary>
    /// Por que o caixa não abre com a loja fechada e o que fazer (o estado oficial da loja vem de <c>HorarioOperacaoRepository</c>:
    /// <c>SEM_HORARIO</c>, <c>FORA_HORARIO</c> ou <c>PAUSADA</c>). O texto orienta a abrir a loja primeiro.
    /// </summary>
    public static string MensagemLojaFechada(string? motivo, string? proximaAbertura) => motivo switch
    {
        "PAUSADA" => "A loja está pausada (não aceita pedidos). Reative a loja em Configurações antes de abrir o caixa.",
        "SEM_HORARIO" => "A loja ainda não tem horário de funcionamento. Cadastre o horário ou abra a loja em Configurações antes de abrir o caixa.",
        _ => string.IsNullOrWhiteSpace(proximaAbertura)
            ? "A loja está fechada. Abra a loja antes de abrir o caixa."
            : $"A loja está fechada (abre {proximaAbertura}). Abra a loja agora, em Configurações, para abrir o caixa antes disso.",
    };

    /// <summary>
    /// A forma de UMA linha da fita vem da conta onde o dinheiro entrou, não da forma principal da venda: venda dividida (R$ 66 em
    /// dinheiro e R$ 20 em Pix) tem uma linha na gaveta e outra na maquininha, e só a primeira é dinheiro físico.
    /// </summary>
    public static string FormaDaLinha(string destino, string? principal, IReadOnlyList<(string? Forma, decimal Valor)> partes, decimal valor)
    {
        if (destino == FormaDestino.Gaveta) return FormaCaixa.Dinheiro;
        if (destino == FormaDestino.Ifood) return FormaCaixa.Ifood;
        var daParte = partes.FirstOrDefault(x => x.Forma is not null && FormaCaixa.Maquininha.Contains(x.Forma) && x.Valor == valor).Forma;
        if (daParte is not null) return daParte;
        return principal is not null && FormaCaixa.Maquininha.Contains(principal) ? principal : FormaCaixa.Pix;
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
