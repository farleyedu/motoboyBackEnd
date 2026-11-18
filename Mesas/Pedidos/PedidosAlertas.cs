using System.Text.Json.Nodes;
using APIBack.Mesas.Sessao;
using Dapper;
using Npgsql;
using R = APIBack.Mesas.Pedidos.PedidosRegras;

namespace APIBack.Mesas.Pedidos;

/// <summary>Alertas do salão vindos dos pedidos: <c>pedido_pronto</c>, <c>atraso_cozinha</c> (acima de 1,5× o tempo do prato) e <c>aprovacao_pendente</c>.</summary>
public sealed class PedidosAlertaFonte : IMesasAlertaFonte
{
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<string>>> AlertasAsync(NpgsqlConnection c, Guid loja, IReadOnlyCollection<Guid> sessaoIds,
        JsonObject config, CancellationToken ct)
    {
        var resultado = new Dictionary<Guid, IReadOnlyList<string>>();
        if (sessaoIds.Count == 0) return resultado;
        var linhas = await c.QueryAsync<(Guid SessaoId, string Estado, string EstacaoId, DateTime Desde, DateTime Agora)>(new CommandDefinition("""
            SELECT sessao_id, estado, estacao_id, COALESCE(t_fila, criado_em) AS desde, NOW() AS agora FROM mesa_item
             WHERE estabelecimento_id = @loja AND sessao_id = ANY(@ids) AND estado IN ('aguardando_aprovacao', 'em_fila', 'em_preparo', 'pronto')
            """, new { loja, ids = sessaoIds.ToArray() }, cancellationToken: ct));
        foreach (var g in linhas.GroupBy(l => l.SessaoId))
        {
            var lista = new List<string>();
            if (g.Any(l => l.Estado == "pronto")) lista.Add("pedido_pronto");
            if (g.Any(l => l.Estado is "em_fila" or "em_preparo" && R.Atrasado((int)(l.Agora - l.Desde).TotalMinutes, R.TempoEsperado(config, l.EstacaoId))))
                lista.Add("atraso_cozinha");
            if (g.Any(l => l.Estado == "aguardando_aprovacao")) lista.Add("aprovacao_pendente");
            if (lista.Count > 0) resultado[g.Key] = lista;
        }
        return resultado;
    }
}
