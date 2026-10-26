using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Atendimento;
using Dapper;
using Npgsql;

namespace APIBack.Payments.MercadoPago
{
    /// <summary>Auditoria financeira (somente insercao). Nunca grava token, segredo ou dado de cartao.</summary>
    public static class PagamentoAuditoria
    {
        public static Task RegistrarAsync(NpgsqlConnection cx, NpgsqlTransaction? tx, Guid? loja, string entidade, string entidadeId, string acao,
            string atorTipo, int? atorId, object? dados, CancellationToken ct = default) =>
            cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO pagamento_auditoria (id_estabelecimento, entidade, entidade_id, acao, ator_tipo, ator_id, dados)
VALUES (@Loja, @Entidade, @EntidadeId, @Acao, @Ator, @AtorId, CAST(@Dados AS jsonb))", new
            {
                Loja = loja, Entidade = entidade, EntidadeId = entidadeId, Acao = acao, Ator = atorTipo, AtorId = atorId,
                Dados = dados == null ? null : JsonSerializer.Serialize(dados)
            }, tx, cancellationToken: ct));
    }

    /// <summary>Nomes e grupo dos eventos SignalR de pagamento (contrato em CONTRATOS.md).</summary>
    public static class PagamentoRealtimeEventos
    {
        /// <summary>Uma intencao de pagamento mudou de status. Payload sem valor de token, QR ou dado bancario.</summary>
        public const string PagamentoAtualizado = "pagamentoAtualizado";
        /// <summary>Uma cobranca do chat mudou. O front busca GET /api/v2/atendimento/cobrancas/{id} em seguida.</summary>
        public const string CobrancaAtualizada = "cobrancaAtualizada";

        public static string Grupo(Guid estabelecimentoId) => ChatRealtimeEvents.Group(estabelecimentoId);
    }

    /// <summary>Outbox transacional: gravada na mesma transacao da mudanca, publicada pelo PagamentoOutboxPublisher depois do commit.</summary>
    public static class PagamentoOutbox
    {
        public static Task PagamentoAtualizadoAsync(NpgsqlConnection cx, NpgsqlTransaction tx, PagamentoIntent intent, Guid? cobrancaId, CancellationToken ct = default) =>
            GravarAsync(cx, tx, intent.IdEstabelecimento, PagamentoRealtimeEventos.PagamentoAtualizado, new
            {
                intentId = intent.Id,
                estabelecimentoId = intent.IdEstabelecimento,
                origem = intent.Origem,
                status = intent.Status,
                versao = intent.Versao,
                pedidoId = intent.PedidoId,
                prePedidoId = intent.PrePedidoId,
                conversaId = intent.ConversaId,
                cobrancaId,
                ocorridoEm = DateTime.UtcNow
            }, ct);

        public static Task CobrancaAtualizadaAsync(NpgsqlConnection cx, NpgsqlTransaction tx, CobrancaChatRegistro c, CancellationToken ct = default) =>
            GravarAsync(cx, tx, c.IdEstabelecimento, PagamentoRealtimeEventos.CobrancaAtualizada, new
            {
                id = c.Id,
                conversaId = c.ConversaId,
                pedidoId = c.PedidoId,
                status = c.Status,
                versao = c.Versao,
                ocorridoEm = DateTime.UtcNow
            }, ct);

        private static Task GravarAsync(NpgsqlConnection cx, NpgsqlTransaction tx, Guid loja, string evento, object payload, CancellationToken ct) =>
            cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO pagamento_outbox (event_id, id_estabelecimento, evento, grupo, payload)
VALUES (@Id, @Loja, @Evento, @Grupo, CAST(@Payload AS jsonb))", new
            {
                Id = Guid.NewGuid(), Loja = loja, Evento = evento, Grupo = PagamentoRealtimeEventos.Grupo(loja), Payload = JsonSerializer.Serialize(payload)
            }, tx, cancellationToken: ct));
    }

    /// <summary>Token opaco do link publico (pagina Pix). So o hash fica no banco.</summary>
    public static class TokenPublico
    {
        public static string Gerar() =>
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

        public static bool FormatoValido(string? token) =>
            !string.IsNullOrWhiteSpace(token) && token.Length is >= 40 and <= 64 && token.AsSpan().IndexOfAnyExcept("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_") < 0;
    }

    public static class PagamentoIntentSql
    {
        public const string Colunas = @"id, id_estabelecimento, ambiente, origem, conta_tipo, id_conta_loja, id_conta_zippy, pedido_id, pre_pedido_id,
conversa_id, cliente_id, origem_ref_id, valor, moeda, descricao, status, versao, external_reference, idempotency_key, valor_pago,
valor_estornado, token_publico_hash, expira_em, pago_em, criado_por, created_at, updated_at";

        public const string ColunasPagamento = @"id, id_intent, id_tentativa, id_estabelecimento, ambiente, mp_payment_id, mp_order_id, mp_collector_id, metodo,
payment_method_id, payment_type_id, status, mp_status, mp_status_detail, valor, valor_estornado, moeda, pix_copia_cola, pix_qr_code_base64,
pix_ticket_url, cartao_bandeira, cartao_final, parcelas, live_mode, expira_em, aprovado_em, mp_atualizado_em, versao, created_at, updated_at";

        public static Task<PagamentoIntent?> ObterAsync(NpgsqlConnection cx, NpgsqlTransaction? tx, Guid id, bool travar, CancellationToken ct) =>
            cx.QueryFirstOrDefaultAsync<PagamentoIntent>(new CommandDefinition(
                $"SELECT {Colunas} FROM pagamento_intent WHERE id = @Id{(travar ? " FOR UPDATE" : "")}", new { Id = id }, tx, cancellationToken: ct));

        public static Task<PagamentoIntent?> PorReferenciaAsync(NpgsqlConnection cx, NpgsqlTransaction tx, string externalReference, CancellationToken ct) =>
            cx.QueryFirstOrDefaultAsync<PagamentoIntent>(new CommandDefinition(
                $"SELECT {Colunas} FROM pagamento_intent WHERE external_reference = @R FOR UPDATE", new { R = externalReference }, tx, cancellationToken: ct));

        public static Task<PagamentoRegistro?> UltimoPagamentoAsync(NpgsqlConnection cx, NpgsqlTransaction? tx, Guid idIntent, CancellationToken ct) =>
            cx.QueryFirstOrDefaultAsync<PagamentoRegistro>(new CommandDefinition(
                $"SELECT {ColunasPagamento} FROM pagamento WHERE id_intent = @Id ORDER BY created_at DESC, mp_payment_id DESC LIMIT 1",
                new { Id = idIntent }, tx, cancellationToken: ct));

        /// <summary>external_reference proprio: zp_{ambiente curto}_{origem}_{id da intencao}. Nada de dado do cliente.</summary>
        public static string ExternalReference(string ambiente, string origem, Guid intentId) =>
            $"zp_{(ambiente == Ambientes.Producao ? "p" : "s")}_{origem}_{intentId:N}";

        public static bool ReferenciaNossa(string? externalReference) =>
            externalReference != null && (externalReference.StartsWith("zp_p_", StringComparison.Ordinal) || externalReference.StartsWith("zp_s_", StringComparison.Ordinal));
    }

    /// <summary>Efeito de uma mudanca de intencao no dominio que a originou (cardapio, chat, assinatura...). Roda DENTRO da transacao.</summary>
    public interface IPagamentoEfeitoHandler
    {
        string Origem { get; }
        /// <summary>Devolve o id da cobranca do chat afetada (so o handler do chat), para o payload de pagamentoAtualizado.</summary>
        Task<Guid?> AplicarAsync(NpgsqlConnection cx, NpgsqlTransaction tx, PagamentoIntent intent, string statusAnterior, string ator, CancellationToken ct);
    }

    /// <summary>
    /// Cardapio web: espelha o status no cardapio_pedido_publico.status_pagamento e, quando o dinheiro e confirmado, libera o pedido
    /// online para o aceite (ou agenda o reembolso se ele ja foi recusado/cancelado ou pago em dobro). Ver APIBack.Repository.CardapioPagamentoSql.
    /// </summary>
    public sealed class CardapioPagamentoEfeito : IPagamentoEfeitoHandler
    {
        public string Origem => OrigensPagamento.Cardapio;

        public async Task<Guid?> AplicarAsync(NpgsqlConnection cx, NpgsqlTransaction tx, PagamentoIntent intent, string statusAnterior, string ator, CancellationToken ct)
        {
            await APIBack.Repository.CardapioPagamentoSql.AplicarEfeitoAsync(cx, tx, intent, ct);
            return null;
        }
    }

    /// <summary>Chat: a cobranca acompanha a intencao pela maquina de estados da cobranca; grava historico e cobrancaAtualizada.</summary>
    public sealed class ChatCobrancaEfeito : IPagamentoEfeitoHandler
    {
        public string Origem => OrigensPagamento.Chat;

        public async Task<Guid?> AplicarAsync(NpgsqlConnection cx, NpgsqlTransaction tx, PagamentoIntent intent, string statusAnterior, string ator, CancellationToken ct)
        {
            var cobranca = await cx.QueryFirstOrDefaultAsync<CobrancaChatRegistro>(new CommandDefinition(@"
SELECT id, id_estabelecimento, conversa_id, id_intent, pedido_id, status, versao FROM cobranca_chat
 WHERE id_intent = @Id AND id_estabelecimento = @Loja FOR UPDATE", new { intent.Id, Loja = intent.IdEstabelecimento }, tx, cancellationToken: ct));
            if (cobranca == null) return null;

            var novo = PagamentoStatusRules.StatusDaCobranca(cobranca.Status, intent.Status);
            if (novo == null) return cobranca.Id;

            await CobrancaChatSql.MudarStatusAsync(cx, tx, cobranca, novo, ator, null, new { intentStatus = intent.Status }, ct);
            return cobranca.Id;
        }
    }

    public static class CobrancaChatSql
    {
        /// <summary>Muda o status (validando a maquina), incrementa a versao, grava o historico e a outbox.</summary>
        public static async Task MudarStatusAsync(NpgsqlConnection cx, NpgsqlTransaction tx, CobrancaChatRegistro c, string novo, string ator, int? usuarioId,
            object? detalhe, CancellationToken ct)
        {
            if (!PagamentoStatusRules.PodeCobranca(c.Status, novo))
                throw PagamentoException.Estado($"Cobranca em '{c.Status}' nao pode ir para '{novo}'.", c.Status);
            var anterior = c.Status;
            c.Status = novo;
            c.Versao++;
            await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE cobranca_chat SET status = @S, versao = @V, updated_at = NOW(),
       pago_em = CASE WHEN @S = 'pago' THEN COALESCE(pago_em, NOW()) ELSE pago_em END,
       cancelado_em = CASE WHEN @S = 'cancelado' THEN NOW() ELSE cancelado_em END,
       cancelado_por = CASE WHEN @S = 'cancelado' THEN @U ELSE cancelado_por END
 WHERE id = @Id", new { S = novo, V = c.Versao, c.Id, U = usuarioId }, tx, cancellationToken: ct));
            await cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO cobranca_chat_evento (id_cobranca, status_anterior, status_novo, ator_tipo, usuario_id, detalhe)
VALUES (@Id, @A, @N, @Ator, @U, CAST(@D AS jsonb))", new
            {
                c.Id, A = anterior, N = novo, Ator = ator, U = usuarioId, D = detalhe == null ? null : JsonSerializer.Serialize(detalhe)
            }, tx, cancellationToken: ct));
            await PagamentoOutbox.CobrancaAtualizadaAsync(cx, tx, c, ct);
        }
    }
}
