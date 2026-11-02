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

/// <summary><c>Situacao</c> = chave oficial do status (pendente, atribuido, em_rota, aguardando_motoboy). <c>Dia</c> = dia (AAAA-MM-DD) em que o pedido foi feito, quando legível.</summary>
public sealed record PedidoEmAberto(string Numero, string Onde, decimal Valor, string Situacao, string? Dia = null);

public sealed record AjusteFechamento(Guid Id, DateTimeOffset Em, int Por, decimal ContadoAnterior, decimal ContadoNovo, string Motivo,
    string? PorNome = null);

public sealed record FechamentoDto(string Id, DateOnly Dia, int Operador, DateTimeOffset Abertura, DateTimeOffset Fechamento,
    decimal Esperado, decimal Contado, IReadOnlyDictionary<string, decimal> Maquininha, IReadOnlyDictionary<string, decimal> MaquininhaEsperado,
    string? Justificativa, decimal Fica, decimal Cofre, IReadOnlyList<AjusteFechamento> Ajustes, string? OperadorNome = null, int Turno = 1,
    bool Ajustavel = true);

/// <param name="Aberto">Aberto de verdade: nem fechado nem com fechamento esperando o gerente (aí o caixa fica travado).</param>
/// <param name="Aguardando">Fechamento enviado pelo operador esperando a aprovação do gerente.</param>
/// <param name="Devolvido">O gerente devolveu o último envio para recontar (o caixa voltou a ficar aberto).</param>
/// <param name="AprovadoPorNome">Fechado por aprovação do gerente: quem aprovou.</param>
/// <param name="Turno">1 no primeiro caixa do dia; 2, 3… quando o dia abriu de novo depois de fechar (novo turno).</param>
/// <param name="Anteriores">Fechamentos que saíram do dia: turnos encerrados e fechamentos desfeitos (reabertos), do mais antigo ao mais novo.</param>
public sealed record CaixaDoDia(DateOnly Dia, bool Aberto, DateTimeOffset? AbertoEm, int? Por, decimal Fundo, IReadOnlyList<LinhaFita> Fita,
    IReadOnlyList<PedidoEmAberto> PedidosEmAberto, FechamentoDto? Fechamento, string? PorNome = null, decimal? FundoOriginal = null,
    IReadOnlyList<CorrecaoFundoDto>? CorrecoesFundo = null, int SolicitacoesPendentes = 0, EnvioDoFechamento? Aguardando = null,
    DevolucaoDoFechamento? Devolvido = null, string? AprovadoPorNome = null, int Turno = 1, IReadOnlyList<FechamentoAnterior>? Anteriores = null,
    bool AprovacaoPendente = false);

/// <summary>
/// Que dia o caixa está rodando. <c>Dia</c> = dia em operação (o do caixa aberto; sem caixa aberto, o da virada); <c>DiaDoCalendario</c>
/// = a data de agora na loja; <c>ViradaDoDia</c> = "HH:mm" em que o dia de operação vira. <c>Situacao</c>: <c>sem_caixa</c> (o dia
/// ainda não abriu), <c>aberto</c>, <c>aguardando</c> (fechamento enviado ao gerente) ou <c>fechado</c>. <c>Pendentes</c>: fechamentos
/// esperando o gerente (nenhum caixa novo abre enquanto houver).
/// </summary>
public sealed record OperacaoDoCaixa(DateOnly Dia, DateOnly DiaDoCalendario, string ViradaDoDia, string Situacao, int Turno,
    IReadOnlyList<FechamentoAguardando> Pendentes);

/// <summary>
/// Um fechamento que saiu do dia. <c>Destino</c> = <c>novo_turno</c> (o fechamento vale; o dia abriu outro turno) ou <c>reaberto</c>
/// (o fechamento foi desfeito: quebra/sobra, ajustes e recolhimento estornados no livro).
/// </summary>
public sealed record FechamentoAnterior(string Destino, int Turno, DateTimeOffset Em, string? PorNome, string? Motivo, DateTimeOffset AbertoEm,
    DateTimeOffset FechadoEm, string? FechadoPorNome, decimal Esperado, decimal Contado, decimal Cofre, decimal Fica);

/// <summary>Abrir outro turno no mesmo dia, com o fundo de troco (o padrão da tela é o que ficou na gaveta).</summary>
public sealed record NovoTurnoRequest(DateOnly Dia, decimal Fundo);

/// <summary>Desfazer o fechamento de hoje e continuar o caixa aberto. <c>Motivo</c> obrigatório (mínimo de 3 letras).</summary>
public sealed record ReabrirCaixaRequest(DateOnly Dia, string? Motivo);

/// <summary>Configuração do caixa da loja, decidida pelo gerente.</summary>
/// <param name="ToleranciaFechamento">Diferença (para mais ou para menos) que o operador fecha sozinho; acima disso vai para o gerente.</param>
/// <param name="ViradaDoDia">"HH:mm" em que o dia de operação vira (padrão 05:00, de 00:00 a 08:00).</param>
public sealed record ConfigDoCaixa(decimal ToleranciaFechamento, bool OperadorVeSaldo, string ViradaDoDia = "05:00")
{
    [System.Text.Json.Serialization.JsonIgnore]
    public TimeSpan Virada => TimeSpan.TryParse(ViradaDoDia, out var v) ? v : TimeSpan.FromHours(5);
}

public sealed record SalvarConfigDoCaixaRequest(decimal? ToleranciaFechamento, bool? OperadorVeSaldo, string? ViradaDoDia = null);

/// <summary>O que o operador contou e enviou ao gerente (nada disso está no livro até a aprovação).</summary>
public sealed record EnvioDoFechamento(DateTimeOffset Em, int Por, string? PorNome, ContagemCaixa Contagem, decimal Esperado, decimal Contado,
    decimal Diferenca, IReadOnlyDictionary<string, decimal> Maquininha, IReadOnlyDictionary<string, decimal> MaquininhaEsperado, string? Justificativa,
    decimal Fica, decimal Tolerancia);

public sealed record DevolucaoDoFechamento(DateTimeOffset Em, int Por, string? PorNome, string Motivo);

/// <summary>
/// Decisão do gerente sobre um fechamento enviado: <c>aprovar</c> (com <c>Recontagem</c> opcional, que substitui a contagem do operador,
/// guardada) ou <c>devolver</c> (com <c>Motivo</c>; só no próprio dia).
/// </summary>
public sealed record DecisaoDoFechamentoRequest(string? Decisao, ContagemCaixa? Recontagem, string? Justificativa, decimal? Fica, string? Motivo);

/// <summary>Fechamento esperando o gerente (aviso no painel).</summary>
/// <param name="Fechado">true: o operador fechou (já está no livro) e só falta o gerente aprovar; false: enviado, esperando a decisão.</param>
public sealed record FechamentoAguardando(DateOnly Dia, DateTimeOffset Em, string? PorNome, decimal Diferenca, decimal Contado, decimal Esperado,
    bool Fechado = false);

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

// ---- 6C5: aba Motoboys do Caixa (acerto em lote) e recebimento de venda que ficou a receber

/// <summary>
/// Como cada parte do pagamento da entrega entra no acerto: <c>dinheiro</c> (contar e devolver), <c>integracao</c> (Pix já confirmado
/// pela integração), <c>cartao_manual</c> (cartão na maquininha: o comprovante físico não confirma sozinho, o caixa confere),
/// <c>ifood</c> (recebível do iFood, não passa pela gaveta), <c>outro</c> (já está na gaveta ou sem conta).
/// </summary>
public static class ParteAcerto
{
    public const string Dinheiro = "dinheiro", Integracao = "integracao", CartaoManual = "cartao_manual", Ifood = "ifood", Outro = "outro";
}

/// <param name="Situacao">a_receber ou recebido (status do livro).</param>
public sealed record ParteDaEntrega(string Tipo, decimal Valor, string Situacao);

/// <param name="Situacao">entregue (dá para acertar) ou em_rota (ainda não).</param>
/// <param name="Lote">Número do lote (L-0006) quando já foi acertada.</param>
public sealed record EntregaDoMotoboy(int PedidoId, string Numero, string? Cliente, DateTimeOffset? Em, string Situacao, decimal Valor,
    IReadOnlyList<ParteDaEntrega> Partes, string? Lote = null);

/// <summary>
/// Um motoboy na aba Motoboys. Os totais somam só o que está pendente. A presença vem da 6C6 (<see cref="ChegadaRegras"/>) e é só destaque:
/// nunca confirma acerto.
/// </summary>
/// <param name="Chegada">rota, chegando, provavel, loja (retorno confirmado) ou sem_sinal; nulo quando não há o que dizer.</param>
/// <param name="ChegadaEm">Desde quando está nesse estado (nulo para sem_sinal).</param>
/// <param name="Sinal">ok, antigo ou ausente: idade da última posição desta loja. Conferência manual vale em qualquer caso.</param>
/// <param name="PosicaoEm">Captura da última posição recebida.</param>
/// <param name="ChegadaOrigem">gps ou manual (o motoboy tocou "cheguei").</param>
public sealed record MotoboyDoDia(int MotoboyId, string Nome, string? Chegada, DateTimeOffset? ChegadaEm, decimal DinheiroCom,
    decimal CartaoAConferir, decimal Integracao, decimal Ifood, IReadOnlyList<EntregaDoMotoboy> Pendentes, IReadOnlyList<EntregaDoMotoboy> EmRota,
    IReadOnlyList<AcertoDto> Lotes, string? Sinal = null, DateTimeOffset? PosicaoEm = null, string? ChegadaOrigem = null);

/// <param name="ConferidoManual">Obrigatório para pedido com cartão: o caixa conferiu na maquininha.</param>
public sealed record ItemAcertoRequest(int PedidoId, bool ConferidoManual = false);

/// <summary>Pedidos que não forem enviados continuam pendentes com o motoboy (conferência parcial). Diferença exige motivo.</summary>
public sealed record AcertoRequest(string? ChaveIdempotencia, DateOnly Dia, int MotoboyId, IReadOnlyList<ItemAcertoRequest>? Itens,
    decimal ValorEntregue, string? Motivo);

public sealed record ItemAcertoDto(int PedidoId, decimal Dinheiro, decimal DigitalIntegracao, decimal CartaoManual, decimal Ifood,
    bool ConferidoManual);

/// <param name="Numero">L-0006.</param>
/// <param name="Tipo">acerto ou correcao (correção aponta o original em <c>CorrigeId</c>; o original não muda).</param>
/// <param name="Situacao">conferido (não sobrou nada com o motoboy) ou parcial.</param>
/// <param name="CorrigidoPor">Id da correção, quando este lote foi corrigido.</param>
public sealed record AcertoDto(Guid Id, string Numero, DateOnly Dia, int MotoboyId, string? MotoboyNome, string Tipo, Guid? CorrigeId,
    decimal Esperado, decimal Entregue, decimal Diferenca, string? Motivo, string Situacao, int PendentesDepois, int Operador,
    string? OperadorNome, DateTimeOffset Em, IReadOnlyList<ItemAcertoDto> Itens, Guid? CorrigidoPor = null);

/// <summary>Correção do valor entregue de um lote (gerente). Vira um lote novo; a diferença nova vai para o livro.</summary>
public sealed record CorrigirAcertoRequest(string? ChaveIdempotencia, decimal EntregueNovo, string? Motivo);

/// <summary>O que a 6C6 lê para o app do motoboy: se ainda há algo a acertar e como ficou o último lote.</summary>
public sealed record SituacaoAcertoMotoboy(int MotoboyId, int Pendentes, decimal DinheiroPendente, bool TudoAcertado, AcertoDto? UltimoLote);

/// <summary>
/// 6C6: como a loja conferiu um pedido do motoboy, para o app dele. Lido dos lotes da 6C5 (única fonte); nada aqui é presumido.
/// </summary>
/// <param name="Situacao">pendente (entregue, fora de lote), conferido (no lote) ou corrigido (lote corrigido depois pelo gerente).</param>
/// <param name="LoteSituacao">conferido ou parcial (o lote deixou outros pedidos pendentes com o motoboy).</param>
/// <param name="Diferenca">Diferença final do lote (com a correção, se houve): negativa = faltou. Zero quando bateu.</param>
public sealed record ConferenciaDoPedido(int PedidoId, string Situacao, decimal Valor, decimal Dinheiro, Guid? LoteId, string? Lote,
    string? LoteSituacao, DateTimeOffset? ConferidoEm, string? ConferidoPor, decimal Diferenca, DateTimeOffset? CorrigidoEm, string? Motivo);

public sealed record ConferenciasDoMotoboy(Guid EstabelecimentoId, int MotoboyId, DateTimeOffset LidoEm, IReadOnlyList<ConferenciaDoPedido> Pedidos);

/// <summary>Pedido concluído cuja venda ficou a receber (forma desconhecida, caixa fechado na hora). Motoboy e iFood não entram.</summary>
public sealed record VendaAReceber(int PedidoId, string Numero, string Descricao, DateTimeOffset Em, decimal AReceber, string? FormaPrevista);

/// <summary>Recebe no caixa o que ficou a receber da venda do pedido: dinheiro vai para a gaveta; Pix e cartão, para a maquininha.
/// <c>ValorRecebido</c> em dinheiro pode ser maior (troco não entra no livro).</summary>
/// <param name="Pagamentos">Pedido pago em mais de uma forma: cada parte com a forma e o valor (as partes somam o que falta). Em dinheiro,
/// <c>ValorRecebido</c> da parte pode ser maior (o troco só vale nela). Sem a lista, vale <c>Forma</c> e <c>ValorRecebido</c> como sempre.</param>
public sealed record RecebimentoRequest(string? ChaveIdempotencia, DateOnly Dia, int PedidoId, string? Forma, decimal ValorRecebido,
    IReadOnlyList<ParteDoRecebimento>? Pagamentos = null);

public sealed record ParteDoRecebimento(string? Forma, decimal Valor, decimal? ValorRecebido = null);

/// <summary>
/// Venda de balcão pelo Caixa (6C5): vira pedido sem endereço. Dinheiro exige <c>ValorRecebido</c>. <c>Concluir</c> (padrão true): entregue
/// e pago na hora; false: segue o fluxo da loja e, sem <c>Forma</c>, o cliente paga na retirada (a venda fica a receber).
/// </summary>
public sealed record VendaBalcaoRequest(string? ChaveIdempotencia, DateOnly Dia, List<APIBack.DTOs.Delivery.PedidoItemRequest>? Itens, string? Forma,
    decimal? ValorRecebido, string? Cliente = null, string? Telefone = null, string? Observacao = null, bool? Concluir = null);

/// <param name="JaExistia">Reenvio com a mesma chave: o pedido e a venda já existiam (nada foi duplicado).</param>
/// <param name="Status">Situação do pedido criado: "concluido" (entregue na hora) ou "pendente" (segue o fluxo).</param>
public sealed record VendaBalcaoResposta(int PedidoId, decimal Total, decimal Troco, bool JaExistia, CaixaDoDia Dia, string? Status = null);
