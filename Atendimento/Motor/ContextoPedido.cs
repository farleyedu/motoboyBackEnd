using APIBack.Service;
using Dapper;
using Npgsql;

namespace APIBack.Atendimento.Motor;

public sealed record PedidoDoAtendimento(string Chave, string Numero, string Status, bool Ativo,
    string? Previsao = null, string? Link = null);

public interface IContextoPedidoAtendimento
{
    Task<IReadOnlyList<PedidoDoAtendimento>> ObterAsync(Guid loja, Guid canal, Guid conversa);
}

/// <summary>Le somente pedidos vinculados ao cliente e ao numero da conversa atual.</summary>
public sealed class ContextoPedidoAtendimento(NpgsqlDataSource dataSource, IConfiguration configuration) : IContextoPedidoAtendimento
{
    private sealed record Linha(int Id, int Status, string? Data, string? Previsao, string? Token);

    public async Task<IReadOnlyList<PedidoDoAtendimento>> ObterAsync(Guid loja, Guid canal, Guid conversa)
    {
        if (conversa == Guid.Empty) return Array.Empty<PedidoDoAtendimento>();
        await using var cx = await dataSource.OpenConnectionAsync();
        var linhas = await cx.QueryAsync<Linha>(@"
SELECT p.id AS Id, p.status_pedido AS Status, p.data_pedido::text AS Data,
       p.previsao_entrega::text AS Previsao,
       CASE WHEN p.status_pedido = 2 AND COALESCE(p.rastreio_opt_in, FALSE)
            AND t.invalidado_em IS NULL AND t.expira_em > NOW() THEN t.token END AS Token
FROM pedido p JOIN conversas anterior ON anterior.id = p.conversa_id
JOIN conversas atual ON atual.id = @Conversa AND atual.id_estabelecimento = @Loja AND atual.id_canal = @Canal
LEFT JOIN pedido_rastreio_token t ON t.pedido_id = p.id
WHERE p.id_estabelecimento = @Loja AND anterior.id_estabelecimento = @Loja
  AND anterior.id_cliente = atual.id_cliente AND anterior.id_canal = atual.id_canal
  AND p.status_pedido IN (1, 2, 3, 4, 5, 7, 8)
  AND COALESCE(to_jsonb(p)->>'origem', '') <> 'ifood' AND p.id_ifood IS NULL
ORDER BY CASE WHEN p.status_pedido IN (1,2,5,8) THEN 0 ELSE 1 END, p.id DESC LIMIT 20;",
            new { Conversa = conversa, Loja = loja, Canal = canal });
        var baseUrl = configuration["Rastreio:PublicBaseUrl"]?.TrimEnd('/');
        var urlValida = Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
        var resultado = new List<PedidoDoAtendimento>();
        foreach (var p in linhas)
        {
            var ativo = p.Status is 1 or 2 or 5 or 8;
            var data = DeliveryRules.ParseStoredDateTime(p.Data);
            var agora = AtendimentoConfigRules.ParaHorarioLocal(DateTime.UtcNow);
            if (!ativo && (!data.HasValue || data.Value < agora.AddHours(-24) || data.Value > agora)) continue;
            var status = p.Status switch
            {
                1 => "recebido pela loja", 2 => "em rota de entrega", 3 => "entregue",
                4 => "cancelado", 5 => "atribuído ao motoboy", 8 => "aguardando o motoboy aceitar",
                7 => "encerrado pela loja", _ => "aguardando atualização"
            };
            var previsao = ativo ? DeliveryRules.ParseStoredDateTime(p.Previsao) : null;
            resultado.Add(new(p.Id.ToString(), p.Id.ToString(), status, ativo,
                previsao?.ToString("HH:mm"), urlValida && p.Token != null ? $"{baseUrl}/rastreio/{p.Token}" : null));
        }
        // Pedido do site ainda nao aceito nao existe na tabela operacional.
        var web = await cx.QueryAsync<(Guid Id, string Codigo)>(@"
SELECT w.id AS Id, w.codigo AS Codigo FROM cardapio_pedido_publico w
JOIN conversas anterior ON anterior.id = w.id_conversa
JOIN conversas atual ON atual.id = @Conversa AND atual.id_estabelecimento = @Loja AND atual.id_canal = @Canal
WHERE w.id_estabelecimento = @Loja AND anterior.id_estabelecimento = @Loja
  AND anterior.id_cliente = atual.id_cliente AND anterior.id_canal = atual.id_canal
  AND w.status = 'aguardando_aceite' ORDER BY w.created_at DESC LIMIT 20;",
            new { Conversa = conversa, Loja = loja, Canal = canal });
        resultado.AddRange(web.Select(p => new PedidoDoAtendimento("web:" + p.Id, p.Codigo, "aguardando a loja aceitar", true)));
        return resultado;
    }
}
