namespace APIBack.GestaoLogica.Estoque;

// Estoque da gestão: perda, inventário e baixa por venda. docs/zippy-gestao/CONTRATOS.md, seção 9.5.

public static class Modalidade
{
    public const string Salao = "salao", Retirada = "retirada", Delivery = "delivery";
    public static readonly string[] Todas = { Salao, Retirada, Delivery };
}

/// <summary>Componente da ficha técnica. <c>Modalidades</c> nulo = vale para todas (embalagem de delivery lista só "delivery").</summary>
public sealed record ComponenteFicha(Guid InsumoId, string Nome, decimal Quantidade, string Unidade,
    IReadOnlyList<string>? Modalidades = null);

/// <summary>Ficha técnica de um produto vendido, por unidade vendida. É montada só na Gestão e Inteligência (regra 12).</summary>
public sealed record FichaTecnica(Guid ProdutoId, IReadOnlyList<ComponenteFicha> Componentes);

/// <summary>
/// Quem lê a ficha técnica. A Gestão e Inteligência ainda não tem ficha no backend: a implementação padrão
/// (<see cref="FichaTecnicaPendente"/>) não devolve nenhuma, e todo pedido fica "sem ficha" até a ligação (ver PENDENCIAS).
/// </summary>
public interface IFichaTecnicaLeitor
{
    Task<IReadOnlyDictionary<Guid, FichaTecnica>> Ler(Guid loja, IReadOnlyCollection<Guid> produtoIds, CancellationToken ct);
}

public sealed class FichaTecnicaPendente : IFichaTecnicaLeitor
{
    public Task<IReadOnlyDictionary<Guid, FichaTecnica>> Ler(Guid loja, IReadOnlyCollection<Guid> produtoIds, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<Guid, FichaTecnica>>(new Dictionary<Guid, FichaTecnica>());
}

public sealed record ItemVendido(int PedidoId, Guid? ProdutoId, string Nome, int Quantidade);

public sealed record PedidoBaixa(int PedidoId, string Modalidade);

/// <summary>Quanto sai de um insumo. <c>Quantidade</c> positiva (o movimento de saída leva o sinal negativo).</summary>
public sealed record ConsumoInsumo(Guid InsumoId, string Nome, string Unidade, decimal Quantidade);

public sealed record PedidoSemFicha(int PedidoId, IReadOnlyList<string> Itens);

public sealed record BaixaVendasPrevia(IReadOnlyList<ConsumoInsumo> Consumo, IReadOnlyList<int> Pedidos,
    IReadOnlyList<PedidoSemFicha> SemFicha, IReadOnlyList<int> JaBaixados);

public sealed record Contagem(Guid InsumoId, decimal Contado);

public sealed record SaldoInsumo(Guid InsumoId, string Nome, string Unidade, decimal Saldo);

/// <summary>Contado − esperado. Só diferença diferente de zero vira movimento de divergência.</summary>
public sealed record DivergenciaInventario(Guid InsumoId, string Nome, string Unidade, decimal Esperado, decimal Contado,
    decimal Diferenca);
