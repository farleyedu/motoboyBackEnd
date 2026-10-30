using System.Globalization;
using APIBack.Model.Enum;
using Dapper;
using Npgsql;

namespace APIBack.GestaoLogica.Caixa;

/// <summary>
/// Pedidos que ainda estão em operação na loja (pendente, atribuído, em rota, aguardando motoboy). Só leitura: não publica venda,
/// não liquida recebível e não mexe em estoque. Concluído, cancelado, rascunho e encerrado automático não entram.
/// Pedido de dia anterior que segue ativo aparece com a data original em <c>Dia</c>; a competência não é alterada.
/// </summary>
public static class PedidosEmAbertoQuery
{
    private static readonly int[] StatusAbertos =
    {
        (int)StatusPedido.Pendente, (int)StatusPedido.Atribuido, (int)StatusPedido.EmRota, (int)StatusPedido.AguardandoMotoboy,
    };

    internal sealed class Linha
    {
        public int Id { get; set; }
        public int? Status { get; set; }
        public string? Valor { get; set; }
        public string? DataPedido { get; set; }
        public string? Origem { get; set; }
        public string? Canal { get; set; }
        public string? NumeroIfood { get; set; }
        public string? TipoIfood { get; set; }
        public decimal? TotalIfood { get; set; }
    }

    public static async Task<IReadOnlyList<PedidoEmAberto>> Carregar(NpgsqlConnection c, Guid loja, CancellationToken ct)
    {
        var colunas = (await c.QueryAsync<string>(new CommandDefinition(@"
SELECT column_name::text FROM information_schema.columns
 WHERE table_schema = current_schema() AND table_name = 'pedido' AND column_name IN ('origem', 'canal')", cancellationToken: ct))).ToHashSet();
        var temIfood = await c.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT to_regclass('ifood_order_links') IS NOT NULL AND to_regclass('ifood_order_snapshots') IS NOT NULL", cancellationToken: ct));

        var origem = colunas.Contains("origem") ? "p.origem::text" : "NULL::text";
        var canal = colunas.Contains("canal") ? "p.canal::text" : "NULL::text";
        var ifood = temIfood
            ? @"l.display_number::text AS NumeroIfood, s.order_type::text AS TipoIfood, s.order_amount AS TotalIfood"
            : "NULL::text AS NumeroIfood, NULL::text AS TipoIfood, NULL::numeric AS TotalIfood";
        var juncoes = temIfood
            ? @"LEFT JOIN ifood_order_links l ON l.pedido_id = p.id AND l.estabelecimento_id = p.id_estabelecimento
  LEFT JOIN ifood_order_snapshots s ON s.merchant_id = l.merchant_id AND s.external_order_id = l.external_order_id
                                   AND s.estabelecimento_id = l.estabelecimento_id"
            : "";

        var linhas = await c.QueryAsync<Linha>(new CommandDefinition($@"
SELECT p.id AS Id, p.status_pedido AS Status, p.value::text AS Valor, p.data_pedido::text AS DataPedido,
       {origem} AS Origem, {canal} AS Canal, {ifood}
  FROM pedido p
  {juncoes}
 WHERE p.id_estabelecimento = @loja AND COALESCE(p.status_pedido, 1) = ANY(@status)
 ORDER BY p.id", new { loja, status = StatusAbertos }, cancellationToken: ct));

        return linhas.Select(Traduzir).ToList();
    }

    private static PedidoEmAberto Traduzir(Linha l)
    {
        var ifood = !string.IsNullOrWhiteSpace(l.NumeroIfood) || l.Origem == "ifood";
        var numero = !string.IsNullOrWhiteSpace(l.NumeroIfood) ? $"#{l.NumeroIfood!.Trim()}" : $"#{l.Id}";
        // iFood: total oficial atual do pedido (não o saldo a receber). Demais: valor gravado no pedido.
        var valor = ifood && l.TotalIfood is { } oficial ? oficial : Dinheiro(l.Valor);
        return new PedidoEmAberto(numero, Onde(l, ifood), CaixaRegras.R2(valor), StatusPedidoExtensions.ToApiKey(l.Status), DiaOriginal(l.DataPedido));
    }

    internal static string Onde(Linha l, bool ifood)
    {
        if (ifood)
            return l.TipoIfood?.Trim().ToUpperInvariant() switch
            {
                "DELIVERY" => "iFood · entrega", "TAKEOUT" => "iFood · retirada", "INDOOR" => "iFood · no local", _ => "iFood",
            };
        var chave = (string.IsNullOrWhiteSpace(l.Canal) ? l.Origem : l.Canal)?.Trim().ToLowerInvariant();
        return chave switch
        {
            null or "" => "Pedido", "whatsapp" or "bot" => "WhatsApp", "app" => "App", "balcao" or "balcão" => "Balcão",
            "cardapio" => "Cardápio web", "atendente" or "manual" => "Atendente", "simulador" => "Simulador",
            _ => char.ToUpperInvariant(chave[0]) + chave[1..],
        };
    }

    internal static decimal Dinheiro(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return 0;
        var t = texto.Trim().Replace("R$", "").Trim();
        // "1.234,50" (pt-BR) ou "1234.50" (banco).
        if (t.Contains(',')) t = t.Replace(".", "").Replace(',', '.');
        return decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) && v >= 0 ? v : 0;
    }

    /// <summary>Coluna legada: "AAAA-MM-DD…" ou "dd/MM/AAAA". Devolve AAAA-MM-DD, ou null se não der para ler.</summary>
    internal static string? DiaOriginal(string? bruto)
    {
        if (string.IsNullOrWhiteSpace(bruto)) return null;
        var t = bruto.Trim();
        if (t.Length >= 10 && DateOnly.TryParseExact(t[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var iso))
            return iso.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (t.Length >= 10 && DateOnly.TryParseExact(t[..10], "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var br))
            return br.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return null;
    }
}
