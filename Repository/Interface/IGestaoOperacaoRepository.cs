namespace APIBack.Repository.Interface;

public sealed class OperacaoLojaRow
{
    public Guid Id { get; set; }
    public string Nome { get; set; } = string.Empty;
    public string Situacao { get; set; } = string.Empty;
    public string[] Modulos { get; set; } = Array.Empty<string>();
}

public sealed class OperacaoContadoresRow
{
    public Guid EstabelecimentoId { get; set; }
    public int Pendentes { get; set; }
    public int EmRota { get; set; }
    public int MotoboysOnline { get; set; }
    public int TransferenciasPendentes { get; set; }
}

public sealed class OperacaoIfoodRow
{
    public Guid EstabelecimentoId { get; set; }
    public int Criticos { get; set; }
    public int Avisos { get; set; }
}

public interface IGestaoOperacaoRepository
{
    /// <summary>Lojas operacionais da empresa: todas (gestor) ou so as com vinculo ativo do usuario.</summary>
    Task<IReadOnlyList<OperacaoLojaRow>> ListarLojasAsync(Guid empresaId, int userId, bool todasAsLojas, CancellationToken ct = default);
    /// <summary>Pedidos, motoboys online e transferencias de varias lojas em uma consulta agrupada (sem N consultas por loja).</summary>
    Task<IReadOnlyList<OperacaoContadoresRow>> ContadoresAsync(IReadOnlyCollection<Guid> lojas, CancellationToken ct = default);
    /// <summary>Alertas iFood criticos e de aviso por loja; vazio quando o schema do iFood ainda nao existe.</summary>
    Task<IReadOnlyList<OperacaoIfoodRow>> AlertasIfoodAsync(IReadOnlyCollection<Guid> lojas, CancellationToken ct = default);
}
