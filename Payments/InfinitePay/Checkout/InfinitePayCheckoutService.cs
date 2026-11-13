using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Payments.InfinitePay.Cliente;
using APIBack.Payments.MercadoPago;
using APIBack.Payments.Provedores;
using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace APIBack.Payments.InfinitePay.Checkout
{
    /// <summary>
    /// Cria o link hospedado da InfinitePay na conta da PROPRIA loja (versao ligada a intencao, imutavel). Fluxo: intencao + linha de
    /// checkout (com o corpo exato) numa transacao; POST /links fora dela com uma "reserva" de 60 s contra chamadas concorrentes;
    /// URL e status gravados juntos com auditoria e outbox. Nao confia em redirect, nao processa cartao, nao consulta link por order_nsu.
    /// </summary>
    public sealed class InfinitePayCheckoutService : IInfinitePayCheckoutService
    {
        private const int ReservaSegundos = 60;
        private readonly NpgsqlDataSource _db;
        private readonly IPagamentoSegredoProtector _segredos;
        private readonly IInfinitePayClient _client;
        private readonly InfinitePayOptions _options;
        private readonly ILogger<InfinitePayCheckoutService> _logger;
        private readonly Func<DateTime> _agora;

        public InfinitePayCheckoutService(NpgsqlDataSource db, IPagamentoSegredoProtector segredos, IInfinitePayClient client,
            IOptions<InfinitePayOptions> options, ILogger<InfinitePayCheckoutService> logger)
            : this(db, segredos, client, options, logger, () => DateTime.UtcNow) { }

        internal InfinitePayCheckoutService(NpgsqlDataSource db, IPagamentoSegredoProtector segredos, IInfinitePayClient client,
            IOptions<InfinitePayOptions> options, ILogger<InfinitePayCheckoutService> logger, Func<DateTime> agora)
        {
            _db = db;
            _segredos = segredos;
            _client = client;
            _options = options.Value;
            _logger = logger;
            _agora = agora;
        }

        public async Task<InfinitePayCheckoutResultado> CriarAsync(InfinitePayCheckoutRequest r, CancellationToken ct = default)
        {
            if (r == null) throw PagamentoException.Invalida("Cobranca obrigatoria.");
            InfinitePayCheckoutMontagem.ValidarPedido(r);

            await using var cx = await _db.OpenConnectionAsync(ct);
            CheckoutRow checkout;
            PagamentoIntent intent;
            VersaoRow versao;

            await using (var tx = await cx.BeginTransactionAsync(ct))
            {
                await cx.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(hashtext(@K))",
                    new { K = $"infinitepay_checkout:{r.IdEstabelecimento}:{r.IdempotencyKey}" }, tx, cancellationToken: ct));

                var existente = await cx.QueryFirstOrDefaultAsync<PagamentoIntent>(new CommandDefinition(
                    $"SELECT {PagamentoIntentSql.Colunas} FROM pagamento_intent WHERE id_estabelecimento = @Loja AND idempotency_key = @Chave FOR UPDATE",
                    new { Loja = r.IdEstabelecimento, Chave = r.IdempotencyKey }, tx, cancellationToken: ct));

                if (existente != null)
                {
                    ConferirRepeticao(existente, r);
                    intent = existente;
                    versao = await VersaoDaIntentAsync(cx, tx, intent, ct);
                    checkout = await CheckoutDaIntentAsync(cx, tx, intent.Id, ct)
                               ?? throw PagamentoException.Estado("Intencao InfinitePay sem checkout.", intent.Status);
                }
                else
                {
                    if (r.PrePedidoId != Guid.Empty) await ExigirPrePedidoDaLojaAsync(cx, tx, r, ct);
                    versao = await VersaoAtivaAsync(cx, tx, r.IdEstabelecimento, ct);
                    var apiKey = AbrirApiKey(versao);
                    var id = Guid.NewGuid();
                    var orderNsu = PagamentoIntentSql.ExternalReference(Ambientes.Producao, r.Origem, id);
                    var agora = _agora();
                    var (corpo, expira) = InfinitePayCheckoutMontagem.Montar(r, versao.InfiniteTagSnapshot, orderNsu, apiKey != null, _options, agora);
                    var json = InfinitePayClient.Serializar(corpo);

                    await cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO pagamento_intent (id, id_estabelecimento, ambiente, provedor, origem, conta_tipo, id_infinitepay_conta_versao, pedido_id, pre_pedido_id,
                              conversa_id, cliente_id, valor, descricao, external_reference, idempotency_key, expira_em, criado_por)
VALUES (@Id, @Loja, 'producao', 'infinitepay', @Origem, 'loja', @Versao, @PedidoId, @PrePedidoId, @ConversaId, @ClienteId, @Valor, @Descricao,
        @Ref, @Chave, @Expira, @CriadoPor)", new
                    {
                        Id = id, Loja = r.IdEstabelecimento, r.Origem, Versao = versao.Id, r.PedidoId, PrePedidoId = r.PrePedidoId == Guid.Empty ? (Guid?)null : r.PrePedidoId,
                        r.ConversaId, r.ClienteId, r.Valor, r.Descricao,
                        Ref = orderNsu, Chave = r.IdempotencyKey, Expira = expira, r.CriadoPor
                    }, tx, cancellationToken: ct));
                    intent = await PagamentoIntentSql.ObterAsync(cx, tx, id, true, ct) ?? throw new InvalidOperationException("Intencao nao encontrada apos criar.");

                    await cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO infinitepay_checkout (id, id_intent, id_estabelecimento, id_conta_versao, order_nsu, handle_snapshot, valor_centavos, usa_api_key,
                                  payload_json, payload_hash, expira_em)
VALUES (@Id, @Intent, @Loja, @Versao, @Nsu, @Handle, @Centavos, @UsaKey, @Json, @Hash, @Expira)", new
                    {
                        Id = Guid.NewGuid(), Intent = id, Loja = r.IdEstabelecimento, Versao = versao.Id, Nsu = orderNsu, Handle = versao.InfiniteTagSnapshot,
                        Centavos = InfinitePayRegras.Centavos(r.Valor), UsaKey = apiKey != null, Json = json, Hash = Hash(json), Expira = expira
                    }, tx, cancellationToken: ct));
                    await PagamentoAuditoria.RegistrarAsync(cx, tx, r.IdEstabelecimento, "pagamento_intent", id.ToString(), "criada", r.CriadoPor.HasValue ? "usuario" : "sistema",
                        r.CriadoPor, new { provedor = ProvedoresPagamento.InfinitePay, intent.Valor, intent.PrePedidoId, intent.PedidoId }, ct);
                    checkout = (await CheckoutDaIntentAsync(cx, tx, id, ct))!;
                }

                if (checkout.Status == "criado")
                {
                    await tx.CommitAsync(ct);
                    return Resultado(intent, checkout, true);
                }

                // Reserva: so uma chamada por vez vai a InfinitePay para esta intencao.
                var reservou = await cx.ExecuteScalarAsync<Guid?>(new CommandDefinition(@"
UPDATE infinitepay_checkout SET tentativas = tentativas + 1, status = 'criando', claim_ate = NOW() + make_interval(secs => @S), updated_at = NOW()
 WHERE id = @Id AND status <> 'criado' AND (claim_ate IS NULL OR claim_ate < NOW())
RETURNING id", new { checkout.Id, S = ReservaSegundos }, tx, cancellationToken: ct));
                await tx.CommitAsync(ct);
                if (reservou == null)
                    throw new PagamentoException(InfinitePayCodigos.CriacaoEmAndamento, 409, "O link de pagamento ja esta sendo criado. Tente de novo em instantes.");
            }

            var chave = AbrirApiKey(versao);
            try
            {
                var resposta = await _client.CriarLinkAsync(checkout.PayloadJson, chave, ct);
                return await ConcluirAsync(cx, intent, checkout, resposta, r.CriadoPor, ct);
            }
            catch (InfinitePayApiException ex)
            {
                await RegistrarFalhaAsync(cx, checkout.Id, ex, ct);
                _logger.LogWarning("InfinitePay /links falhou para a intencao {Intent} (http {Status}, {Erro}).", intent.Id, ex.StatusHttp, ex.Erro);
                throw ex.ParaPagamento();
            }
        }

        public async Task<InfinitePayCheckoutResultado> ObterAsync(Guid intentId, Guid idEstabelecimento, CancellationToken ct = default)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            var intent = await PagamentoIntentSql.ObterAsync(cx, null, intentId, false, ct);
            if (intent == null || intent.IdEstabelecimento != idEstabelecimento || intent.Provedor != ProvedoresPagamento.InfinitePay)
                throw PagamentoException.NaoEncontrado();
            var checkout = await CheckoutDaIntentAsync(cx, null, intentId, ct);
            if (checkout == null || checkout.Status != "criado" || checkout.CheckoutUrl == null) throw PagamentoException.NaoEncontrado("Link de pagamento ainda nao criado.");
            return Resultado(intent, checkout, true);
        }

        private async Task<InfinitePayCheckoutResultado> ConcluirAsync(NpgsqlConnection cx, PagamentoIntent intent, CheckoutRow checkout, InfinitePayLinkResposta resposta,
            int? usuario, CancellationToken ct)
        {
            await using var tx = await cx.BeginTransactionAsync(ct);
            await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE infinitepay_checkout SET status = 'criado', checkout_url = @Url, resposta_hash = @Hash, claim_ate = NULL, erro_codigo = NULL, erro_mensagem = NULL,
       criado_em = NOW(), updated_at = NOW()
 WHERE id = @Id AND status <> 'criado'", new { checkout.Id, resposta.Url, Hash = resposta.RespostaHash }, tx, cancellationToken: ct));

            var atual = await PagamentoIntentSql.ObterAsync(cx, tx, intent.Id, true, ct) ?? intent;
            if (atual.Status == StatusIntent.Criado)
            {
                await cx.ExecuteAsync(new CommandDefinition(
                    "UPDATE pagamento_intent SET status = 'aguardando_pagamento', versao = versao + 1, updated_at = NOW() WHERE id = @Id AND status = 'criado'",
                    new { atual.Id }, tx, cancellationToken: ct));
                atual = (await PagamentoIntentSql.ObterAsync(cx, tx, intent.Id, false, ct))!;
                await PagamentoAuditoria.RegistrarAsync(cx, tx, atual.IdEstabelecimento, "pagamento_intent", atual.Id.ToString(), "link_criado",
                    usuario.HasValue ? "usuario" : "sistema", usuario, new { provedor = ProvedoresPagamento.InfinitePay, tentativas = checkout.Tentativas + 1 }, ct);
                await PagamentoOutbox.PagamentoAtualizadoAsync(cx, tx, atual, null, ct);
            }
            var salvo = (await CheckoutDaIntentAsync(cx, tx, intent.Id, ct))!;
            await tx.CommitAsync(ct);
            return Resultado(atual, salvo, false);
        }

        private static async Task RegistrarFalhaAsync(NpgsqlConnection cx, Guid checkoutId, InfinitePayApiException ex, CancellationToken ct)
        {
            // Sem resposta definitiva (rede, 429, 5xx): o link pode ter sido criado. Mantem 'criando' e libera a reserva para repetir o MESMO corpo.
            var status = ex.RecusaDefinitiva ? "falhou" : "criando";
            await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE infinitepay_checkout SET status = @S, claim_ate = NULL, erro_codigo = @C, erro_mensagem = @M, updated_at = NOW() WHERE id = @Id AND status <> 'criado'",
                new { Id = checkoutId, S = status, C = ex.Erro ?? $"http_{ex.StatusHttp}", M = ex.Message.Length > 300 ? ex.Message[..300] : ex.Message },
                cancellationToken: CancellationToken.None));
        }

        private static void ConferirRepeticao(PagamentoIntent i, InfinitePayCheckoutRequest r)
        {
            var pre = r.PrePedidoId == Guid.Empty ? (Guid?)null : r.PrePedidoId;
            if (i.Provedor != ProvedoresPagamento.InfinitePay || i.Valor != r.Valor || i.PrePedidoId != pre || i.PedidoId != r.PedidoId ||
                i.Origem != r.Origem)
                throw new PagamentoException(PagamentoCodigos.IdempotenciaConflito, 409, "Esta chave de idempotencia ja foi usada com outros dados.");
        }

        private static async Task ExigirPrePedidoDaLojaAsync(NpgsqlConnection cx, NpgsqlTransaction tx, InfinitePayCheckoutRequest r, CancellationToken ct)
        {
            var ok = await cx.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS (SELECT 1 FROM cardapio_pedido_publico WHERE id = @P AND id_estabelecimento = @Loja)",
                new { P = r.PrePedidoId, Loja = r.IdEstabelecimento }, tx, cancellationToken: ct));
            if (!ok) throw PagamentoException.NaoEncontrado("Pedido nao encontrado nesta loja.");
        }

        private static async Task<VersaoRow> VersaoAtivaAsync(NpgsqlConnection cx, NpgsqlTransaction tx, Guid loja, CancellationToken ct)
        {
            var v = await cx.QueryFirstOrDefaultAsync<VersaoRow>(new CommandDefinition(@"
SELECT v.id, v.id_estabelecimento, v.infinite_tag_snapshot, v.api_key_cifrada, c.habilitada
  FROM infinitepay_conta_loja c JOIN infinitepay_conta_versao v ON v.id_conta = c.id AND v.ativa
 WHERE c.id_estabelecimento = @Loja", new { Loja = loja }, tx, cancellationToken: ct));
            if (v == null || !v.Habilitada)
                throw PagamentoException.Regra(ProvedorPagamentoCodigos.ProvedorNaoConfigurado, "InfinitePay nao esta configurada e habilitada para esta loja.");
            return v;
        }

        /// <summary>Repeticao usa a versao gravada na intencao, nunca a ativa de hoje (nao muda o recebedor).</summary>
        private static async Task<VersaoRow> VersaoDaIntentAsync(NpgsqlConnection cx, NpgsqlTransaction tx, PagamentoIntent intent, CancellationToken ct) =>
            await cx.QueryFirstOrDefaultAsync<VersaoRow>(new CommandDefinition(@"
SELECT id, id_estabelecimento, infinite_tag_snapshot, api_key_cifrada, TRUE AS habilitada
  FROM infinitepay_conta_versao WHERE id = @Id AND id_estabelecimento = @Loja",
                new { Id = intent.IdInfinitePayContaVersao, Loja = intent.IdEstabelecimento }, tx, cancellationToken: ct))
            ?? throw new PagamentoException(ProvedorPagamentoCodigos.ContaDeOutraLoja, 422, "A conta recebedora nao pertence a loja selecionada.");

        private string? AbrirApiKey(VersaoRow v)
        {
            if (string.IsNullOrEmpty(v.ApiKeyCifrada)) return null;
            if (!_segredos.Configurado) throw PagamentoException.NaoConfigurado("Chave mestra de pagamentos nao configurada.");
            return _segredos.Revelar(v.ApiKeyCifrada, SegredoContexto.InfinitePayApiKey(v.Id))
                   ?? throw PagamentoException.NaoConfigurado("Nao foi possivel abrir a API Key InfinitePay.");
        }

        private static Task<CheckoutRow?> CheckoutDaIntentAsync(NpgsqlConnection cx, NpgsqlTransaction? tx, Guid intentId, CancellationToken ct) =>
            cx.QueryFirstOrDefaultAsync<CheckoutRow>(new CommandDefinition(@"
SELECT id, id_intent, order_nsu, status, tentativas, checkout_url, payload_json, usa_api_key, expira_em
  FROM infinitepay_checkout WHERE id_intent = @Id" + (tx != null ? " FOR UPDATE" : ""), new { Id = intentId }, tx, cancellationToken: ct));

        private static InfinitePayCheckoutResultado Resultado(PagamentoIntent intent, CheckoutRow c, bool reaproveitado)
        {
            using var doc = JsonDocument.Parse(c.PayloadJson);
            var opcoes = doc.RootElement.TryGetProperty("options", out var o) && o.ValueKind == JsonValueKind.Object ? o : (JsonElement?)null;
            var metodos = opcoes != null && opcoes.Value.TryGetProperty("payment_methods_enabled", out var m) && m.ValueKind == JsonValueKind.Array
                ? InfinitePayRegras.MetodosEfetivos(new InfinitePayOpcoes { PaymentMethodsEnabled = System.Linq.Enumerable.ToList(System.Linq.Enumerable.Select(m.EnumerateArray(), e => e.GetString() ?? string.Empty)) })
                : InfinitePayRegras.MetodosEfetivos(null);
            var unico = opcoes != null && opcoes.Value.TryGetProperty("single_payment", out var s) && s.ValueKind == JsonValueKind.True;
            return new InfinitePayCheckoutResultado
            {
                IntentId = intent.Id, Status = intent.Status, Versao = intent.Versao, Valor = intent.Valor, MetodosEfetivos = metodos,
                UrlCheckout = c.CheckoutUrl ?? string.Empty, OrderNsu = c.OrderNsu, ExpiraEm = c.ExpiraEm, PagamentoUnico = unico, Reaproveitado = reaproveitado
            };
        }

        private static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

        private sealed class VersaoRow
        {
            public Guid Id { get; set; }
            public Guid IdEstabelecimento { get; set; }
            public string InfiniteTagSnapshot { get; set; } = string.Empty;
            public string? ApiKeyCifrada { get; set; }
            public bool Habilitada { get; set; }
        }

        private sealed class CheckoutRow
        {
            public Guid Id { get; set; }
            public Guid IdIntent { get; set; }
            public string OrderNsu { get; set; } = string.Empty;
            public string Status { get; set; } = string.Empty;
            public int Tentativas { get; set; }
            public string? CheckoutUrl { get; set; }
            public string PayloadJson { get; set; } = string.Empty;
            public bool UsaApiKey { get; set; }
            public DateTime? ExpiraEm { get; set; }
        }
    }
}
