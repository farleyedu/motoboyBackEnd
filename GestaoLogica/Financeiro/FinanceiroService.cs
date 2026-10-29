using APIBack.Acontecimentos;
using Dapper;
using Npgsql;

namespace APIBack.GestaoLogica.Financeiro;

public sealed record GrupoDeGastoDto(string Chave, string Rotulo, decimal Valor);

public sealed record ParaOndeFoiDto(IReadOnlyList<GrupoDeGastoDto> Grupos, decimal Total);

/// <summary>Contas a pagar: <c>Atrasado</c> (vencido antes de hoje, de qualquer mês) e <c>Proximos7</c> (de hoje a 7 dias).</summary>
public sealed record APagarResumoDto(decimal Atrasado, int QuantidadeAtrasada, decimal Proximos7, int QuantidadeProximos7);

/// <summary>Agregados do Financeiro calculados no servidor (antes montados na tela).</summary>
public sealed record ResumoFinanceiroDto(DateOnly Mes, ParaOndeFoiDto ParaOndeFoi, APagarResumoDto APagar);

/// <summary>Leituras agregadas do livro para a tela do Financeiro. Só lê: nada aqui escreve no livro.</summary>
public interface IFinanceiroService
{
    Task<IReadOnlyList<LancamentoDto>> APagar(Guid loja, DateOnly ate, CancellationToken ct);
    Task<ResumoFinanceiroDto> Resumo(Guid loja, DateOnly mes, DateOnly hoje, CancellationToken ct);
}

public sealed class FinanceiroService(NpgsqlDataSource dataSource) : IFinanceiroService
{
    public async Task<IReadOnlyList<LancamentoDto>> APagar(Guid loja, DateOnly ate, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        return await AcontecimentoSql.DespesasEmAberto(c, loja, ate);
    }

    public async Task<ResumoFinanceiroDto> Resumo(Guid loja, DateOnly mes, DateOnly hoje, CancellationToken ct)
    {
        var inicio = AcontecimentoRegras.Mes(mes);
        await using var c = await dataSource.OpenConnectionAsync(ct);
        var pagos = (await c.QueryAsync<FinanceiroRegras.Gasto>(@"
SELECT s.valor AS Valor, s.categoria AS Categoria, a.tipo AS Tipo, a.modulo AS Modulo
  FROM gestao_lancamentos_situacao s
  JOIN gestao_acontecimentos a ON a.estabelecimento_id = s.estabelecimento_id AND a.id = s.acontecimento_id
 WHERE s.estabelecimento_id = @loja AND s.natureza = 'despesa' AND s.status = 'pago'
   AND date_trunc('month', s.data_pagamento)::date = @inicio", new { loja, inicio })).ToList();
        var abertos = await AcontecimentoSql.DespesasEmAberto(c, loja, hoje.AddDays(7));
        return new ResumoFinanceiroDto(inicio, FinanceiroRegras.ParaOndeFoi(pagos), FinanceiroRegras.APagar(abertos, hoje));
    }
}

/// <summary>Regras puras do Financeiro (as mesmas da tela, agora no servidor).</summary>
public static class FinanceiroRegras
{
    public sealed class Gasto
    {
        public decimal Valor { get; set; }
        public string? Categoria { get; set; }
        public string? Tipo { get; set; }
        public string? Modulo { get; set; }
    }

    private static readonly HashSet<string> Fixas = ["aluguel", "energia", "água", "agua", "internet", "contas da casa", "despesas fixas", "impostos"];
    private static readonly HashSet<string> Servicos = ["serviços", "servicos", "manutenção", "manutencao", "fachada"];

    public static (string Chave, string Rotulo) GrupoDe(string? categoria, string? tipo, string? modulo)
    {
        var cat = (categoria ?? "").Trim().ToLowerInvariant();
        if (modulo == ModuloGestao.Compras || tipo == "compra") return ("compras", "Compras de insumos");
        if (modulo == ModuloGestao.Equipe || tipo == "turno") return ("equipe", "Equipe");
        if (cat == "taxas") return ("taxas", "Taxas iFood e cartão");
        if (Fixas.Contains(cat)) return ("fixas", "Despesas fixas");
        if (Servicos.Contains(cat)) return ("servicos", "Serviços e manutenção");
        return ("outras", "Outras despesas");
    }

    /// <summary>Despesas pagas no mês por grupo (correções entram com o seu sinal). Retirada e transferência não são despesa.</summary>
    public static ParaOndeFoiDto ParaOndeFoi(IEnumerable<Gasto> pagos)
    {
        var grupos = pagos.GroupBy(p => GrupoDe(p.Categoria, p.Tipo, p.Modulo))
            .Select(g => new GrupoDeGastoDto(g.Key.Chave, g.Key.Rotulo, Math.Round(g.Sum(x => x.Valor), 2)))
            .Where(g => g.Valor > 0).OrderByDescending(g => g.Valor).ToList();
        return new ParaOndeFoiDto(grupos, grupos.Sum(g => g.Valor));
    }

    /// <summary>Valor líquido de cada despesa em aberto (original + correções) e o que está atrasado ou vence em 7 dias.</summary>
    public static APagarResumoDto APagar(IReadOnlyList<LancamentoDto> lancamentos, DateOnly hoje)
    {
        var liquido = lancamentos.Where(l => l.CorrigeLancamentoId is null).ToDictionary(l => l.Id, l => l.Valor);
        foreach (var l in lancamentos.Where(l => l.CorrigeLancamentoId is { } alvo && liquido.ContainsKey(alvo)))
            liquido[l.CorrigeLancamentoId!.Value] += l.Valor;
        var abertos = lancamentos.Where(l => l.CorrigeLancamentoId is null && l.DataVencimento is not null && liquido[l.Id] > 0).ToList();
        var atrasados = abertos.Where(l => l.DataVencimento < hoje).ToList();
        var proximos = abertos.Where(l => l.DataVencimento >= hoje && l.DataVencimento <= hoje.AddDays(7)).ToList();
        return new APagarResumoDto(Math.Round(atrasados.Sum(l => liquido[l.Id]), 2), atrasados.Count,
            Math.Round(proximos.Sum(l => liquido[l.Id]), 2), proximos.Count);
    }
}
