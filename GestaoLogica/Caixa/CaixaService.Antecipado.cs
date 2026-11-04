using APIBack.Acontecimentos;
using APIBack.Security;
using APIBack.Model.Enum;
using Dapper;
using Npgsql;

namespace APIBack.GestaoLogica.Caixa;

// Recebimento antecipado: o cliente paga no Caixa um pedido que ainda está em operação (cozinha, retirada, entrega por motoboy ainda não
// despachada). Receber não é entregar: o pedido continua na fila e só o fluxo da loja o conclui. O dinheiro entra no livro na conclusão,
// como em todo pedido que nasce pago (o mesmo caminho do "Enviar pedido pago"): a venda lê status_pagamento e a forma gravados aqui.

public sealed partial class CaixaService
{
    private static readonly int[] StatusRecebiveisAntes = AntecipadoRegras.Recebiveis.ToArray();

    private sealed class PedidoParaReceber
    {
        public int? Status { get; set; }
        public string? StatusPagamento { get; set; }
        public string? TipoPagamento { get; set; }
        public string? Valor { get; set; }
        public int? Motoboy { get; set; }
        public bool Ifood { get; set; }
    }

    public async Task<CaixaDoDia> ReceberAntecipado(Guid loja, int usuario, GestaoPapeis.Acesso acesso, RecebimentoAntecipadoRequest req, CancellationToken ct)
    {
        if (!acesso.PodeLancar) throw new GestaoException("SEM_PERMISSAO", "Você não pode registrar no caixa.", 403);
        BalcaoRegras.Chave(req.ChaveIdempotencia);
        if (req.PedidoId <= 0) throw Invalido("Escolha o pedido.");
        var forma = req.Forma?.Trim() ?? "";
        var tipo = BalcaoRegras.TipoPagamento(forma);
        var recebido = BalcaoRegras.ValorRecebido(forma, req.ValorRecebido);
        var hoje = await SoHoje(loja, req.Dia, ct);

        await using var c = await dataSource.OpenConnectionAsync(ct);
        await DiaAberto(c, loja, hoje);
        await using var tx = await c.BeginTransactionAsync(ct);

        var temMotoboy = await c.ExecuteScalarAsync<bool>(@"SELECT EXISTS (SELECT 1 FROM information_schema.columns
 WHERE table_schema = current_schema() AND table_name = 'pedido' AND column_name = 'motoboy_responsavel')", transaction: tx);
        var temIfood = await c.ExecuteScalarAsync<bool>("SELECT to_regclass('ifood_order_links') IS NOT NULL", transaction: tx);
        var motoboy = temMotoboy ? "p.motoboy_responsavel" : "NULL::integer";
        var ifood = temIfood
            ? "EXISTS (SELECT 1 FROM ifood_order_links l WHERE l.pedido_id = p.id AND l.estabelecimento_id = p.id_estabelecimento)"
            : "FALSE";
        // Trava a linha: dois recebimentos simultâneos do mesmo pedido não passam juntos.
        var p = await c.QuerySingleOrDefaultAsync<PedidoParaReceber>($@"
SELECT p.status_pedido AS Status, p.status_pagamento::text AS StatusPagamento, p.tipo_pagamento::text AS TipoPagamento,
       p.value::text AS Valor, {motoboy} AS Motoboy, {ifood} AS Ifood
  FROM pedido p WHERE p.id = @id AND p.id_estabelecimento = @loja FOR UPDATE OF p", new { id = req.PedidoId, loja }, tx)
            ?? throw new GestaoException("NAO_ENCONTRADO", $"Pedido #{req.PedidoId} não encontrado.", 404);

        var erro = AntecipadoRegras.Barreira(p.Status, p.StatusPagamento, p.TipoPagamento, tipo, p.Motoboy.HasValue, p.Ifood);
        if (erro is { Replay: true }) { await tx.CommitAsync(ct); return (await CarregarDia(loja, hoje, ct))!; }
        if (erro is not null) throw new GestaoException(erro.Codigo, erro.Mensagem, 409);

        var total = PedidosEmAbertoQuery.Dinheiro(p.Valor);
        if (recebido is { } r && r < total) throw Invalido($"Valor recebido menor que o pedido (R$ {total:N2}).");

        var linhas = await c.ExecuteAsync(@"
UPDATE pedido SET status_pagamento = 'Pago', tipo_pagamento = @tipo, troco = @troco
 WHERE id = @id AND id_estabelecimento = @loja AND status_pedido = ANY(@abertos)
   AND NOT (lower(coalesce(status_pagamento, '')) IN ('pago', 'paid', 'aprovado', 'approved', 'pago_online', 'settled'))",
            new { id = req.PedidoId, loja, tipo, troco = recebido, abertos = StatusRecebiveisAntes }, tx);
        if (linhas != 1) throw new GestaoException("PEDIDO_MUDOU", "O pedido mudou enquanto você recebia. Atualize a tela.", 409);
        await tx.CommitAsync(ct);
        return (await CarregarDia(loja, hoje, ct))!;
    }
}

/// <summary>Regras puras do recebimento antecipado: quando o Caixa pode registrar o pagamento de um pedido ainda em operação.</summary>
public static class AntecipadoRegras
{
    /// <summary>Em operação e ainda não despachado: pendente, atribuído (a oferta ainda não saiu) ou aguardando motoboy. Em rota já é do motoboy.</summary>
    internal static readonly HashSet<int> Recebiveis = new() { (int)StatusPedido.Pendente, (int)StatusPedido.Atribuido, (int)StatusPedido.AguardandoMotoboy };

    public sealed record Barra(string Codigo, string Mensagem, bool Replay = false);

    /// <summary>
    /// Nulo = pode registrar. Replay = já está pago exatamente nesta forma (reenvio): responde sem repetir. Nunca cobra o que já está
    /// pago, nunca cobra do cliente o que o motoboy recebe, nunca toca iFood (recebível externo) e só serve pedido ainda em operação.
    /// </summary>
    public static Barra? Barreira(int? status, string? statusPagamento, string? tipoPagamento, string tipoPedido, bool comMotoboy, bool ifood)
    {
        if (ifood) return new("IFOOD", "Pedido do iFood: o app cobra e repassa. Não se recebe no Caixa.");
        var s = status ?? (int)StatusPedido.Pendente;
        if (!Recebiveis.Contains(s))
            return new("PEDIDO_NAO_ABERTO", s == (int)StatusPedido.Concluido
                ? "O pedido já foi entregue: receba em A receber." : "Só se recebe antes de entregar um pedido ainda em operação.");
        if (APIBack.Service.DeliveryCompletionRules.IsPaid(statusPagamento))
            return string.Equals(tipoPagamento?.Trim(), tipoPedido, StringComparison.OrdinalIgnoreCase)
                ? new("JA_PAGO", "Este pedido já está pago.", Replay: true)
                : new("JA_PAGO", "Este pedido já está pago: não cobre de novo.");
        if (comMotoboy) return new("COM_MOTOBOY", "O pedido já está com um motoboy: o dinheiro é recebido por ele e conferido no acerto.");
        return null;
    }
}
