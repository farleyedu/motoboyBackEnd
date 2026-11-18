using Dapper;
using Npgsql;

namespace APIBack.Assistente;

// ============================================================================ dados crus (o que o banco agrega)

public sealed record LinhaUsoDia(DateOnly Dia, string Provedor, string Modelo, long Respostas, long Reserva, long Entrada, long Saida, long Cache,
    long Telas, long Rascunhos);

public sealed record LinhaUsoLoja(Guid Loja, string Nome, string Modelo, long Respostas, long Entrada, long Saida, long Cache, string Provedor = "");

public sealed record ContagemAcoes(long Propostas, long Confirmadas, long Recusadas, long Vencidas, long Falharam, long Desfeitas);

public sealed record Ideia(DateTimeOffset Em, Guid LojaId, string Loja, string Pergunta, string? Rascunho);

public sealed record DadosUso(
    IReadOnlyList<LinhaUsoDia> PorDia,
    IReadOnlyList<LinhaUsoLoja> PorLoja,
    IReadOnlyDictionary<Guid, long> ConversasPorLoja,
    long Conversas,
    IReadOnlyDictionary<DateOnly, long> ErrosPorDia,
    IReadOnlyList<(Guid Loja, DateOnly Dia)> Limites,
    ContagemAcoes Acoes,
    long ChamadosEnviados,
    long SemFuncionalidade,
    IReadOnlyList<Ideia> Ideias,
    IReadOnlyList<LinhaUsoItem>? PorItem = null,
    IReadOnlyList<LinhaPulo>? Pulos = null);

/// <summary>Respostas de um item da cadeia no período.</summary>
public sealed record LinhaUsoItem(string ItemId, string Provedor, string Modelo, long Respostas);

/// <summary>Vezes que um item foi pulado no período, por motivo.</summary>
public sealed record LinhaPulo(string ItemId, string Motivo, long Vezes);

// ============================================================================ resposta (CONTRATO-API §8.2)

public sealed record TokensUsoDto(long Entrada, long Saida, long CacheLidos);

public sealed record TotaisUsoDto(long Conversas, long Perguntas, long Respostas, TokensUsoDto Tokens, decimal CustoUsd, bool CustoCompleto,
    long RespostasDaReserva, long Erros, long BloqueiosPorLimite, long LojasNoLimite);

public sealed record DiaUsoDto(DateOnly Dia, long Perguntas, long Respostas, long Erros, TokensUsoDto Tokens, decimal CustoUsd);

public sealed record ModeloUsoDto(string Provedor, string Modelo, long Respostas, long Reserva, TokensUsoDto Tokens, decimal? CustoUsd, bool TemPreco);

public sealed record LojaUsoDto(Guid LojaId, string Loja, long Conversas, long Respostas, TokensUsoDto Tokens, decimal CustoUsd, long DiasNoLimite);

public sealed record AcoesUsoDto(long Propostas, long Confirmadas, long Recusadas, long Vencidas, long Falharam, long Desfeitas);

public sealed record ResultadosUsoDto(AcoesUsoDto Acoes, long TelasAbertas, long RascunhosDeChamado, long ChamadosEnviados, long SemFuncionalidade);

public sealed record ItemUsoDto(string ItemId, string Provedor, string Modelo, bool Gratis, long Respostas, IReadOnlyDictionary<string, long> Pulos,
    DateTimeOffset? EsgotadoAte, bool Desligado, string? Erro, bool NaCadeia);

public sealed record UsoDto(DateOnly De, DateOnly Ate, TotaisUsoDto Totais, IReadOnlyList<DiaUsoDto> PorDia, IReadOnlyList<ModeloUsoDto> PorModelo,
    IReadOnlyList<LojaUsoDto> PorLoja, ResultadosUsoDto Resultados, IReadOnlyList<Ideia> Ideias, IReadOnlyList<ItemUsoDto> PorItem);

/// <summary>Contas do uso e custo, sem banco. Público para teste.</summary>
public static class UsoCalculo
{
    public const int MaxDias = 366;
    public const int TopLojas = 20;

    /// <summary>US$ estimado: entrada sem cache × entrada + saída × saída + cache lido × cache (preços por milhão). Sem preço = nulo.</summary>
    public static decimal? Custo(long entrada, long saida, long cache, PrecoModelo? preco)
    {
        if (preco is null) return null;
        var semCache = Math.Max(0, entrada - cache);
        return Math.Round((semCache * preco.Entrada + saida * preco.Saida + cache * preco.CacheLido) / 1_000_000m, 6);
    }

    /// <summary>Período pedido (dias de São Paulo). Padrão: últimos 30 dias contando hoje.</summary>
    public static (DateOnly De, DateOnly Ate) Periodo(string? de, string? ate, DateOnly hoje)
    {
        DateOnly? Ler(string? v) => string.IsNullOrWhiteSpace(v) ? null
            : DateOnly.TryParseExact(v, "yyyy-MM-dd", out var d) ? d : throw new AssistenteException(422, "PERIODO_INVALIDO", "Data inválida: use AAAA-MM-DD.");
        var fim = Ler(ate) ?? hoje;
        var inicio = Ler(de) ?? fim.AddDays(-29);
        if (inicio > fim) throw new AssistenteException(422, "PERIODO_INVALIDO", "A data inicial é depois da final.");
        if (fim.DayNumber - inicio.DayNumber + 1 > MaxDias) throw new AssistenteException(422, "PERIODO_INVALIDO", $"Escolha no máximo {MaxDias} dias.");
        return (inicio, fim);
    }

    /// <summary>Motivos de pulo mostrados por item (sempre presentes, mesmo com zero).</summary>
    public static readonly string[] MotivosMostrados =
    [
        MotivosPulo.Esgotado, MotivosPulo.LimiteDia, MotivosPulo.LimiteProvedor, MotivosPulo.ErroServidor, MotivosPulo.SemChave,
        MotivosPulo.Desligado, MotivosPulo.DesligadoErro, MotivosPulo.PagoBloqueado, MotivosPulo.PedidoRecusado,
    ];

    /// <summary>Preço de um modelo de um provedor: provedor grátis custa 0; senão a tabela; fora dela, sem preço.</summary>
    public static PrecoModelo? PrecoEfetivo(ConfigIA cfg, string provedor, string modelo) =>
        cfg.ProvedorDe(provedor)?.Gratis == true ? new PrecoModelo(modelo, 0, 0, 0) : cfg.PrecoDe(modelo);

    public static UsoDto Montar(DateOnly de, DateOnly ate, DadosUso d, ConfigIA cfg, FotoCadeia? foto = null, DateTimeOffset? agora = null)
    {
        PrecoModelo? Preco(string provedor, string modelo) => PrecoEfetivo(cfg, provedor, modelo);
        static TokensUsoDto T(long e, long s, long c) => new(e, s, c);

        var porModelo = d.PorDia.GroupBy(l => (l.Provedor, l.Modelo))
            .Select(g =>
            {
                var (e, s, c) = (g.Sum(x => x.Entrada), g.Sum(x => x.Saida), g.Sum(x => x.Cache));
                var preco = Preco(g.Key.Provedor, g.Key.Modelo);
                return new ModeloUsoDto(g.Key.Provedor, g.Key.Modelo, g.Sum(x => x.Respostas), g.Sum(x => x.Reserva), T(e, s, c), Custo(e, s, c, preco), preco is not null);
            })
            .OrderByDescending(m => m.CustoUsd ?? -1).ThenByDescending(m => m.Respostas).ToList();

        var porDia = new List<DiaUsoDto>();
        for (var dia = de; dia <= ate; dia = dia.AddDays(1))
        {
            var linhas = d.PorDia.Where(l => l.Dia == dia).ToList();
            var resp = linhas.Sum(l => l.Respostas);
            var errosDia = d.ErrosPorDia.GetValueOrDefault(dia);
            porDia.Add(new DiaUsoDto(dia, resp + errosDia, resp, errosDia,
                T(linhas.Sum(l => l.Entrada), linhas.Sum(l => l.Saida), linhas.Sum(l => l.Cache)),
                linhas.Sum(l => Custo(l.Entrada, l.Saida, l.Cache, Preco(l.Provedor, l.Modelo)) ?? 0m)));
        }

        var diasNoLimite = d.Limites.GroupBy(l => l.Loja).ToDictionary(g => g.Key, g => (long)g.Select(x => x.Dia).Distinct().Count());
        var porLoja = d.PorLoja.GroupBy(l => (l.Loja, l.Nome))
            .Select(g => new LojaUsoDto(g.Key.Loja, g.Key.Nome, d.ConversasPorLoja.GetValueOrDefault(g.Key.Loja), g.Sum(x => x.Respostas),
                T(g.Sum(x => x.Entrada), g.Sum(x => x.Saida), g.Sum(x => x.Cache)),
                g.Sum(x => Custo(x.Entrada, x.Saida, x.Cache, Preco(x.Provedor, x.Modelo)) ?? 0m), diasNoLimite.GetValueOrDefault(g.Key.Loja)))
            .OrderByDescending(l => l.CustoUsd).ThenByDescending(l => l.Respostas).Take(TopLojas).ToList();

        var respostas = d.PorDia.Sum(l => l.Respostas);
        var erros = d.ErrosPorDia.Values.Sum();
        var totais = new TotaisUsoDto(
            d.Conversas, respostas + erros, respostas,
            T(d.PorDia.Sum(l => l.Entrada), d.PorDia.Sum(l => l.Saida), d.PorDia.Sum(l => l.Cache)),
            porModelo.Sum(m => m.CustoUsd ?? 0m), porModelo.All(m => m.TemPreco),
            d.PorDia.Sum(l => l.Reserva), erros, d.Limites.Count, d.Limites.Select(l => l.Loja).Distinct().Count());

        var a = d.Acoes;
        return new UsoDto(de, ate, totais, porDia, porModelo, porLoja,
            new ResultadosUsoDto(new AcoesUsoDto(a.Propostas, a.Confirmadas, a.Recusadas, a.Vencidas, a.Falharam, a.Desfeitas),
                d.PorDia.Sum(l => l.Telas), d.PorDia.Sum(l => l.Rascunhos), d.ChamadosEnviados, d.SemFuncionalidade),
            d.Ideias, PorItem(d, cfg, foto, agora ?? DateTimeOffset.UtcNow));
    }

    /// <summary>Uso por item: os da cadeia na ordem dela, depois os que saíram da cadeia mas aparecem no período.</summary>
    public static IReadOnlyList<ItemUsoDto> PorItem(DadosUso d, ConfigIA cfg, FotoCadeia? foto, DateTimeOffset agora)
    {
        var respostas = (d.PorItem ?? []).ToList();
        var pulos = (d.Pulos ?? []).ToList();
        IReadOnlyDictionary<string, long> Pulos(string id) => MotivosMostrados.ToDictionary(m => m, m => pulos.Where(p => p.ItemId == id && p.Motivo == m).Sum(p => p.Vezes));
        var lista = cfg.Cadeia.Select(i =>
        {
            var e = foto is null ? null : RegrasCadeia.EstadoValido(i, foto);
            return new ItemUsoDto(i.Id, i.Provedor, i.Modelo, cfg.ProvedorDe(i.Provedor)?.Gratis == true,
                respostas.Where(r => r.ItemId == i.Id).Sum(r => r.Respostas), Pulos(i.Id),
                e?.EsgotadoAte is { } ate && ate > agora ? ate : null, e?.Desligado == true, e?.Erro, true);
        }).ToList();
        var naCadeia = cfg.Cadeia.Select(i => i.Id).ToHashSet();
        lista.AddRange(respostas.Where(r => !naCadeia.Contains(r.ItemId)).GroupBy(r => r.ItemId).Select(g =>
        {
            var ultimo = g.Last();
            return new ItemUsoDto(g.Key, ultimo.Provedor, ultimo.Modelo, cfg.ProvedorDe(ultimo.Provedor)?.Gratis == true, g.Sum(r => r.Respostas),
                Pulos(g.Key), null, false, null, false);
        }));
        return lista;
    }
}

// ============================================================================ leitura no banco

public interface IAssistenteUsoRepositorio
{
    /// <summary>Tudo do período [inicioUtc, fimUtc). <paramref name="agoraUtc"/> decide quais pendentes já venceram.</summary>
    Task<DadosUso> Ler(DateTimeOffset inicioUtc, DateTimeOffset fimUtc, DateTimeOffset agoraUtc, CancellationToken ct);
    /// <summary>Modelos que já responderam alguma vez (para apontar os que estão sem preço).</summary>
    Task<IReadOnlyList<(string Provedor, string Modelo)>> ModelosUsados(CancellationToken ct);
    /// <summary>Quantas perguntas desde <paramref name="inicio"/> ficaram sem nenhuma IA, e quando foi a primeira.</summary>
    Task<(long Vezes, DateTimeOffset? Primeira)> CadeiaEsgotada(DateTimeOffset inicio, CancellationToken ct);
}

public sealed class AssistenteUsoRepositorio(NpgsqlDataSource source) : IAssistenteUsoRepositorio
{
    private const string Dia = "(criado_em AT TIME ZONE 'America/Sao_Paulo')::date";

    public async Task<DadosUso> Ler(DateTimeOffset inicio, DateTimeOffset fim, DateTimeOffset agora, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        var p = new { inicio, fim, agora };
        CommandDefinition Cmd(string sql) => new(sql, p, cancellationToken: ct);

        var dias = (await c.QueryAsync<(DateTime Dia, string Provedor, string Modelo, long Respostas, long Reserva, long Entrada, long Saida, long Cache, long Telas, long Rascunhos)>(Cmd($"""
            SELECT {Dia} AS dia, provedor, modelo, COUNT(*), COUNT(*) FILTER (WHERE usou_reserva),
                   COALESCE(SUM(tokens_entrada), 0)::bigint, COALESCE(SUM(tokens_saida), 0)::bigint, COALESCE(SUM(tokens_cache_lidos), 0)::bigint,
                   COALESCE(SUM(telas), 0)::bigint, COALESCE(SUM(rascunhos), 0)::bigint
            FROM assistente_uso WHERE criado_em >= @inicio AND criado_em < @fim
            GROUP BY 1, 2, 3
            """)))
            .Select(r => new LinhaUsoDia(DateOnly.FromDateTime(r.Dia), r.Provedor, r.Modelo, r.Respostas, r.Reserva, r.Entrada, r.Saida, r.Cache, r.Telas, r.Rascunhos))
            .ToList();

        var lojas = (await c.QueryAsync<(Guid Loja, string Nome, string Modelo, string Provedor, long Respostas, long Entrada, long Saida, long Cache)>(Cmd("""
            SELECT u.estabelecimento_id, COALESCE(NULLIF(e.nome_fantasia, ''), '?'), u.modelo, u.provedor, COUNT(*),
                   SUM(u.tokens_entrada)::bigint, SUM(u.tokens_saida)::bigint, SUM(u.tokens_cache_lidos)::bigint
            FROM assistente_uso u JOIN estabelecimentos e ON e.id = u.estabelecimento_id
            WHERE u.criado_em >= @inicio AND u.criado_em < @fim
            GROUP BY 1, 2, 3, 4
            """))).Select(r => new LinhaUsoLoja(r.Loja, r.Nome, r.Modelo, r.Respostas, r.Entrada, r.Saida, r.Cache, r.Provedor)).ToList();

        var conversasPorLoja = (await c.QueryAsync<(Guid Loja, long N)>(Cmd("""
            SELECT estabelecimento_id, COUNT(DISTINCT conversa_id) FROM assistente_uso
            WHERE criado_em >= @inicio AND criado_em < @fim GROUP BY 1
            """))).ToDictionary(r => r.Loja, r => r.N);

        var conversas = await c.ExecuteScalarAsync<long>(Cmd(
            "SELECT COUNT(DISTINCT conversa_id) FROM assistente_uso WHERE criado_em >= @inicio AND criado_em < @fim"));

        var erros = (await c.QueryAsync<(DateTime Dia, long N)>(Cmd($"""
            SELECT {Dia}, COUNT(*) FROM assistente_eventos WHERE tipo = 'erro' AND criado_em >= @inicio AND criado_em < @fim GROUP BY 1
            """))).ToDictionary(r => DateOnly.FromDateTime(r.Dia), r => r.N);

        var limites = (await c.QueryAsync<(Guid Loja, DateTime Dia)>(Cmd("""
            SELECT estabelecimento_id, dia FROM assistente_eventos WHERE tipo = 'limite' AND criado_em >= @inicio AND criado_em < @fim
            """))).Select(r => (r.Loja, DateOnly.FromDateTime(r.Dia))).ToList();

        var acoes = await c.QuerySingleAsync<(long Propostas, long Confirmadas, long Recusadas, long Vencidas, long Falharam, long Desfeitas)>(Cmd("""
            SELECT COUNT(*) FILTER (WHERE desfaz_acao_id IS NULL),
                   COUNT(*) FILTER (WHERE desfaz_acao_id IS NULL AND situacao = 'executada'),
                   COUNT(*) FILTER (WHERE desfaz_acao_id IS NULL AND situacao = 'recusada'),
                   COUNT(*) FILTER (WHERE desfaz_acao_id IS NULL AND situacao = 'pendente' AND expira_em <= @agora),
                   COUNT(*) FILTER (WHERE desfaz_acao_id IS NULL AND situacao = 'falhou'),
                   COUNT(*) FILTER (WHERE desfaz_acao_id IS NOT NULL AND situacao = 'executada')
            FROM assistente_acoes WHERE criada_em >= @inicio AND criada_em < @fim
            """));

        var eventos = (await c.QueryAsync<(string Tipo, long N)>(Cmd("""
            SELECT tipo, COUNT(*) FROM assistente_eventos
            WHERE tipo IN ('chamado_enviado', 'sem_funcionalidade') AND criado_em >= @inicio AND criado_em < @fim GROUP BY 1
            """))).ToDictionary(r => r.Tipo, r => r.N);

        var ideias = (await c.QueryAsync<(DateTime Em, Guid Loja, string Nome, string? Pergunta, string? Rascunho)>(Cmd("""
            SELECT v.criado_em, v.estabelecimento_id, COALESCE(NULLIF(e.nome_fantasia, ''), '?'), v.detalhe, v.extra
            FROM assistente_eventos v JOIN estabelecimentos e ON e.id = v.estabelecimento_id
            WHERE v.tipo = 'sem_funcionalidade' AND v.criado_em >= @inicio AND v.criado_em < @fim
            ORDER BY v.criado_em DESC LIMIT 20
            """))).Select(r => new Ideia(new DateTimeOffset(DateTime.SpecifyKind(r.Em, DateTimeKind.Utc)), r.Loja, r.Nome, r.Pergunta ?? "", r.Rascunho)).ToList();

        // Colunas da migração 20261205_07 (cadeia): sem ela, uso por item vem vazio.
        IReadOnlyList<LinhaUsoItem> porItem = [];
        IReadOnlyList<LinhaPulo> pulos = [];
        try
        {
            porItem = (await c.QueryAsync<(string ItemId, string Provedor, string Modelo, long N)>(Cmd("""
                SELECT item_id, provedor, modelo, COUNT(*) FROM assistente_uso
                WHERE item_id IS NOT NULL AND criado_em >= @inicio AND criado_em < @fim GROUP BY 1, 2, 3
                """))).Select(r => new LinhaUsoItem(r.ItemId, r.Provedor, r.Modelo, r.N)).ToList();
            pulos = (await c.QueryAsync<(string ItemId, string Motivo, long N)>(Cmd("""
                SELECT extra, detalhe, COUNT(*) FROM assistente_eventos
                WHERE tipo = 'pulo' AND extra IS NOT NULL AND detalhe IS NOT NULL AND criado_em >= @inicio AND criado_em < @fim GROUP BY 1, 2
                """))).Select(r => new LinhaPulo(r.ItemId, r.Motivo, r.N)).ToList();
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedColumn) { }

        return new DadosUso(dias, lojas, conversasPorLoja, conversas, erros, limites,
            new ContagemAcoes(acoes.Propostas, acoes.Confirmadas, acoes.Recusadas, acoes.Vencidas, acoes.Falharam, acoes.Desfeitas),
            eventos.GetValueOrDefault(TiposEvento.ChamadoEnviado), eventos.GetValueOrDefault(TiposEvento.SemFuncionalidade), ideias, porItem, pulos);
    }

    public async Task<(long Vezes, DateTimeOffset? Primeira)> CadeiaEsgotada(DateTimeOffset inicio, CancellationToken ct)
    {
        try
        {
            await using var c = await source.OpenConnectionAsync(ct);
            var r = await c.QuerySingleAsync<(long Vezes, DateTime? Primeira)>(new CommandDefinition(
                "SELECT COUNT(*), MIN(criado_em) FROM assistente_eventos WHERE tipo = 'erro' AND extra = 'cadeia_esgotada' AND criado_em >= @inicio",
                new { inicio }, cancellationToken: ct));
            return (r.Vezes, r.Primeira is { } p ? new DateTimeOffset(DateTime.SpecifyKind(p, DateTimeKind.Utc)) : null);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return (0, null);
        }
    }

    public async Task<IReadOnlyList<(string Provedor, string Modelo)>> ModelosUsados(CancellationToken ct)
    {
        try
        {
            await using var c = await source.OpenConnectionAsync(ct);
            return (await c.QueryAsync<(string, string)>(new CommandDefinition("SELECT DISTINCT provedor, modelo FROM assistente_uso ORDER BY 2", cancellationToken: ct))).ToList();
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return [];
        }
    }
}
