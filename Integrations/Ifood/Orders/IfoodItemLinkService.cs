using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace APIBack.Integrations.Ifood.Orders;

public enum IfoodLinkAction
{
    /// <summary>Já existe vínculo confirmado para o id do item: vale ele, sem olhar o nome.</summary>
    UseExisting,
    /// <summary>Exatamente um produto ativo da loja tem o mesmo nome: vincula (origem "nome").</summary>
    AutoConfirm,
    /// <summary>Nenhum produto, ou mais de um, com esse nome: fica pendente para uma pessoa decidir.</summary>
    Pending,
    /// <summary>Desfeito por pessoa: nunca é religado automaticamente.</summary>
    Untouched
}

public readonly record struct IfoodLinkDecision(IfoodLinkAction Action, Guid? ProdutoId = null);

/// <summary>Regras puras do vínculo item iFood → produto do cardápio (decididas pelo dono do produto).</summary>
public static class IfoodItemLinkRules
{
    private static readonly Regex Whitespace = new("[ \t\r\n\f\v]+", RegexOptions.Compiled);

    /// <summary>Mesmo critério do SQL (<see cref="NameSql"/>): ignora caixa e espaços repetidos/nas pontas; o ACENTO continua diferenciando.</summary>
    public static string NormalizeName(string? name) => Whitespace.Replace(name ?? "", " ").Trim(' ').ToLowerInvariant();

    /// <summary>Expressão SQL equivalente a <see cref="NormalizeName"/> para uma coluna ou parâmetro texto.</summary>
    public static string NameSql(string expression) => $"lower(btrim(regexp_replace({expression}, '\\s+', ' ', 'g')))";

    /// <summary>
    /// Ordem da decisão: 1) vínculo confirmado pelo id do item; 2) desfeito por pessoa não religa; 3) sem vínculo, casa por nome
    /// só quando há exatamente um produto ativo com o mesmo nome (zero ou mais de um deixam pendente).
    /// </summary>
    public static IfoodLinkDecision Decide(string? existingState, Guid? existingProduto, string nomeIfood, IEnumerable<(Guid Id, string Nome)> activeProducts)
    {
        if (existingState == "confirmado" && existingProduto.HasValue) return new(IfoodLinkAction.UseExisting, existingProduto);
        if (existingState == "desfeito") return new(IfoodLinkAction.Untouched);
        var wanted = NormalizeName(nomeIfood);
        var matches = wanted.Length == 0 ? new List<Guid>()
            : activeProducts.Where(p => NormalizeName(p.Nome) == wanted).Select(p => p.Id).Distinct().ToList();
        return matches.Count == 1 ? new(IfoodLinkAction.AutoConfirm, matches[0]) : new(IfoodLinkAction.Pending);
    }
}

/// <summary>
/// Registra as chaves (<c>items[].id</c>) dos pedidos iFood importados e tenta ligá-las ao cardápio. Roda DEPOIS do commit da
/// importação, em transação própria e curta: nunca entra na transação crítica, e qualquer falha só vira log.
/// </summary>
public class IfoodItemLinkService
{
    private readonly NpgsqlDataSource _source;
    private readonly ILogger? _logger;
    public IfoodItemLinkService(NpgsqlDataSource source, ILogger<IfoodItemLinkService>? logger = null) => (_source, _logger) = (source, logger);

    /// <param name="countOrder">
    /// Verdadeiro quando o pedido acabou de ser criado. No reprocessamento (pedido que já existia) a chave é garantida, mas
    /// <c>pedidos_count</c> não sobe, para repetir o mesmo pedido/evento não inflar a contagem.
    /// </param>
    public virtual async Task RegisterAsync(Guid store, Guid merchant, IReadOnlyList<IfoodImportedItem> items, bool countOrder, CancellationToken ct = default)
    {
        try
        {
            var keys = items.Where(i => !string.IsNullOrWhiteSpace(i.ExternalItemId))
                .GroupBy(i => i.ExternalItemId!.Trim()).Select(g => (Key: g.Key, Item: g.First())).ToArray();
            if (keys.Length == 0) return;
            await using var connection = await _source.OpenConnectionAsync(ct);
            if (!await IfoodSchemaProbe.LinksAsync(connection, null)) return;
            await using var transaction = await connection.BeginTransactionAsync(ct);
            var rows = (await connection.QueryAsync<LinkRow>(new CommandDefinition("""
INSERT INTO ifood_produto_vinculo(estabelecimento_id,merchant_id,tipo,chave_id,external_code,nome_ifood,imagem_url,pedidos_count)
SELECT @Store,@Merchant,'item',k.chave,k.code,k.nome,k.imagem,1
FROM unnest(@Keys,@Codes,@Names,@Images) AS k(chave,code,nome,imagem)
ON CONFLICT(estabelecimento_id,merchant_id,tipo,chave_id) DO UPDATE
SET pedidos_count=ifood_produto_vinculo.pedidos_count+@Increment,visto_ultima_em=GREATEST(ifood_produto_vinculo.visto_ultima_em,NOW())
RETURNING id AS Id,estado AS Estado,produto_id AS ProdutoId,nome_ifood AS NomeIfood
""", new
            {
                Store = store, Merchant = merchant, Increment = countOrder ? 1 : 0,
                Keys = keys.Select(k => k.Key).ToArray(), Codes = keys.Select(k => k.Item.ExternalCode).ToArray(),
                Names = keys.Select(k => k.Item.Name).ToArray(), Images = keys.Select(k => k.Item.ImagemUrl).ToArray()
            }, transaction, cancellationToken: ct))).ToList();
            var pending = rows.Where(r => r.Estado == "pendente").ToList();
            if (pending.Count > 0)
            {
                var candidates = (await connection.QueryAsync<Candidate>(new CommandDefinition($"""
SELECT v.id AS VinculoId,p.id AS ProdutoId,p.nome AS Nome
FROM ifood_produto_vinculo v JOIN cardapio_produto p ON p.id_estabelecimento=v.estabelecimento_id AND p.deleted_at IS NULL
 AND {IfoodItemLinkRules.NameSql("p.nome")}={IfoodItemLinkRules.NameSql("v.nome_ifood")}
WHERE v.id=ANY(@Ids)
""", new { Ids = pending.Select(r => r.Id).ToArray() }, transaction, cancellationToken: ct))).ToLookup(c => c.VinculoId);
                foreach (var row in pending)
                {
                    var decision = IfoodItemLinkRules.Decide(row.Estado, row.ProdutoId, row.NomeIfood, candidates[row.Id].Select(c => (c.ProdutoId, c.Nome)));
                    if (decision.Action != IfoodLinkAction.AutoConfirm) continue;
                    await connection.ExecuteAsync(new CommandDefinition("""
UPDATE ifood_produto_vinculo SET estado='confirmado',origem='nome',produto_id=@Produto,confirmado_em=NOW()
WHERE id=@Id AND estado='pendente'
""", new { row.Id, Produto = decision.ProdutoId }, transaction, cancellationToken: ct));
                }
            }
            await transaction.CommitAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // O vínculo só enriquece a exibição: nada aqui pode derrubar importação, fila, detalhe ou entrega.
            _logger?.LogWarning("Vínculo de itens iFood não registrado: {Error}.", ex.GetType().Name);
        }
    }

    /// <summary>
    /// Ao criar ou renomear um produto, tenta casar os pendentes da mesma loja pelo nome (mesma regra e mesma unicidade). Chamado
    /// depois do commit do cardápio; falha (inclusive tabela ainda inexistente) é ignorada.
    /// </summary>
    public static async Task ReevaluatePendingAsync(NpgsqlConnection connection, Guid store, string productName, ILogger? logger = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(productName)) return;
        try
        {
            if (!await IfoodSchemaProbe.LinksAsync(connection, null)) return;
            await connection.ExecuteAsync(new CommandDefinition($"""
WITH alvo AS (
  SELECT (array_agg(p.id))[1] AS id FROM cardapio_produto p
  WHERE p.id_estabelecimento=@Store AND p.deleted_at IS NULL AND {IfoodItemLinkRules.NameSql("p.nome")}={IfoodItemLinkRules.NameSql("@Nome::text")}
  HAVING count(*)=1)
UPDATE ifood_produto_vinculo v SET estado='confirmado',origem='nome',produto_id=alvo.id,confirmado_em=NOW()
FROM alvo
WHERE v.estabelecimento_id=@Store AND v.tipo='item' AND v.estado='pendente'
 AND {IfoodItemLinkRules.NameSql("v.nome_ifood")}={IfoodItemLinkRules.NameSql("@Nome::text")}
""", new { Store = store, Nome = productName }, cancellationToken: ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { logger?.LogWarning("Reavaliação de vínculos iFood não aplicada: {Error}.", ex.GetType().Name); }
    }

    private sealed class LinkRow { public Guid Id { get; set; } public string Estado { get; set; } = ""; public Guid? ProdutoId { get; set; } public string NomeIfood { get; set; } = ""; }
    private sealed class Candidate { public Guid VinculoId { get; set; } public Guid ProdutoId { get; set; } public string Nome { get; set; } = ""; }
}
