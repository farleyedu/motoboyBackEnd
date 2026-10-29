using System.Text.Json;
using APIBack.Acontecimentos;
using Dapper;
using Npgsql;

namespace APIBack.GestaoLogica.Estoque;

/// <summary>Item do estoque para a tela: saldo do livro, mínimo da loja e o custo vigente por unidade base (null = custo desconhecido).</summary>
public sealed record ItemDeEstoque(Guid InsumoId, string Nome, string Unidade, string? Categoria, string? FotoUrl,
    decimal Saldo, decimal? Minimo, decimal? CustoMedio);

public sealed record MotivoDeSaida(string Id, string Rotulo, string Natureza, string? Cor, bool Padrao, bool Ativo);

public sealed record MotivoSaidaRequest(string? Id, string? Rotulo, string? Natureza, string? Cor, bool? Ativo);

public sealed record MinimoRequest(decimal? Minimo);

public sealed record RascunhoContagem(JsonElement Dados, DateTimeOffset AtualizadoEm);

/// <summary>
/// O que o Estoque guardava no navegador (CONTRATOS §9.5): mínimo, motivos de saída e rascunho da contagem. Configuração da loja,
/// não livro. O operador de caixa lê o saldo e escreve o rascunho (a contagem às cegas), mas não muda mínimo nem motivos.
/// </summary>
public interface IEstoquePoliticasService
{
    Task<IReadOnlyList<ItemDeEstoque>> Itens(Guid loja, CancellationToken ct);
    Task<decimal?> DefinirMinimo(Guid loja, int usuario, Guid itemId, decimal? minimo, CancellationToken ct);
    Task<IReadOnlyList<MotivoDeSaida>> Motivos(Guid loja, CancellationToken ct);
    Task<IReadOnlyList<MotivoDeSaida>> SalvarMotivo(Guid loja, int usuario, MotivoSaidaRequest req, CancellationToken ct);
    Task<RascunhoContagem?> LerRascunho(Guid loja, CancellationToken ct);
    Task SalvarRascunho(Guid loja, int usuario, JsonElement dados, CancellationToken ct);
    Task DescartarRascunho(Guid loja, CancellationToken ct);
}

public sealed class EstoquePoliticasService(NpgsqlDataSource dataSource) : IEstoquePoliticasService
{
    public static readonly IReadOnlyList<MotivoDeSaida> MotivosDoZippy = new MotivoDeSaida[]
    {
        new("vencimento", "Vencimento", "perda", "#FF6B81", true, true),
        new("danificado", "Produto danificado", "perda", "#FB923C", true, true),
        new("preparo", "Erro de preparo", "perda", "#FFC35C", true, true),
        new("cancelado", "Pedido cancelado após produção", "perda", "#F472B6", true, true),
        new("acidente", "Acidente", "perda", "#F97316", true, true),
        new("descarte", "Descarte operacional", "perda", "#9DA9B7", true, true),
        new("interno", "Consumo interno", "uso", "#38BDF8", true, true),
        new("cortesia", "Cortesia", "uso", "#A78BFA", true, true),
    };

    private const int MaxRascunhoBytes = 200_000;

    public async Task<IReadOnlyList<ItemDeEstoque>> Itens(Guid loja, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        if (!await c.ExecuteScalarAsync<bool>("SELECT to_regclass('insumo') IS NOT NULL"))
            throw Invalido("Os insumos da loja ainda não existem: ative a Gestão e Inteligência.");
        // Custo = vigência mais recente (gratuito = 0). Sem linha em insumo_custo o custo é desconhecido, nunca zero.
        return (await c.QueryAsync<ItemDeEstoque>(@"
SELECT i.id AS InsumoId, i.nome, i.unidade, i.categoria, i.foto_url AS FotoUrl,
       coalesce(s.saldo, 0) AS Saldo, m.minimo AS Minimo,
       CASE WHEN k.id IS NULL THEN NULL WHEN k.gratuito THEN 0 ELSE k.custo_base END AS CustoMedio
  FROM insumo i
  LEFT JOIN (SELECT item_id, sum(quantidade) AS saldo FROM gestao_movimentos_estoque
              WHERE estabelecimento_id = @loja GROUP BY item_id) s ON s.item_id = i.id
  LEFT JOIN gestao_estoque_minimos m ON m.estabelecimento_id = i.estabelecimento_id AND m.item_id = i.id
  LEFT JOIN LATERAL (SELECT id, gratuito, custo_base FROM insumo_custo
                      WHERE estabelecimento_id = i.estabelecimento_id AND insumo_id = i.id
                      ORDER BY vigencia_inicio DESC, criado_em DESC LIMIT 1) k ON true
 WHERE i.estabelecimento_id = @loja AND i.ativo
 ORDER BY i.nome", new { loja })).ToList();
    }

    public async Task<decimal?> DefinirMinimo(Guid loja, int usuario, Guid itemId, decimal? minimo, CancellationToken ct)
    {
        if (minimo is < 0) throw Invalido("O mínimo não pode ser negativo.");
        await using var c = await dataSource.OpenConnectionAsync(ct);
        if (!await c.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM insumo WHERE id = @itemId AND estabelecimento_id = @loja)", new { itemId, loja }))
            throw new GestaoException("NAO_ENCONTRADO", "Insumo não encontrado nesta loja.", 404);
        if (minimo is null or 0)
        {
            await c.ExecuteAsync("DELETE FROM gestao_estoque_minimos WHERE estabelecimento_id = @loja AND item_id = @itemId", new { loja, itemId });
            return null;
        }
        var valor = Math.Round(minimo.Value, 4);
        await c.ExecuteAsync(@"
INSERT INTO gestao_estoque_minimos (estabelecimento_id, item_id, minimo, atualizado_por, atualizado_em)
VALUES (@loja, @itemId, @valor, @usuario, now())
ON CONFLICT (estabelecimento_id, item_id) DO UPDATE SET minimo = EXCLUDED.minimo, atualizado_por = EXCLUDED.atualizado_por, atualizado_em = now()",
            new { loja, itemId, valor, usuario });
        return valor;
    }

    public async Task<IReadOnlyList<MotivoDeSaida>> Motivos(Guid loja, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        var linhas = (await c.QueryAsync<(string Id, bool Padrao, string? Rotulo, string? Natureza, string? Cor, bool Ativo)>(
            "SELECT id, padrao, rotulo, natureza, cor, ativo FROM gestao_estoque_motivos WHERE estabelecimento_id = @loja ORDER BY criado_em", new { loja })).ToList();
        var desligados = linhas.Where(l => l.Padrao && !l.Ativo).Select(l => l.Id).ToHashSet();
        return MotivosDoZippy.Select(m => m with { Ativo = !desligados.Contains(m.Id) })
            .Concat(linhas.Where(l => !l.Padrao).Select(l => new MotivoDeSaida(l.Id, l.Rotulo!, l.Natureza!, l.Cor, false, l.Ativo)))
            .ToList();
    }

    /// <summary>Id de motivo do Zippy: só liga/desliga. Id novo ou da loja: cria ou edita (rótulo, natureza, cor, ativo).</summary>
    public async Task<IReadOnlyList<MotivoDeSaida>> SalvarMotivo(Guid loja, int usuario, MotivoSaidaRequest req, CancellationToken ct)
    {
        await using (var c = await dataSource.OpenConnectionAsync(ct))
        {
            var id = req.Id?.Trim();
            if (!string.IsNullOrEmpty(id) && MotivosDoZippy.Any(m => m.Id == id))
            {
                await c.ExecuteAsync(@"
INSERT INTO gestao_estoque_motivos (estabelecimento_id, id, padrao, ativo, criado_por) VALUES (@loja, @id, true, @ativo, @usuario)
ON CONFLICT (estabelecimento_id, id) DO UPDATE SET ativo = EXCLUDED.ativo", new { loja, id, ativo = req.Ativo ?? true, usuario });
            }
            else
            {
                var rotulo = req.Rotulo?.Trim();
                if (string.IsNullOrEmpty(rotulo) || rotulo.Length > 80) throw Invalido("Dê um nome ao motivo (até 80 letras).");
                if (req.Natureza is not ("perda" or "uso")) throw Invalido("Diga se o motivo é perda ou uso.");
                if (req.Cor is { Length: > 9 }) throw Invalido("Cor inválida.");
                if (string.IsNullOrEmpty(id))
                {
                    id = "loja-" + Guid.NewGuid().ToString("N")[..12];
                    await c.ExecuteAsync(@"
INSERT INTO gestao_estoque_motivos (estabelecimento_id, id, padrao, rotulo, natureza, cor, ativo, criado_por)
VALUES (@loja, @id, false, @rotulo, @natureza, @cor, @ativo, @usuario)",
                        new { loja, id, rotulo, natureza = req.Natureza, cor = req.Cor, ativo = req.Ativo ?? true, usuario });
                }
                else if (await c.ExecuteAsync(@"
UPDATE gestao_estoque_motivos SET rotulo = @rotulo, natureza = @natureza, cor = @cor, ativo = coalesce(@ativo, ativo)
 WHERE estabelecimento_id = @loja AND id = @id AND NOT padrao", new { loja, id, rotulo, natureza = req.Natureza, cor = req.Cor, ativo = req.Ativo }) == 0)
                    throw new GestaoException("NAO_ENCONTRADO", "Motivo não encontrado nesta loja.", 404);
            }
        }
        return await Motivos(loja, ct);
    }

    public async Task<RascunhoContagem?> LerRascunho(Guid loja, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        var l = await c.QuerySingleOrDefaultAsync<(string Dados, DateTimeOffset AtualizadoEm)?>(
            "SELECT dados::text, atualizado_em FROM gestao_estoque_contagem_rascunho WHERE estabelecimento_id = @loja", new { loja });
        return l is null ? null : new RascunhoContagem(JsonDocument.Parse(l.Value.Dados).RootElement.Clone(), l.Value.AtualizadoEm);
    }

    public async Task SalvarRascunho(Guid loja, int usuario, JsonElement dados, CancellationToken ct)
    {
        if (dados.ValueKind != JsonValueKind.Object) throw Invalido("Envie o rascunho da contagem.");
        var texto = dados.GetRawText();
        if (texto.Length > MaxRascunhoBytes) throw Invalido("O rascunho ficou grande demais.");
        await using var c = await dataSource.OpenConnectionAsync(ct);
        await c.ExecuteAsync(@"
INSERT INTO gestao_estoque_contagem_rascunho (estabelecimento_id, dados, atualizado_por, atualizado_em)
VALUES (@loja, @texto::jsonb, @usuario, now())
ON CONFLICT (estabelecimento_id) DO UPDATE SET dados = EXCLUDED.dados, atualizado_por = EXCLUDED.atualizado_por, atualizado_em = now()",
            new { loja, texto, usuario });
    }

    public async Task DescartarRascunho(Guid loja, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        await c.ExecuteAsync("DELETE FROM gestao_estoque_contagem_rascunho WHERE estabelecimento_id = @loja", new { loja });
    }

    private static GestaoException Invalido(string m) => new("CAMPO_INVALIDO", m, 422);
}
