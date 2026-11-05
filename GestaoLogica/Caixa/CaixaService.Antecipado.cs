using APIBack.Acontecimentos;
using APIBack.Security;
using APIBack.Model.Enum;
using Dapper;
using Npgsql;
using System.Text.Json;
using System.Globalization;

namespace APIBack.GestaoLogica.Caixa;

// Recebimento antecipado: o cliente paga no Caixa um pedido que ainda está em operação (cozinha, retirada, entrega por motoboy ainda não
// despachada). Receber não é entregar: o pedido continua na fila e só o fluxo da loja o conclui. O dinheiro entra no livro no ato, como
// adiantamento de cliente: uma transferência de "Adiantamentos de clientes" para a gaveta ou a maquininha. Por isso o fechamento já espera
// esse valor, sem faturar nada. A venda nasce na conclusão, tirando o valor dos adiantamentos; o cancelamento devolve (estorno).

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
        await GarantirContaAdiantamentos(c, loja, usuario, ct);
        var destino = await DestinoDoAdiantamento(c, loja, forma, ct);
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
        if (total <= 0) throw Invalido("Este pedido está sem valor: confira antes de receber.");
        if (recebido is { } r && r < total) throw Invalido($"Valor recebido menor que o pedido (R$ {total:N2}).");

        var linhas = await c.ExecuteAsync(@"
UPDATE pedido SET status_pagamento = 'Pago', tipo_pagamento = @tipo, troco = @troco
 WHERE id = @id AND id_estabelecimento = @loja AND status_pedido = ANY(@abertos)
   AND NOT (lower(coalesce(status_pagamento, '')) IN ('pago', 'paid', 'aprovado', 'approved', 'pago_online', 'settled'))",
            new { id = req.PedidoId, loja, tipo, troco = recebido, abertos = StatusRecebiveisAntes }, tx);
        if (linhas != 1) throw new GestaoException("PEDIDO_MUDOU", "O pedido mudou enquanto você recebia. Atualize a tela.", 409);
        await PublicarAdiantamento(c, tx, loja, usuario, req.PedidoId, forma, total, destino, hoje, ct);
        await tx.CommitAsync(ct);
        return (await CarregarDia(loja, hoje, ct))!;
    }

    /// <summary>
    /// Venda de balcão que segue o fluxo já paga ("Enviar pedido pago"): mesma regra do recebimento antecipado. Idempotente: repetir a venda
    /// com a mesma chave não lança de novo.
    /// </summary>
    public async Task RegistrarAdiantamentoDoBalcao(Guid loja, int usuario, int pedidoId, string forma, decimal total, DateOnly hoje, CancellationToken ct)
    {
        if (total <= 0) return;
        await using var c = await dataSource.OpenConnectionAsync(ct);
        await GarantirContaAdiantamentos(c, loja, usuario, ct);
        var destino = await DestinoDoAdiantamento(c, loja, forma, ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        var jaTem = await c.ExecuteScalarAsync<bool>(@"SELECT EXISTS (SELECT 1 FROM gestao_acontecimentos a
 WHERE a.estabelecimento_id = @loja AND a.origem_tipo = 'pedido' AND a.origem_id = @id AND a.corrige_id IS NULL AND a.tipo IN (@tipo, 'venda'))",
            new { loja, id = pedidoId.ToString(CultureInfo.InvariantCulture), tipo = APIBack.GestaoLogica.Vendas.VendaDoPedido.TipoAdiantamento }, tx);
        if (!jaTem) await PublicarAdiantamento(c, tx, loja, usuario, pedidoId, forma, total, destino, hoje, ct);
        await tx.CommitAsync(ct);
    }

    private async Task PublicarAdiantamento(NpgsqlConnection c, NpgsqlTransaction tx, Guid loja, int usuario, int pedidoId, string forma, decimal total,
        Guid destino, DateOnly hoje, CancellationToken ct)
    {
        var conta = await ContaRecebivel(c, tx, loja, APIBack.GestaoLogica.Vendas.VendaDoPedido.SubtipoAdiantamentos)
            ?? throw new GestaoException("SEM_CONTA", "A conta de adiantamentos não existe: aplique a migração do Caixa.", 409);
        var descricao = $"Pedido #{pedidoId} pago antes da entrega · {forma}";
        await livro.Publicar(c, tx, loja, usuario, new PublicarAcontecimentoRequest
        {
            ChaveIdempotencia = APIBack.GestaoLogica.Vendas.VendaDoPedido.ChaveAdiantamento(pedidoId),
            Tipo = APIBack.GestaoLogica.Vendas.VendaDoPedido.TipoAdiantamento, Modulo = ModuloGestao.Caixa, DataFato = _relogio.GetUtcNow(),
            Descricao = descricao, Origem = new OrigemRef { Tipo = "pedido", Id = pedidoId.ToString(CultureInfo.InvariantCulture) },
            Dados = JsonSerializer.SerializeToElement(new { forma, valor = total }),
            Lancamentos = Transferencia(conta, destino, total, hoje, descricao),
        }, ct);
    }

    /// <summary>Dinheiro vai para a gaveta; Pix e cartão, para a maquininha. Sem maquininha cadastrada, recusa antes de marcar o pedido como pago.</summary>
    private async Task<Guid> DestinoDoAdiantamento(NpgsqlConnection c, Guid loja, string forma, CancellationToken ct) =>
        forma == FormaCaixa.Dinheiro ? (await ContasDoCaixa(c, loja, null, ct)).Gaveta
            : await ContaRecebivel(c, null, loja, "maquininha")
              ?? throw new GestaoException("SEM_CONTA", "Cadastre a maquininha no Financeiro antes de receber em Pix ou cartão.", 409);

    private static Task<Guid?> ContaRecebivel(NpgsqlConnection c, NpgsqlTransaction? tx, Guid loja, string subtipo) =>
        c.ExecuteScalarAsync<Guid?>(@"SELECT id FROM gestao_contas
 WHERE estabelecimento_id = @loja AND tipo = 'recebivel' AND subtipo = @subtipo AND ativo ORDER BY criado_em, id LIMIT 1", new { loja, subtipo }, tx);

    private async Task GarantirContaAdiantamentos(NpgsqlConnection c, Guid loja, int usuario, CancellationToken ct)
    {
        var subtipo = APIBack.GestaoLogica.Vendas.VendaDoPedido.SubtipoAdiantamentos;
        if (await ContaRecebivel(c, null, loja, subtipo) is not null) return;
        await livro.CriarConta(loja, usuario, new ContaRequest
        {
            ChaveIdempotencia = "caixa:adiantamentos", Tipo = TipoConta.Recebivel, Subtipo = subtipo, Nome = "Adiantamentos de clientes",
        }, ct);
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
        if (APIBack.Service.DeliveryCompletionRules.IsPaid(statusPagamento) || APIBack.GestaoLogica.Vendas.VendaRegras.PagoOnline(tipoPagamento))
            return string.Equals(tipoPagamento?.Trim(), tipoPedido, StringComparison.OrdinalIgnoreCase)
                ? new("JA_PAGO", "Este pedido já está pago.", Replay: true)
                : new("JA_PAGO", "Este pedido já está pago: não cobre de novo.");
        if (comMotoboy) return new("COM_MOTOBOY", "O pedido já está com um motoboy: o dinheiro é recebido por ele e conferido no acerto.");
        return null;
    }
}
