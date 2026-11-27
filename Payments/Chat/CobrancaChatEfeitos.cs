using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Hubs;
using APIBack.Payments.MercadoPago;
using APIBack.Repository;
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

            var cardapioAvisa = c.PrePedidoId.HasValue && await PrePedidoAsync(cx, tx, c, intent, ct);

            if (pagouAgora)
            {
                var tardio = statusAnteriorIntent is StatusIntent.Expirado or StatusIntent.Cancelado;
                await PagamentoAuditoria.RegistrarAsync(cx, tx, c.IdEstabelecimento, "cobranca_chat", c.Id.ToString(), tardio ? "paga_tardia" : "paga", "sistema", null,
                    new { intentId = intent.Id, valor = intent.ValorPago, c.PedidoId, c.PrePedidoId, c.ConversaId, autor = c.AutorUsuarioId }, ct);
                // Pago atrasado (Pix vencido/cancelado que entrou mesmo assim): a cobranca reemitida no lugar dele nao pode ser paga tambem.
                if (await TemSucessoraAbertaAsync(cx, tx, c.Id, ct))
                    await CobrancaChatRepositorio.AgendarTarefaAsync(cx, tx, c.Id, c.IdEstabelecimento, TiposTarefa.CancelarSucessora, ct);

                var config = await CobrancaChatRepositorio.ConfigAsync(cx, c.IdEstabelecimento, ct);
                // Quem esperava "pagamento aprovado" do cardapio recebe so ele, e nao tambem o aviso do chat.
                if (config.ConfirmarPagamentoCliente && !cardapioAvisa)
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
        /// Pre-pedido do cardapio cobrado pelo chat. Quando o dinheiro entra num pedido online ainda sem pagamento, esta intencao vira
        /// "a" intencao dele (e por ela que a fila da loja, o aceite, a recusa e o reembolso o enxergam) e quem esperava o pagamento vai
        /// para o aceite do restaurante (nunca aceita sozinho nem cria o pedido real: isso continua com o atendente/fluxo do cardapio).
        /// Devolve true quando o proprio cardapio vai avisar o cliente de que o pagamento foi aprovado.
        /// </summary>
        public static async Task<bool> PrePedidoAsync(NpgsqlConnection cx, NpgsqlTransaction tx, CobrancaChatLinha c, PagamentoIntent intent, CancellationToken ct)
        {
            var cardapioAvisa = false;
            if (intent.Status == StatusIntent.Pago)
            {
                var metodo = await CardapioPagamentoSql.MetodoPagoAsync(cx, tx, intent, ct);
                // Recusado, cancelado, aceito ou ja pago por outra intencao ficam como estao.
                cardapioAvisa = await cx.ExecuteScalarAsync<bool?>(new CommandDefinition(@"
UPDATE cardapio_pedido_publico
   SET id_intent_pago = @Intent, id_intent_atual = @Intent, pago_em = COALESCE(pago_em, NOW()),
       metodo_online = CASE WHEN @Metodo IN ('pix', 'cartao') THEN @Metodo ELSE metodo_online END,
       confirmado_em = CASE WHEN status = 'aguardando_pagamento' THEN NOW() ELSE confirmado_em END,
       status = CASE WHEN status = 'aguardando_pagamento' THEN 'aguardando_aceite' ELSE status END,
       updated_at = NOW()
 WHERE id = @Id AND id_estabelecimento = @Loja AND pagamento_online AND id_intent_pago IS NULL
   AND status IN ('pendente', 'aguardando_codigo', 'expirado', 'aguardando_pagamento', 'aguardando_aceite')
RETURNING aviso_pagamento_pendente AND status = 'aguardando_aceite'",
                    new { Intent = intent.Id, Metodo = metodo, Id = c.PrePedidoId, Loja = c.IdEstabelecimento }, tx, cancellationToken: ct)) ?? false;
            }

            // O espelho segue so a intencao que importa: uma cobranca vencida nao "despaga" quem pagou por outra.
            await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE cardapio_pedido_publico SET status_pagamento = @S, updated_at = NOW()
 WHERE id = @Id AND id_estabelecimento = @Loja AND status_pagamento IS DISTINCT FROM @S
   AND (id_intent_pago IS NULL OR id_intent_pago = @Intent)",
                new { S = PagamentoStatusRules.StatusPagamentoPrePedido(intent.Status), Intent = intent.Id, Id = c.PrePedidoId, Loja = c.IdEstabelecimento },
                tx, cancellationToken: ct));

            // Reembolso pedido pelo cardapio (recusa, cancelamento) de um pagamento feito pelo chat tambem precisa concluir.
            if (intent.ValorEstornado > 0) await CardapioPagamentoSql.ConcluirReembolsosAsync(cx, tx, intent.Id, ct);
            return cardapioAvisa;
        }

        /// <summary>
        /// O dinheiro saiu deste pre-pedido (cobranca movida para outro pedido): ele volta a esperar pagamento, se ainda nao foi decidido.
        /// </summary>
        public static Task DesvincularPrePedidoAsync(NpgsqlConnection cx, NpgsqlTransaction tx, Guid loja, Guid prePedidoId, Guid intentId, CancellationToken ct) =>
            cx.ExecuteAsync(new CommandDefinition(@"
UPDATE cardapio_pedido_publico
   SET id_intent_pago = NULL, id_intent_atual = NULL, pago_em = NULL, status_pagamento = 'pendente',
       status = CASE WHEN status = 'aguardando_aceite' AND pagamento_online THEN 'aguardando_pagamento' ELSE status END,
       updated_at = NOW()
 WHERE id = @Id AND id_estabelecimento = @Loja AND id_intent_pago = @Intent",
                new { Id = prePedidoId, Loja = loja, Intent = intentId }, tx, cancellationToken: ct));
    }
}
