using System;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Payments.MercadoPago;
using APIBack.Service;
using Dapper;
using Npgsql;

namespace APIBack.Repository
{
    /// <summary>
    /// SQL do pagamento online do cardapio que precisa rodar DENTRO de uma transacao alheia: a do sincronizador do Mercado Pago
    /// (webhook, conciliador, criacao) e a das decisoes do pre-pedido (recusa, cancelamento). A linha do pre-pedido e sempre
    /// travada antes, entao aceite, recusa, cancelamento e confirmacao do pagamento nunca se cruzam.
    /// </summary>
    public static class CardapioPagamentoSql
    {
        private sealed class PrePedidoTravado
        {
            public Guid Id { get; set; }
            public string Status { get; set; } = string.Empty;
            public bool PagamentoOnline { get; set; }
            public Guid? IdIntentPago { get; set; }
        }

        /// <summary>
        /// Efeito de uma mudanca de intencao do cardapio (roda na transacao do sincronizador, com a intencao travada):
        /// 1) primeira intencao paga vira "a" intencao do pedido e o pedido online confirmado vai para aguardando_aceite;
        /// 2) dinheiro que entra num pedido ja recusado/cancelado, ou um segundo pagamento, vira reembolso automatico;
        /// 3) o espelho status_pagamento segue so a intencao que importa (a paga ou, antes disso, a mais recente);
        /// 4) reembolso cujo estorno o Mercado Pago confirmou passa a reembolsado.
        /// </summary>
        public static async Task AplicarEfeitoAsync(NpgsqlConnection cx, NpgsqlTransaction tx, PagamentoIntent intent, CancellationToken ct)
        {
            if (intent.PrePedidoId == null) return;
            var pre = await cx.QueryFirstOrDefaultAsync<PrePedidoTravado>(new CommandDefinition(@"
SELECT id, status, pagamento_online, id_intent_pago
  FROM cardapio_pedido_publico WHERE id = @Id AND id_estabelecimento = @Loja FOR UPDATE",
                new { Id = intent.PrePedidoId, Loja = intent.IdEstabelecimento }, tx, cancellationToken: ct));
            if (pre == null) return;

            var paga = StatusIntent.FamiliaPaga(intent.Status);
            var saldo = intent.ValorPago - intent.ValorEstornado;
            var ehAPaga = pre.IdIntentPago == intent.Id;

            if (paga && pre.IdIntentPago == null)
            {
                var metodo = await cx.ExecuteScalarAsync<string?>(new CommandDefinition(
                    "SELECT metodo FROM pagamento WHERE id_intent = @Id ORDER BY created_at DESC LIMIT 1", new { intent.Id }, tx, cancellationToken: ct));
                // So o pedido online confirmado e ainda sem decisao anda: recusado/cancelado fica onde esta (e o dinheiro volta abaixo).
                await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE cardapio_pedido_publico
   SET id_intent_pago = @Intent, id_intent_atual = @Intent, pago_em = COALESCE(pago_em, NOW()),
       metodo_online = CASE WHEN pagamento_online AND @Metodo IN ('pix', 'cartao') THEN @Metodo ELSE metodo_online END,
       confirmado_em = CASE WHEN status = 'aguardando_pagamento' THEN NOW() ELSE confirmado_em END,
       status = CASE WHEN status = 'aguardando_pagamento' THEN 'aguardando_aceite' ELSE status END,
       updated_at = NOW()
 WHERE id = @Id", new { Intent = intent.Id, Metodo = metodo, pre.Id }, tx, cancellationToken: ct));
                ehAPaga = true;

                if ((pre.Status is CardapioPedidoStatus.Recusado or CardapioPedidoStatus.Cancelado || !pre.PagamentoOnline)
                    && intent.Status == StatusIntent.Pago && saldo > 0)
                {
                    await RegistrarReembolsoAsync(cx, tx, intent.IdEstabelecimento, pre.Id, intent.Id, CardapioReembolsoTipo.Automatico,
                        CardapioReembolsoMotivo.PagoAposEncerramento, saldo, CardapioReembolsoStatus.Pendente, null, null, ct);
                }
            }
            else if (paga && !ehAPaga && intent.Status == StatusIntent.Pago && saldo > 0)
            {
                // O pedido ja tinha sido pago por outra intencao: este dinheiro e devolvido inteiro.
                await RegistrarReembolsoAsync(cx, tx, intent.IdEstabelecimento, pre.Id, intent.Id, CardapioReembolsoTipo.Automatico,
                    CardapioReembolsoMotivo.PagamentoDuplicado, saldo, CardapioReembolsoStatus.Pendente, null, null, ct);
            }

            var espelha = ehAPaga;
            if (!espelha && pre.IdIntentPago == null)
            {
                var ultima = await cx.ExecuteScalarAsync<Guid?>(new CommandDefinition(@"
SELECT id FROM pagamento_intent WHERE pre_pedido_id = @Pre AND origem = 'cardapio' ORDER BY created_at DESC, id DESC LIMIT 1",
                    new { Pre = pre.Id }, tx, cancellationToken: ct));
                espelha = ultima == intent.Id;
            }
            if (espelha)
            {
                await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE cardapio_pedido_publico
   SET status_pagamento = @S, id_intent_atual = @Intent, updated_at = NOW()
 WHERE id = @Id AND (status_pagamento IS DISTINCT FROM @S OR id_intent_atual IS DISTINCT FROM @Intent)",
                    new { S = PagamentoStatusRules.StatusPagamentoPrePedido(intent.Status), Intent = intent.Id, pre.Id }, tx, cancellationToken: ct));
            }

            if (intent.ValorEstornado > 0) await ConcluirReembolsosAsync(cx, tx, intent.Id, ct);
        }

        /// <summary>Reembolso cujo estorno o Mercado Pago ja confirmou (pagamento_estorno concluido) passa a reembolsado.</summary>
        public static Task ConcluirReembolsosAsync(NpgsqlConnection cx, NpgsqlTransaction? tx, Guid intentId, CancellationToken ct) =>
            cx.ExecuteAsync(new CommandDefinition(@"
UPDATE cardapio_pedido_reembolso r
   SET status = 'reembolsado', concluido_em = COALESCE(r.concluido_em, NOW()), updated_at = NOW()
  FROM pagamento_estorno e
 WHERE r.id_intent = @Intent AND r.id_estorno = e.id AND e.status = 'concluido'
   AND r.status IN ('reembolso_pendente', 'reembolso_em_processamento')", new { Intent = intentId }, tx, cancellationToken: ct));

        /// <summary>Insere o reembolso (a chave repete a decisao: a mesma recusa clicada duas vezes nao cria outro). Devolve o id ou null se ja existia.</summary>
        public static Task<Guid?> RegistrarReembolsoAsync(NpgsqlConnection cx, NpgsqlTransaction tx, Guid loja, Guid prePedidoId, Guid intentId,
            string tipo, string motivo, decimal valor, string status, string? justificativa, int? solicitadoPor, CancellationToken ct,
            string? chave = null) =>
            cx.ExecuteScalarAsync<Guid?>(new CommandDefinition(@"
INSERT INTO cardapio_pedido_reembolso (id, id_estabelecimento, pre_pedido_id, id_intent, tipo, motivo, valor, justificativa, status,
                                       solicitado_por, idempotency_key)
VALUES (@Id, @Loja, @Pre, @Intent, @Tipo, @Motivo, @Valor, @Justificativa, @Status, @Por, @Chave)
ON CONFLICT (idempotency_key) DO NOTHING
RETURNING id", new
            {
                Id = Guid.NewGuid(), Loja = loja, Pre = prePedidoId, Intent = intentId, Tipo = tipo, Motivo = motivo, Valor = valor,
                Justificativa = justificativa, Status = status, Por = solicitadoPor,
                Chave = chave ?? CardapioPagamentoRules.ChaveReembolso(prePedidoId, intentId, motivo)
            }, tx, cancellationToken: ct));
    }
}
