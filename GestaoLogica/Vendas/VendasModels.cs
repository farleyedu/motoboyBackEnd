namespace APIBack.GestaoLogica.Vendas;

// Produtos vendidos, de-para de vendas e ficha técnica reais (6C3-A). Produto vendido = cardapio_produto: não existe ID fictício.

public sealed record ProdutoVendidoDto(Guid Id, string Nome, string? FotoUrl, Guid? FichaId, decimal Preco);

public sealed record EventoVendaDto(string Id, DateTimeOffset Quando, string Titulo, string? Detalhe, string? Responsavel);

/// <summary>Item de um canal. <c>Id</c> é "ifood:{guid}" (ifood_produto_vinculo) ou "cardapio:{nome normalizado}" (linha avulsa de pedido).</summary>
public sealed record VinculoVendaDto(string Id, string Canal, string NomeNoCanal, Guid? ProdutoVendidoId, decimal PrecoNoCanal,
    IReadOnlyDictionary<string, int> VendidosPorMes, bool PrecoDiverge, IReadOnlyList<EventoVendaDto> Historico);

public sealed record VincularVendaRequest(string? VinculoId, Guid? ProdutoVendidoId);

public sealed record ComponenteRequest(Guid InsumoId, decimal Quantidade, string? Unidade, IReadOnlyList<string>? Modalidades);

public sealed record SalvarFichaRequest(IReadOnlyList<ComponenteRequest>? Componentes);

/// <summary><c>Custo</c> nulo = o insumo ainda não tem preço (nunca vira zero).</summary>
public sealed record LinhaFichaDto(Guid InsumoId, string Nome, decimal Quantidade, string Unidade, decimal? Custo);

public sealed record ComponenteFichaDto(Guid InsumoId, string Nome, decimal Quantidade, string Unidade, IReadOnlyList<string>? Modalidades,
    bool Embalagem, decimal? Custo);

public sealed record ModalidadeFichaDto(IReadOnlyList<LinhaFichaDto> Ingredientes, IReadOnlyList<LinhaFichaDto> Embalagens);

public sealed record FichaDto(Guid Id, Guid ProdutoVendidoId, DateTimeOffset? AtualizadaEm, IReadOnlyList<ComponenteFichaDto> Componentes,
    IReadOnlyDictionary<string, ModalidadeFichaDto> PorModalidade);

public sealed record InsumoFicha(string Nome, string Unidade, string? Categoria, bool Ativo);
