namespace APIBack.GestaoLogica.Caixa;

// Caixa do dia. Espelha a interface CaixaFonte do Painel (zippy-admin/src/features/zippy-gestao/caixa/caixaAdaptador.ts) para a
// Integração só trocar o adaptador simulado. docs/zippy-gestao/CONTRATOS.md, seção 9.7.

/// <summary>Formas da fita: din, pix, deb, cre, ifood.</summary>
public static class FormaCaixa
{
    public const string Dinheiro = "din", Pix = "pix", Debito = "deb", Credito = "cre", Ifood = "ifood";
    public static readonly string[] Todas = { Dinheiro, Pix, Debito, Credito, Ifood };
    public static readonly string[] Maquininha = { Debito, Credito, Pix };
}

/// <summary>Movimentos de gaveta: sang (sangria), sup (suprimento), pag (pagamento em dinheiro), ret (devolução).</summary>
public static class MovimentoCaixa
{
    public const string Sangria = "sang", Suprimento = "sup", Pagamento = "pag", Devolucao = "ret";
    public static readonly string[] Todos = { Sangria, Suprimento, Pagamento, Devolucao };
}

/// <summary>Linha da fita. <c>Tipo</c>: abre, venda, sup, sang, pag, canc. <c>Valor</c> com sinal (saída da gaveta negativa).</summary>
public sealed record LinhaFita(string Id, string Hora, string Tipo, string Descricao, string Forma, decimal Valor);

public sealed record PedidoEmAberto(string Numero, string Onde, decimal Valor, string Situacao);

public sealed record AjusteFechamento(Guid Id, DateTimeOffset Em, int Por, decimal ContadoAnterior, decimal ContadoNovo, string Motivo);

public sealed record FechamentoDto(string Id, DateOnly Dia, int Operador, DateTimeOffset Abertura, DateTimeOffset Fechamento,
    decimal Esperado, decimal Contado, IReadOnlyDictionary<string, decimal> Maquininha, IReadOnlyDictionary<string, decimal> MaquininhaEsperado,
    string? Justificativa, decimal Fica, decimal Cofre, IReadOnlyList<AjusteFechamento> Ajustes);

public sealed record CaixaDoDia(DateOnly Dia, bool Aberto, DateTimeOffset? AbertoEm, int? Por, decimal Fundo, IReadOnlyList<LinhaFita> Fita,
    IReadOnlyList<PedidoEmAberto> PedidosEmAberto, FechamentoDto? Fechamento);

/// <summary>Contagem por cédula (200, 100, 50, 20, 10, 5, 2) mais o total em moedas.</summary>
public sealed record ContagemCaixa(IReadOnlyDictionary<int, int>? Notas, decimal Moedas);

public sealed record ConferenciaDaContagem(decimal Esperado, decimal Contado, decimal Diferenca);

public sealed record AbrirCaixaRequest(DateOnly Dia, decimal Fundo);

/// <summary><c>Referencia</c> = id do pedido na devolução (estorna a venda inteira quando o valor bate).</summary>
public sealed record MovimentoCaixaRequest(string? ChaveIdempotencia, DateOnly Dia, string Tipo, decimal Valor, string? Motivo, string? Referencia,
    Guid? ContaId = null);

public sealed record FecharCaixaRequest(DateOnly Dia, ContagemCaixa Contagem, IReadOnlyDictionary<string, decimal>? Maquininha, string? Justificativa,
    decimal Fica);

public sealed record AjustarFechamentoRequest(decimal ContadoNovo, string? Motivo);
