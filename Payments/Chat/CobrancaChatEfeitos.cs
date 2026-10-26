using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Hubs;
using APIBack.Payments.MercadoPago;
using Dapper;
using Npgsql;

namespace APIBack.Payments.Chat
{
    /// <summary>
    /// Segundo efeito da origem chat (depois do ChatCobrancaEfeito do nucleo, que ja moveu o status da cobranca): reflete o dinheiro no
    /// pedido vinculado e agenda o que sai da transacao (avisar o cliente, avancar o pedido, cancelar a reemitida). Roda DENTRO da
    /// transacao do sincronizador, so quando a intencao mudou: webhook duplicado nao repete nada.
    /// </summary>
    public sealed class ChatCobrancaPedidoEfeito : IPagamentoEfeitoHandler
    {
        public string Origem => OrigensPagamento.Chat;

        public async Task<Guid?> AplicarAsync(NpgsqlConnection cx, NpgsqlTransaction tx, PagamentoIntent intent, string statusAnterior, string ator, CancellationToken ct)
        {
            var c = await CobrancaChatRepositorio.PorIntentAsync(cx, tx, intent.Id, intent.IdEstabelecimento, ct);
            if (c == null) return null;
            await CobrancaChatEfeitos.AposMudancaAsync(cx, tx, c, intent, statusAnterior, ct);
            return c.Id;
        }
    }

    public static class CobrancaChatEfeitos
    {
        /// <summary>
        /// Depois de a intencao mudar. <paramref name="statusAnteriorIntent"/> decide se o dinheiro acabou de entrar (uma vez so).
        /// Tambem chamado quando a cobranca e ligada a uma intencao que ja estava paga (o webhook chegou antes do vinculo).
        /// </summary>
        public static async Task AposMudancaAsync(NpgsqlConnection cx, NpgsqlTransaction tx, CobrancaChatLinha c, PagamentoIntent intent, string statusAnteriorIntent,
            CancellationToken ct)
        {
            var pagouAgora = StatusIntent.FamiliaPaga(intent.Status) && !StatusIntent.FamiliaPaga(statusAnteriorIntent);
            var estorno = intent.Status is StatusIntent.EstornadoParcial or StatusIntent.Estornado;

            if (c.PrePedidoId.HasValue)
                await PrePedidoAsync(cx, tx, c, intent, ct);

            if (pagouAgora)
            {
                var tardio = statusAnteriorIntent is StatusIntent.Expirado or StatusIntent.Cancelado;
                await PagamentoAuditoria.RegistrarAsync(cx, tx, c.IdEstabelecimento, "cobranca_chat", c.Id.ToString(), tardio ? "paga_tardia" : "paga", "sistema", null,
                    new { intentId = intent.Id, valor = intent.ValorPago, c.PedidoId, c.PrePedidoId, c.ConversaId, autor = c.AutorUsuarioId }, ct);
                // Pago atrasado (Pix vencido/cancelado que entrou mesmo assim): a cobranca reemitida no lugar dele nao pode ser paga tambem.
                if (await TemSucessoraAbertaAsync(cx, tx, c.Id, ct))
                    await CobrancaChatRepositorio.AgendarTarefaAsync(cx, tx, c.Id, c.IdEstabelecimento, TiposTarefa.CancelarSucessora, ct);

                var config = await CobrancaChatRepositorio.ConfigAsync(cx, c.IdEstabelecimento, ct);
                if (config.ConfirmarPagamentoCliente)
                    await CobrancaChatRepositorio.AgendarTarefaAsync(cx, tx, c.Id, c.IdEstabelecimento, TiposTarefa.ConfirmarCliente, ct);
            }

            // Avulsa nunca mexe em pedido.
            if (c.Tipo == TiposCobranca.Vinculada && (pagouAgora || estorno))
            {
                var pedidoId = c.PedidoId ?? await cx.ExecuteScalarAsync<int?>(new CommandDefinition(
                    "SELECT id_pedido FROM cardapio_pedido_publico WHERE id = @Id AND id_estabelecimento = @Loja", new { Id = c.PrePedidoId, Loja = c.IdEstabelecimento },
                    tx, cancellationToken: ct));
                if (pedidoId.HasValue) await RecalcularPedidoAsync(cx, tx, c, pedidoId.Value, pagouAgora, ct);
            }
        }

        private static Task<bool> TemSucessoraAbertaAsync(NpgsqlConnection cx, NpgsqlTransaction tx, Guid id, CancellationToken ct) =>
            cx.ExecuteScalarAsync<bool>(new CommandDefinition(@"
WITH RECURSIVE cadeia AS (
    SELECT id, status FROM cobranca_chat WHERE reemitida_de = @Id
    UNION ALL
    SELECT c.id, c.status FROM cobranca_chat c JOIN cadeia ON c.reemitida_de = cadeia.id)
SELECT EXISTS (SELECT 1 FROM cadeia WHERE status IN ('aguardando_aprovacao', 'criando', 'aguardando', 'enviado'))", new { Id = id }, tx, cancellationToken: ct));

        /// <summary>
        /// Financeiro do pedido a partir do que entrou online: quitado vira "Pago" (e avisa o painel); estornado a ponto de deixar saldo volta
        /// a "Pendente". Pagou alem do total (duas cobrancas pagas) fica registrado para o gerente reembolsar.
        /// </summary>
        public static async Task RecalcularPedidoAsync(NpgsqlConnection cx, NpgsqlTransaction tx, CobrancaChatLinha c, int pedidoId, bool pagouAgora, CancellationToken ct)
        {
            var alvo = await CobrancaChatRepositorio.PedidoAsync(cx, tx, pedidoId, travar: true, ct);
            if (alvo == null || alvo.IdEstabelecimento != c.IdEstabelecimento || alvo.Total is not > 0) return;

            var quitado = alvo.Recebido >= alvo.Total.Value;
            var jaPago = CobrancaChatRegras.StatusPago(alvo.StatusPagamento);
            string? acao = null;
            if (quitado && !jaPago)
            {
                await cx.ExecuteAsync(new CommandDefinition(
                    "UPDATE pedido SET status_pagamento = 'Pago', tipo_pagamento = 'PIX' WHERE id = @Id AND id_estabelecimento = @Loja",
                    new { Id = pedidoId, Loja = c.IdEstabelecimento }, tx, cancellationToken: ct));
                acao = "pagamento_confirmado";
                // Pedido esperando o pagamento (rascunho): segue para o proximo estagio pela regra de aceite da loja, fora da transacao.
                if (alvo.Status == "6")
                    await CobrancaChatRepositorio.AgendarTarefaAsync(cx, tx, c.Id, c.IdEstabelecimento, TiposTarefa.AvancarPedido, ct);
            }
            else if (!quitado && jaPago && !pagouAgora)
            {
                await cx.ExecuteAsync(new CommandDefinition(
                    "UPDATE pedido SET status_pagamento = 'Pendente' WHERE id = @Id AND id_estabelecimento = @Loja",
                    new { Id = pedidoId, Loja = c.IdEstabelecimento }, tx, cancellationToken: ct));
                acao = "pagamento_estornado";
            }

            var excedente = alvo.Recebido - alvo.Total.Value;
            await PagamentoAuditoria.RegistrarAsync(cx, tx, c.IdEstabelecimento, "pedido", pedidoId.ToString(),
                acao ?? (pagouAgora ? (excedente > 0 ? "pagamento_excedente" : "pagamento_parcial") : "estorno"), "sistema", null,
                new { cobrancaId = c.Id, recebido = alvo.Recebido, total = alvo.Total, excedente = excedente > 0 ? excedente : 0m }, ct);
            if (pagouAgora && excedente > 0 && jaPago)
                await CobrancaChatRepositorio.EventoAsync(cx, tx, c.Id, c.Status, c.Status, "sistema", null, new { alerta = "pagamento_excedente", excedente }, ct);

            if (acao != null)
            {
                var agora = DateTimeOffset.UtcNow;
                await cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO delivery_realtime_outbox (event_id, event_name, target_group, estabelecimento_id, aggregate_version, payload, occurred_at_utc)
VALUES (@Id, @Nome, @Grupo, @Loja, @Versao, CAST(@Payload AS jsonb), NOW())", new
                {
                    Id = Guid.NewGuid(), Nome = DeliveryRealtimeEvents.DeliveryOrderUpdated, Grupo = DeliveryRealtimeEvents.EstablishmentGroup(c.IdEstabelecimento),
                    Loja = c.IdEstabelecimento, Versao = agora.ToUnixTimeMilliseconds(),
                    Payload = JsonSerializer.Serialize(new { schemaVersion = 2, pedidoId, action = acao, cobrancaId = c.Id })
                }, tx, cancellationToken: ct));
            }
        }

        /// <summary>
        /// Pre-pedido do cardapio cobrado pelo chat: espelha o status de pagamento e, se ele esperava o pagamento, libera para o aceite do
        /// restaurante (nunca aceita sozinho nem cria o pedido real: isso continua com o atendente/fluxo do cardapio).
        /// </summary>
        private static async Task PrePedidoAsync(NpgsqlConnection cx, NpgsqlTransaction tx, CobrancaChatLinha c, PagamentoIntent intent, CancellationToken ct)
        {
            await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE cardapio_pedido_publico SET status_pagamento = @S, updated_at = NOW()
 WHERE id = @Id AND id_estabelecimento = @Loja AND status_pagamento IS DISTINCT FROM @S",
                new { S = PagamentoStatusRules.StatusPagamentoPrePedido(intent.Status), Id = c.PrePedidoId, Loja = c.IdEstabelecimento }, tx, cancellationToken: ct));
            if (intent.Status == StatusIntent.Pago)
                await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE cardapio_pedido_publico SET status = 'aguardando_aceite', updated_at = NOW()
 WHERE id = @Id AND id_estabelecimento = @Loja AND status = 'aguardando_pagamento'", new { Id = c.PrePedidoId, Loja = c.IdEstabelecimento }, tx, cancellationToken: ct));
        }
    }
}
