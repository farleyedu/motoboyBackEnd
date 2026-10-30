using APIBack.GestaoLogica.Estoque;
using Dapper;
using Npgsql;

namespace APIBack.GestaoLogica.Vendas;

/// <summary>Ficha técnica lida do PostgreSQL (gestao_ficha_componente). Produto sem componente fica fora do resultado ("sem ficha").</summary>
public sealed class FichaTecnicaLeitorPostgres(NpgsqlDataSource dataSource) : IFichaTecnicaLeitor
{
    private sealed class Linha
    {
        public Guid ProdutoId { get; set; }
        public Guid InsumoId { get; set; }
        public string Nome { get; set; } = "";
        public decimal Quantidade { get; set; }
        public string Unidade { get; set; } = "";
        public string[]? Modalidades { get; set; }
    }

    public async Task<IReadOnlyDictionary<Guid, FichaTecnica>> Ler(Guid loja, IReadOnlyCollection<Guid> produtoIds, CancellationToken ct)
    {
        var vazio = new Dictionary<Guid, FichaTecnica>();
        if (produtoIds.Count == 0) return vazio;
        await using var c = await dataSource.OpenConnectionAsync(ct);
        if (!await c.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT to_regclass('gestao_ficha_componente') IS NOT NULL", cancellationToken: ct))) return vazio;
        var linhas = await c.QueryAsync<Linha>(new CommandDefinition("""
SELECT f.produto_id AS ProdutoId, f.insumo_id AS InsumoId, i.nome AS Nome, f.quantidade AS Quantidade, f.unidade AS Unidade, f.modalidades AS Modalidades
  FROM gestao_ficha_componente f
  JOIN insumo i ON i.estabelecimento_id = f.estabelecimento_id AND i.id = f.insumo_id
 WHERE f.estabelecimento_id = @loja AND f.produto_id = ANY(@produtoIds)
 ORDER BY f.produto_id, f.ordem, f.id
""", new { loja, produtoIds = produtoIds.ToArray() }, cancellationToken: ct));
        return linhas.GroupBy(l => l.ProdutoId).ToDictionary(g => g.Key, g => new FichaTecnica(g.Key,
            g.Select(l => new ComponenteFicha(l.InsumoId, l.Nome, l.Quantidade, l.Unidade, l.Modalidades)).ToList()));
    }
}
