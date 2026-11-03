using System;
using System.Collections.Generic;
using System.Linq;

namespace APIBack.Acompanhamento.Incidentes
{
    public static class CategoriasIncidente
    {
        public const string Ifood = "ifood";
        public const string CanalMeta = "canal_meta";
        public const string Robo = "robo";
        public const string FilaPedidos = "fila_pedidos";
        public const string Impressao = "impressao";
        public const string MotoboySemPosicao = "motoboy_sem_posicao";
        public const string Endereco = "endereco";
        public const string Cardapio = "cardapio";
        public const string Cobranca = "cobranca";
        public const string Chamados = "chamados";
        public const string Implantacao = "implantacao";

        /// <summary>categoria -> fonte que falta (null = ha evidencia automatica hoje). Sem fonte nao ha incidente inventado.</summary>
        public static readonly IReadOnlyDictionary<string, string?> FonteFaltante = new Dictionary<string, string?>
        {
            [Ifood] = null,
            [CanalMeta] = null,
            [Robo] = "telemetria de execucao do robo (heartbeat/erros por loja)",
            [FilaPedidos] = "metrica de atraso da fila de pedidos por loja",
            [Impressao] = null, // detectada por impressao_job (pendente ha mais de N minutos ou com erro)
            [MotoboySemPosicao] = "telemetria de ultima posicao por motoboy ativo",
            [Endereco] = "taxa de falha de geocodificacao/consulta de endereco por loja",
            [Cardapio] = "verificacao de publicacao e erros do cardapio web por loja",
            [Cobranca] = "evento de falha de cobranca por assinatura (hoje so ha status)",
            [Chamados] = "indicador de volume de chamados por assunto",
            [Implantacao] = "marco de implantacao com prazo por loja",
        };

        public static bool Existe(string? categoria) => categoria != null && FonteFaltante.ContainsKey(categoria);
    }

    public static class ExecutoresIncidente
    {
        public const string Zippy = "zippy", Loja = "loja", Terceiro = "terceiro", Indeterminado = "indeterminado";
        public static readonly string[] Todos = { Zippy, Loja, Terceiro, Indeterminado };
    }

    public sealed record SinalFalha(
        string Categoria, string Fonte, string Assinatura, Guid LojaId, DateTimeOffset Inicio, string CausaProvavel, string Executor, string ProvaJson);

    public sealed record FalhaAberta(Guid Id, string Categoria, string Assinatura, Guid LojaId, DateTimeOffset Inicio, string CausaProvavel, string Executor);

    public sealed record GrupoCompartilhado(string Categoria, string Assinatura, DateTimeOffset Inicio, string CausaProvavel, string Executor, IReadOnlyList<Guid> LojaIds, IReadOnlyList<Guid> FalhaIds);

    /// <summary>Regra deterministica: 3+ lojas distintas com falha aberta de mesma assinatura viram um unico incidente.</summary>
    public static class IncidenteAgregador
    {
        public const int MinimoLojas = 3;

        public static IReadOnlyList<GrupoCompartilhado> Agrupar(IEnumerable<FalhaAberta> abertas, int minimo = MinimoLojas)
        {
            var grupos = new List<GrupoCompartilhado>();
            foreach (var g in abertas.GroupBy(f => f.Assinatura).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var lojas = g.Select(f => f.LojaId).Distinct().OrderBy(x => x).ToList();
                if (lojas.Count < minimo) continue;
                var primeira = g.OrderBy(f => f.Inicio).ThenBy(f => f.Id).First();
                // Executor do agregado: o consenso das falhas; sem consenso, indeterminado (nunca se atribui culpa por palpite).
                var executores = g.Select(f => f.Executor).Distinct().ToList();
                var executor = executores.Count == 1 ? executores[0] : ExecutoresIncidente.Indeterminado;
                grupos.Add(new GrupoCompartilhado(primeira.Categoria, g.Key, primeira.Inicio, primeira.CausaProvavel, executor, lojas, g.Select(f => f.Id).OrderBy(x => x).ToList()));
            }
            return grupos;
        }
    }

    public sealed class IncidenteException : Exception
    {
        public IncidenteException(int status, string code, string message, object? details = null) : base(message)
        {
            Status = status;
            Code = code;
            Details = details;
        }

        public int Status { get; }
        public string Code { get; }
        public object? Details { get; }
    }

    // ---- contratos de saida

    public sealed record FalhaDto(Guid Id, string Categoria, string Fonte, Guid LojaId, string? LojaNome, DateTimeOffset Inicio, DateTimeOffset? Fim,
        string CausaProvavel, string Executor, string Prova, string Estado, string Origem, Guid? IncidenteId);

    public sealed record IncidenteDto(Guid Id, string Categoria, string Estado, DateTimeOffset Inicio, DateTimeOffset? Fim, string CausaProvavel, string Executor,
        int LojasAfetadas, IReadOnlyList<FalhaDto> Lojas, int EnviosPendentes);

    public sealed record CoberturaDto(string Categoria, string Estado, string? FonteFaltante);

    public sealed record IncidentesDto(DateTimeOffset GeradoEm, IReadOnlyList<IncidenteDto> Incidentes, IReadOnlyList<FalhaDto> FalhasIsoladas, IReadOnlyList<CoberturaDto> Cobertura);

    public sealed record AvisoDto(long Id, Guid IncidenteId, string Tipo, string Texto, DateTimeOffset CriadoEm, bool Lido);

    public sealed record EnvioDto(Guid LojaId, string Contato, string Estado, bool Enviado, string? Aviso);

    public sealed record AvisarResultadoDto(IReadOnlyList<EnvioDto> Envios);

    // ---- contratos de entrada

    public sealed class RegistrarFalhaRequest
    {
        public string? Categoria { get; set; }
        public string? Fonte { get; set; }
        public Guid? LojaId { get; set; }
        public DateTimeOffset? Inicio { get; set; }
        public string? CausaProvavel { get; set; }
        public string? Executor { get; set; }
        public string? Prova { get; set; }
        public string? Assinatura { get; set; }
    }

    public sealed class LidosRequest
    {
        public List<long> Ids { get; set; } = new();
    }

    public sealed class DestinatarioRequest
    {
        public Guid LojaId { get; set; }
        public string? Contato { get; set; }
    }

    public sealed class AvisarRequest
    {
        public List<DestinatarioRequest> Destinatarios { get; set; } = new();
        public string? Mensagem { get; set; }
        /// <summary>Um humano leu e aprovou o texto. Sem isso nada e preparado.</summary>
        public bool MensagemValidada { get; set; }
    }

    public sealed class NormalizouRequest
    {
        public bool Confirmado { get; set; }
        public string? Nota { get; set; }
    }
}
