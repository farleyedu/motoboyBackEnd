namespace APIBack.DTOs.Gestao;

/// <summary>Visao geral da operacao de todas as lojas da empresa do usuario, numa chamada so.</summary>
public sealed class VisaoGeralOperacaoDto
{
    public Guid EmpresaId { get; set; }
    public DateTimeOffset GeradoEmUtc { get; set; }
    public List<LojaOperacaoDto> Lojas { get; set; } = new();
}

/// <summary>
/// Contadores de uma loja. Contador nulo = a loja nao tem o modulo (ou esta bloqueada) e o painel nao deve mostrar zero.
/// Pedidos pendentes e em rota existem em toda loja com Pedidos; o resto depende de Delivery e iFood.
/// </summary>
public sealed class LojaOperacaoDto
{
    public Guid EstabelecimentoId { get; set; }
    public string Nome { get; set; } = string.Empty;
    public string SituacaoComercial { get; set; } = string.Empty;
    public bool TemDelivery { get; set; }
    public bool TemIfood { get; set; }
    public int? PedidosPendentes { get; set; }
    /// <summary>Atribuidos, aguardando o aceite do motoboy ou em rota (mesmo agrupamento do painel de pedidos).</summary>
    public int? PedidosEmRota { get; set; }
    public int? MotoboysOnline { get; set; }
    public int? TransferenciasPendentes { get; set; }
    public int? IfoodAlertasCriticos { get; set; }
    public int? IfoodAlertasAviso { get; set; }
}
