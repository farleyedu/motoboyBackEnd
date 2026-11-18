namespace APIBack.Mesas.Pedidos;

// Pedidos (CONTRATO-API §0.1 e §3.5). Todo comando leva a "chave". "Motivo" onde o comando tem * ou a autonomia pede.

/// <summary>Um item do lançamento. <c>Buscar</c>: o cliente escolheu buscar no balcão (vale se a loja deixa escolher).</summary>
public sealed record ItemNovo(Guid? ProdutoId, decimal? Qtd, string? Obs, string? Alergia, Guid? ComandaId, IReadOnlyList<Guid>? ComandaIds,
    bool? Buscar, bool? Extra);

/// <summary><c>Origem</c>: só dono/gerente escolhem (<c>cliente</c> simula o pedido do celular; <c>balcao</c>); o resto vem do papel.</summary>
public sealed record LancarPedidoRequest(Guid? Chave, Guid? MesaId, IReadOnlyList<ItemNovo>? Itens, bool? Rascunho, bool? SegurarPrincipal,
    string? Origem, Guid? PessoaId);
public sealed record RepetirRodadaRequest(Guid? Chave, Guid? MesaId, Guid? PessoaId, IReadOnlyList<ItemNovo>? Itens);

/// <summary>Escolha de itens: <c>ItemIds</c> | <c>ItemId</c> | <c>PedidoId</c> | <c>MesaId</c> (sessão aberta da mesa).</summary>
public sealed record ItensRequest(Guid? Chave, Guid? ItemId, IReadOnlyList<Guid>? ItemIds, Guid? PedidoId, Guid? MesaId, string? Motivo);
public sealed record ItemRequest(Guid? Chave, Guid? ItemId, string? Motivo);
public sealed record EditarItemRequest(Guid? Chave, Guid? ItemId, decimal? Qtd, string? Obs, IReadOnlyList<Guid>? ComandaIds, string? Motivo);
public sealed record MoverItemRequest(Guid? Chave, Guid? ItemId, IReadOnlyList<Guid>? ComandaIds, string? Motivo);
public sealed record MoverItemMesaRequest(Guid? Chave, Guid? ItemId, Guid? ParaMesaId, Guid? ComandaId, int? VersaoMesa, int? VersaoParaMesa,
    string? Motivo);
public sealed record AtribuirPreparoRequest(Guid? Chave, Guid? ItemId, Guid? PessoaId);
public sealed record TrocarEstacaoRequest(Guid? Chave, Guid? ItemId, string? EstacaoId);
public sealed record TrocarQuemLevaRequest(Guid? Chave, Guid? ItemId, string? QuemLeva);
/// <summary>Comandos do pedido: soltar o principal, cobrar, atraso (<c>Para</c>: garcom|cliente), recado (<c>Texto</c>), escalar (<c>ParaId</c>).</summary>
public sealed record PedidoRequest(Guid? Chave, Guid? PedidoId, string? Para, string? Texto, Guid? ParaId);
public sealed record ReconhecerRequest(Guid? Chave, Guid? PedidoId, IReadOnlyList<Guid>? PedidoIds);
public sealed record PassarEntregaRequest(Guid? Chave, IReadOnlyList<Guid>? ItemIds, Guid? ItemId, Guid? GarcomId);
public sealed record CortesiaRequest(Guid? Chave, Guid? ItemId, int? VersaoMesa, string? Motivo);
public sealed record PerdaRequest(Guid? Chave, Guid? ProdutoId, Guid? ItemId, decimal? Qtd, Guid? MesaId, string? Motivo);
public sealed record AvisoRequest(Guid? Chave, Guid? AvisoId);

// Respostas: sempre eventoId + versoes (mesaId → versão nova).

public sealed record PedidoLancadoDto(Guid EventoId, IReadOnlyDictionary<Guid, int> Versoes, Guid PedidoId, int AguardandoAprovacao,
    IReadOnlyList<Guid> ItemIds);
public sealed record RodadaDto(Guid EventoId, IReadOnlyDictionary<Guid, int> Versoes, Guid PedidoId, int Repetidos, IReadOnlyList<Guid> ItemIds);
/// <summary><c>Quantos</c> = enviados | aprovados | recusados | entregues | aCaminho | avisados (conforme o comando).</summary>
public sealed record ItensDto(Guid EventoId, IReadOnlyDictionary<Guid, int> Versoes, int Quantos, IReadOnlyList<Guid> ItemIds);
public sealed record ItemEstadoDto(Guid EventoId, IReadOnlyDictionary<Guid, int> Versoes, Guid ItemId, string Estado, bool EntregaDireta = false);
public sealed record ItemMovidoDto(Guid EventoId, IReadOnlyDictionary<Guid, int> Versoes, Guid ItemId, Guid MesaId, Guid PedidoId, Guid? GarcomAvisado);
public sealed record AvisoCriadoDto(Guid EventoId, IReadOnlyDictionary<Guid, int> Versoes, Guid AvisoId);
public sealed record PerdaDto(Guid EventoId, IReadOnlyDictionary<Guid, int> Versoes, Guid PerdaId, Guid AvisoId);
public sealed record PedidoComandoDto(Guid EventoId, IReadOnlyDictionary<Guid, int> Versoes, Guid PedidoId);

// Leituras (§4.1)

public sealed record ItemCozinhaDto(Guid Id, Guid PedidoId, Guid ProdutoId, string Nome, string? Foto, decimal Qtd, string Estado, string EstacaoId,
    short EtapaAtual, IReadOnlyList<Etapa> Etapas, string? Obs, string? Alergia, string? QuemLeva, string TipoPrato, int Refazer,
    string? Cliente, Guid? PreparoPor, string? PreparoNome, DateTime? TFila, DateTime? TPronto, DateTime? AvisadoEm, DateTime? PegouEm,
    string? PegouNome, bool EsperandoEntrada, bool EsperandoGerente, bool EntregaDireta, bool EntregaPresumida);
public sealed record OutroSetorDto(string EstacaoId, decimal Qtd);
public sealed record RecadoDto(string Tipo, string? Texto, string? Para, string? Nome, DateTime Quando);
/// <summary>Um cartão = um pedido numa coluna (novos | preparando | pronto); "pronto" tem faixa garcom|cliente.</summary>
public sealed record CartaoCozinhaDto(string Id, Guid PedidoId, Guid MesaId, string MesaNumero, int Pessoas, string Origem, DateTime CriadoEm,
    string Coluna, string? Faixa, IReadOnlyList<ItemCozinhaDto> Itens, IReadOnlyList<OutroSetorDto> Outros, int Minutos, int EsperaMin,
    int Esperado, string Cor, bool EsperandoEntrada, bool SegurarPrincipal, DateTime? SoltoEm, Guid? GarcomId, string? GarcomNome,
    int? AvisadoMin, IReadOnlyList<RecadoDto> Recados, bool SemPainel);
public sealed record EstacaoResumoDto(string EstacaoId, int ParaFazer, int Atrasados, bool Painel, string? Via, int Esperado);
public sealed record CozinhaDto(IReadOnlyList<CartaoCozinhaDto> Cartoes, IReadOnlyList<EstacaoResumoDto> Estacoes, DateTime Agora);
public sealed record TotalItemDto(Guid ProdutoId, string Nome, decimal Qtd, IReadOnlyList<Guid> PedidoIds);
public sealed record ParaLevarDto(Guid ItemId, Guid PedidoId, Guid MesaId, string MesaNumero, string Nome, string? Foto, decimal Qtd,
    Guid? GarcomId, DateTime? Desde, DateTime? PegouEm, string? PegouNome, string Estado);
public sealed record AprovarDto(Guid PedidoId, Guid MesaId, string MesaNumero, string Origem, DateTime CriadoEm, Guid? EscaladoPara,
    IReadOnlyList<ItemCozinhaDto> Itens);
/// <summary>O que é do garçom agora: levar, a caminho (Peguei), aprovar e o que está na cozinha das mesas dele.</summary>
public sealed record GarcomPedidosDto(Guid? GarcomId, IReadOnlyList<ParaLevarDto> ParaLevar, IReadOnlyList<ParaLevarDto> ACaminho,
    IReadOnlyList<AprovarDto> Aprovar, int NaCozinha, IReadOnlyList<ParaLevarDto> Retirada);
public sealed record PedidoResumoDto(Guid Id, Guid SessaoId, Guid MesaId, string MesaNumero, string Origem, string Estado, string? AtorNome,
    DateTime CriadoEm, bool SegurarPrincipal, DateTime? VistoEm, Guid? EscaladoPara, IReadOnlyList<ItemCozinhaDto> Itens,
    IReadOnlyList<RecadoDto> Recados);
public sealed record AvisoGestorDto(Guid Id, string Tipo, Guid? MesaId, Guid? ItemId, decimal Valor, string? Motivo, string? PorNome, string? Texto,
    bool Lido, DateTime CriadoEm);
/// <summary>Posição do item para o cliente (P18 expõe): faixa, nunca minutos exatos.</summary>
public sealed record FilaClienteDto(Guid ItemId, string Estado, int AFrente, string Faixa);
