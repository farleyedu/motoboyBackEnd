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
public sealed record LinhaFita(string Id, string Hora, string Tipo, string Descricao, string Forma, decimal Valor,
    bool Corrigivel = false, bool Estornada = false, Guid? SolicitacaoPendente = null);

public sealed record PedidoEmAberto(string Numero, string Onde, decimal Valor, string Situacao);

public sealed record AjusteFechamento(Guid Id, DateTimeOffset Em, int Por, decimal ContadoAnterior, decimal ContadoNovo, string Motivo,
    string? PorNome = null);

public sealed record FechamentoDto(string Id, DateOnly Dia, int Operador, DateTimeOffset Abertura, DateTimeOffset Fechamento,
    decimal Esperado, decimal Contado, IReadOnlyDictionary<string, decimal> Maquininha, IReadOnlyDictionary<string, decimal> MaquininhaEsperado,
    string? Justificativa, decimal Fica, decimal Cofre, IReadOnlyList<AjusteFechamento> Ajustes, string? OperadorNome = null);

public sealed record CaixaDoDia(DateOnly Dia, bool Aberto, DateTimeOffset? AbertoEm, int? Por, decimal Fundo, IReadOnlyList<LinhaFita> Fita,
    IReadOnlyList<PedidoEmAberto> PedidosEmAberto, FechamentoDto? Fechamento, string? PorNome = null, decimal? FundoOriginal = null,
    IReadOnlyList<CorrecaoFundoDto>? CorrecoesFundo = null, int SolicitacoesPendentes = 0);

/// <summary>Contagem por cédula (200, 100, 50, 20, 10, 5, 2) mais o total em moedas.</summary>
public sealed record ContagemCaixa(IReadOnlyDictionary<int, int>? Notas, decimal Moedas);

public sealed record ConferenciaDaContagem(decimal Esperado, decimal Contado, decimal Diferenca);

public sealed record AbrirCaixaRequest(DateOnly Dia, decimal Fundo);

/// <summary>
/// <c>Referencia</c> = id do pedido na devolução (estorna a venda inteira quando o valor bate). No pagamento (<c>pag</c>):
/// <c>PessoaId</c> paga a remuneração da pessoa pela gaveta (Equipe); <c>LancamentoId</c> quita uma compra a pagar (valor inteiro).
/// </summary>
public sealed record MovimentoCaixaRequest(string? ChaveIdempotencia, DateOnly Dia, string Tipo, decimal Valor, string? Motivo, string? Referencia,
    Guid? ContaId = null, Guid? PessoaId = null, Guid? LancamentoId = null);

public sealed record FecharCaixaRequest(DateOnly Dia, ContagemCaixa Contagem, IReadOnlyDictionary<string, decimal>? Maquininha, string? Justificativa,
    decimal Fica);

public sealed record AjustarFechamentoRequest(decimal ContadoNovo, string? Motivo);

/// <summary>Compra a pagar (fora do cartão) que pode ser paga em dinheiro pela gaveta. <c>Valor</c> = líquido, com correções.</summary>
public sealed record CompraAPagar(Guid LancamentoId, Guid AcontecimentoId, string Descricao, decimal Valor, DateOnly? Vencimento);

/// <summary>O que dá para pagar em dinheiro pela gaveta: remuneração da equipe e compras a pagar.</summary>
public sealed record APagarNaGaveta(IReadOnlyList<APIBack.GestaoLogica.Equipe.PessoaAPagar> Equipe, IReadOnlyList<CompraAPagar> Compras);

// ---- 6C2-B: correções do Caixa (docs/zippy-gestao/6c2b-contratos-correcoes-caixa.md no zippy-admin)

public sealed record SolicitarCorrecaoRequest(string? ChaveIdempotencia, string? Motivo);

public sealed record EstornarMovimentoRequest(string? ChaveIdempotencia, string? Motivo);

/// <summary><c>Decisao</c>: "aprovar" ou "rejeitar". O motivo é obrigatório ao rejeitar.</summary>
public sealed record DecisaoRequest(string? Decisao, string? Motivo);

/// <summary><c>FundoAtual</c> = o fundo efetivo que a pessoa viu; se mudou desde então, a correção é recusada (CONFLITO).</summary>
public sealed record CorrigirFundoRequest(string? ChaveIdempotencia, decimal FundoAtual, decimal FundoNovo, string? Motivo);

public sealed record MovimentoSolicitado(Guid Id, string Tipo, string Descricao, DateTimeOffset DataFato, decimal Valor);

public sealed record SolicitacaoDto(Guid Id, DateOnly Dia, string Tipo, string Status, MovimentoSolicitado? Movimento, decimal? FundoAnterior,
    decimal? FundoNovo, string Motivo, int SolicitadoPor, string? SolicitadoPorNome, DateTimeOffset SolicitadoEm, int? DecididoPor,
    string? DecididoPorNome, DateTimeOffset? DecididoEm, string? MotivoDecisao, Guid? CorrecaoId);

public sealed record CorrecaoFundoDto(Guid Id, DateOnly Dia, DateTimeOffset Em, int Por, string? PorNome, decimal FundoAnterior,
    decimal FundoNovo, string Motivo);

/// <summary>Resposta da correção de fundo: gerente efetiva (<c>Efetivada</c>); operador só cria o pedido (HTTP 202).</summary>
public sealed record CorrigirFundoResposta(bool Efetivada, CaixaDoDia? Dia, SolicitacaoDto? Solicitacao);
