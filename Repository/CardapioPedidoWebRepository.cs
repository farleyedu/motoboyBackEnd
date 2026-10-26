using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using APIBack.Model.Cardapio;
using APIBack.Repository.Interface;
using APIBack.Service;
using Dapper;
using Npgsql;

namespace APIBack.Repository
{
    /// <summary>
    /// Leituras e transicoes do pre-pedido do cardapio web. Sem a migration 20261002_03 (confirmacao) ou 20261101_30 (pagamento
    /// online) as consultas respondem 503 MIGRATION_PENDING (o resto do cardapio segue funcionando).
    /// </summary>
    public sealed class CardapioPedidoWebRepository : ICardapioPedidoWebRepository
    {
        private const string Colunas = @"
id, id_estabelecimento, codigo, status, tipo_entrega, nome_cliente, telefone_cliente, email_cliente,
forma_pagamento, observacoes, subtotal_produtos, subtotal_adicionais, taxa_entrega, total,
itens_json::text AS itens_json, endereco_entrega_json::text AS endereco_entrega_json, status_pagamento,
created_at, updated_at, codigo_confirmacao, codigo_expira_em, canal_confirmacao, telefone_contato,
id_conversa, confirmado_em, aceito_em, recusado_em, motivo_recusa, id_pedido,
pagamento_online, metodo_online, chave_idempotencia, id_intent_atual, id_intent_pago, pago_em, cancelado_em, motivo_cancelamento";

        private const string ColunasReembolso = @"
id, id_estabelecimento, pre_pedido_id, id_intent, tipo, motivo, valor, justificativa, status, solicitado_por, decidido_por, decidido_em,
id_estorno, idempotency_key, tentativas, ultimo_erro, concluido_em, created_at";

        // Confirmar o numero: presencial (ou online ja pago) vai ao restaurante; online sem dinheiro espera o pagamento.
        private const string StatusAoConfirmar =
            "CASE WHEN pagamento_online AND id_intent_pago IS NULL THEN 'aguardando_pagamento' ELSE 'aguardando_aceite' END";

        private static readonly TimeSpan LeaseReembolso = TimeSpan.FromMinutes(3);

        private readonly NpgsqlDataSource _dataSource;

        public CardapioPedidoWebRepository(NpgsqlDataSource dataSource)
        {
            _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        }

        private static DeliveryDomainException MigrationPending() =>
            new(503, "MIGRATION_PENDING", "O pedido do cardapio web ainda nao foi habilitado neste ambiente (migrations 20261002_03 e 20261101_30 pendentes).");

        private static bool IsMissingSchema(PostgresException ex) =>
            ex.SqlState is PostgresErrorCodes.UndefinedColumn or PostgresErrorCodes.UndefinedTable;

        private async Task<T> GuardAsync<T>(Func<NpgsqlConnection, Task<T>> action)
        {
            try
            {
                await using var connection = await _dataSource.OpenConnectionAsync();
                return await action(connection);
            }
            catch (PostgresException ex) when (IsMissingSchema(ex))
            {
                throw MigrationPending();
            }
        }

        /// <summary>Uma transacao com o pre-pedido travado (FOR UPDATE): aceite, recusa, cancelamento e o webhook se enfileiram nela.</summary>
        private Task<T> TransacaoAsync<T>(Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> action) =>
            GuardAsync(async connection =>
            {
                await using var tx = await connection.BeginTransactionAsync();
                var resultado = await action(connection, tx);
                await tx.CommitAsync();
                return resultado;
            });

        public Task<CardapioPedidoPublico?> ObterAsync(Guid id) =>
            GuardAsync(connection => connection.QuerySingleOrDefaultAsync<CardapioPedidoPublico>(
                $"SELECT {Colunas} FROM cardapio_pedido_publico WHERE id = @Id;", new { Id = id }));

        public Task<CardapioPedidoPublico?> ObterAsync(Guid estabelecimentoId, Guid id) =>
            GuardAsync(connection => connection.QuerySingleOrDefaultAsync<CardapioPedidoPublico>(
                $"SELECT {Colunas} FROM cardapio_pedido_publico WHERE id = @Id AND id_estabelecimento = @EstabelecimentoId;",
                new { Id = id, EstabelecimentoId = estabelecimentoId }));

        public Task<int> ExpirarCodigosVencidosAsync(Guid estabelecimentoId) =>
            GuardAsync(connection => connection.ExecuteAsync(@"
UPDATE cardapio_pedido_publico
   SET status = 'expirado', updated_at = NOW()
 WHERE id_estabelecimento = @EstabelecimentoId
   AND status = 'aguardando_codigo'
   AND codigo_expira_em <= NOW();", new { EstabelecimentoId = estabelecimentoId }));

        public async Task<(CardapioPedidoPublico? Pedido, bool Colisao)> DefinirCodigoAsync(Guid id, string codigo, TimeSpan validade)
        {
            try
            {
                var pedido = await GuardAsync(connection => connection.QuerySingleOrDefaultAsync<CardapioPedidoPublico>($@"
UPDATE cardapio_pedido_publico
   SET codigo_confirmacao = @Codigo,
       codigo_expira_em = NOW() + make_interval(secs => @Segundos),
       status = 'aguardando_codigo',
       updated_at = NOW()
 WHERE id = @Id
   AND status IN ('pendente', 'aguardando_codigo', 'expirado')
RETURNING {Colunas};", new { Id = id, Codigo = codigo, Segundos = validade.TotalSeconds }));
                return (pedido, false);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                return (null, true);
            }
        }

        public async Task<bool> TrocarTelefoneAsync(Guid id, string telefoneCliente)
        {
            var affected = await GuardAsync(connection => connection.ExecuteAsync(@"
UPDATE cardapio_pedido_publico
   SET telefone_cliente = @Telefone,
       telefone_contato = NULL,
       canal_confirmacao = NULL,
       codigo_confirmacao = NULL,
       codigo_expira_em = NULL,
       updated_at = NOW()
 WHERE id = @Id
   AND status = 'aguardando_codigo';", new { Id = id, Telefone = telefoneCliente })
            );
            return affected > 0;
        }

        // Um UPDATE so: se o webhook confirmar o pagamento ao mesmo tempo, o Postgres reavalia o CASE sobre a linha ja paga.
        public async Task<bool> MarcarAguardandoAceiteAsync(Guid id, string telefoneContato, Guid conversaId) =>
            await GuardAsync(connection => connection.ExecuteAsync($@"
UPDATE cardapio_pedido_publico
   SET status = {StatusAoConfirmar},
       canal_confirmacao = 'janela_aberta',
       telefone_contato = @Telefone,
       id_conversa = @ConversaId,
       confirmado_em = NOW(),
       updated_at = NOW()
 WHERE id = @Id
   AND status = 'pendente';", new { Id = id, Telefone = telefoneContato, ConversaId = conversaId })) > 0;

        public Task<CardapioPedidoPublico?> ConfirmarPorCodigoAsync(Guid estabelecimentoId, string codigo, string telefoneContato, Guid conversaId) =>
            GuardAsync(connection => connection.QuerySingleOrDefaultAsync<CardapioPedidoPublico>($@"
UPDATE cardapio_pedido_publico
   SET status = {StatusAoConfirmar},
       canal_confirmacao = 'codigo',
       telefone_contato = @Telefone,
       id_conversa = @ConversaId,
       confirmado_em = NOW(),
       updated_at = NOW()
 WHERE id_estabelecimento = @EstabelecimentoId
   AND status = 'aguardando_codigo'
   AND codigo_confirmacao = @Codigo
   AND telefone_cliente = @Telefone
   AND codigo_expira_em > NOW()
RETURNING {Colunas};", new { EstabelecimentoId = estabelecimentoId, Codigo = codigo, Telefone = telefoneContato, ConversaId = conversaId }));

        public Task<bool> CodigoDeOutroTelefoneAsync(Guid estabelecimentoId, string codigo, string telefoneContato) =>
            GuardAsync(async connection => await connection.ExecuteScalarAsync<bool>(@"
SELECT EXISTS (
    SELECT 1
      FROM cardapio_pedido_publico
     WHERE id_estabelecimento = @EstabelecimentoId
       AND status = 'aguardando_codigo'
       AND codigo_confirmacao = @Codigo
       AND codigo_expira_em > NOW()
       AND telefone_cliente <> @Telefone
);", new { EstabelecimentoId = estabelecimentoId, Codigo = codigo, Telefone = telefoneContato }));

        public async Task<IReadOnlyList<CardapioPedidoPublico>> ListarAguardandoAceiteAsync(Guid estabelecimentoId, int limite) =>
            (await GuardAsync(connection => connection.QueryAsync<CardapioPedidoPublico>($@"
SELECT {Colunas}
  FROM cardapio_pedido_publico
 WHERE id_estabelecimento = @EstabelecimentoId
   AND status IN ('aguardando_aceite', 'aceitando')
   AND (NOT pagamento_online OR id_intent_pago IS NOT NULL)
 ORDER BY confirmado_em ASC, created_at ASC
 LIMIT @Limite;", new { EstabelecimentoId = estabelecimentoId, Limite = Math.Clamp(limite, 1, 100) }))).ToList();

        public async Task<bool> IniciarAceiteAsync(Guid estabelecimentoId, Guid id) =>
            await GuardAsync(connection => connection.ExecuteAsync(@"
UPDATE cardapio_pedido_publico
   SET status = 'aceitando', updated_at = NOW()
 WHERE id = @Id
   AND id_estabelecimento = @EstabelecimentoId
   AND status IN ('aguardando_aceite', 'aceitando')
   AND (NOT pagamento_online OR (id_intent_pago IS NOT NULL AND status_pagamento = 'pago'));",
                new { Id = id, EstabelecimentoId = estabelecimentoId })) > 0;

        public Task DesfazerAceiteAsync(Guid estabelecimentoId, Guid id) =>
            GuardAsync(connection => connection.ExecuteAsync(@"
UPDATE cardapio_pedido_publico
   SET status = 'aguardando_aceite', updated_at = NOW()
 WHERE id = @Id AND id_estabelecimento = @EstabelecimentoId AND status = 'aceitando';",
                new { Id = id, EstabelecimentoId = estabelecimentoId }));

        public async Task<bool> MarcarAceitoAsync(Guid estabelecimentoId, Guid id, int? pedidoId) =>
            await GuardAsync(connection => connection.ExecuteAsync(@"
UPDATE cardapio_pedido_publico
   SET status = 'aceito', aceito_em = NOW(), id_pedido = @PedidoId, updated_at = NOW()
 WHERE id = @Id
   AND id_estabelecimento = @EstabelecimentoId
   AND status = 'aceitando';", new { Id = id, EstabelecimentoId = estabelecimentoId, PedidoId = pedidoId })) > 0;

        public Task<CardapioDecisao> RecusarAsync(Guid estabelecimentoId, Guid id, string? motivo) =>
            TransacaoAsync(async (cx, tx) =>
            {
                var atual = await TravarAsync(cx, tx, id, estabelecimentoId);
                if (atual == null) return new CardapioDecisao(false, string.Empty, null, null, 0m);
                if (atual.Status != CardapioPedidoStatus.AguardandoAceite) return new CardapioDecisao(false, atual.Status, atual, null, 0m);

                var pedido = await cx.QuerySingleAsync<CardapioPedidoPublico>($@"
UPDATE cardapio_pedido_publico
   SET status = 'recusado', recusado_em = NOW(), motivo_recusa = @Motivo, updated_at = NOW()
 WHERE id = @Id
RETURNING {Colunas};", new { Id = id, Motivo = motivo }, tx);
                var (reembolso, valor) = await ReembolsoIntegralAsync(cx, tx, pedido, CardapioReembolsoMotivo.RecusaLoja);
                return new CardapioDecisao(true, pedido.Status, pedido, reembolso, valor);
            });

        public Task<CardapioDecisao> CancelarAsync(Guid id, string motivoCancelamento, string motivoReembolso, IReadOnlyCollection<string> statusPermitidos) =>
            TransacaoAsync(async (cx, tx) =>
            {
                var atual = await TravarAsync(cx, tx, id, null);
                if (atual == null) return new CardapioDecisao(false, string.Empty, null, null, 0m);
                if (!statusPermitidos.Contains(atual.Status)) return new CardapioDecisao(false, atual.Status, atual, null, 0m);

                var pedido = await cx.QuerySingleAsync<CardapioPedidoPublico>($@"
UPDATE cardapio_pedido_publico
   SET status = 'cancelado', cancelado_em = NOW(), motivo_cancelamento = @Motivo,
       codigo_confirmacao = NULL, codigo_expira_em = NULL, updated_at = NOW()
 WHERE id = @Id
RETURNING {Colunas};", new { Id = id, Motivo = motivoCancelamento }, tx);
                var (reembolso, valor) = await ReembolsoIntegralAsync(cx, tx, pedido, motivoReembolso);
                return new CardapioDecisao(true, pedido.Status, pedido, reembolso, valor);
            });

        private static Task<CardapioPedidoPublico?> TravarAsync(NpgsqlConnection cx, NpgsqlTransaction tx, Guid id, Guid? estabelecimentoId) =>
            cx.QuerySingleOrDefaultAsync<CardapioPedidoPublico>($@"
SELECT {Colunas} FROM cardapio_pedido_publico
 WHERE id = @Id AND (@Loja::uuid IS NULL OR id_estabelecimento = @Loja)
   FOR UPDATE;", new { Id = id, Loja = estabelecimentoId }, tx);

        /// <summary>Pedido pago: tudo o que ainda nao voltou volta (linha de reembolso automatica, processada depois do commit).</summary>
        private static async Task<(Guid? Id, decimal Valor)> ReembolsoIntegralAsync(NpgsqlConnection cx, NpgsqlTransaction tx, CardapioPedidoPublico pedido, string motivo)
        {
            if (!pedido.IdIntentPago.HasValue) return (null, 0m);
            // Sem travar a intencao (o sincronizador trava intencao -> pre-pedido; aqui e o contrario): o trigger do estorno barra excesso.
            var saldo = await cx.ExecuteScalarAsync<decimal>(@"
SELECT i.valor_pago - i.valor_estornado
       - COALESCE((SELECT SUM(r.valor) FROM cardapio_pedido_reembolso r
                    WHERE r.id_intent = i.id AND r.status IN ('aguardando_aprovacao', 'reembolso_pendente', 'reembolso_em_processamento')), 0)
  FROM pagamento_intent i WHERE i.id = @Intent AND i.status IN ('pago', 'estornado_parcial');",
                new { Intent = pedido.IdIntentPago }, tx);
            if (saldo <= 0) return (null, 0m);

            var id = await CardapioPagamentoSql.RegistrarReembolsoAsync(cx, tx, pedido.IdEstabelecimento, pedido.Id, pedido.IdIntentPago.Value,
                CardapioReembolsoTipo.Automatico, motivo, saldo, CardapioReembolsoStatus.Pendente, null, null, default);
            id ??= await cx.ExecuteScalarAsync<Guid?>("SELECT id FROM cardapio_pedido_reembolso WHERE idempotency_key = @Chave",
                new { Chave = CardapioPagamentoRules.ChaveReembolso(pedido.Id, pedido.IdIntentPago.Value, motivo) }, tx);
            return (id, saldo);
        }

        public Task<ConversaPorTelefone?> ObterConversaPorTelefoneAsync(Guid estabelecimentoId, IReadOnlyList<string> variantes)
        {
            if (variantes.Count == 0) return Task.FromResult<ConversaPorTelefone?>(null);

            return GuardAsync(async connection =>
            {
                var row = await connection.QueryFirstOrDefaultAsync<ConversaRow>(@"
SELECT c.id AS ConversaId, c.janela_24h_fim AS JanelaFim, c.data_ultima_entrada AS UltimaEntrada
  FROM conversas c
  JOIN clientes cl ON cl.id = c.id_cliente
 WHERE c.id_estabelecimento = @EstabelecimentoId
   AND regexp_replace(cl.telefone_e164, '\D', '', 'g') = ANY(@Variantes)
 ORDER BY c.data_ultima_entrada DESC NULLS LAST, c.janela_24h_fim DESC NULLS LAST, c.data_ultima_mensagem DESC NULLS LAST
 LIMIT 1;", new { EstabelecimentoId = estabelecimentoId, Variantes = variantes.ToArray() });
                return row == null ? null : new ConversaPorTelefone(row.ConversaId, row.JanelaFim, row.UltimaEntrada);
            });
        }

        // =====================================================================
        // Pagamento online
        // =====================================================================

        public Task<CardapioPedidoPublico?> ObterPorChaveAsync(Guid estabelecimentoId, string chaveIdempotencia) =>
            GuardAsync(connection => connection.QuerySingleOrDefaultAsync<CardapioPedidoPublico>(
                $"SELECT {Colunas} FROM cardapio_pedido_publico WHERE id_estabelecimento = @Loja AND chave_idempotencia = @Chave;",
                new { Loja = estabelecimentoId, Chave = chaveIdempotencia }));

        public Task VincularIntentAsync(Guid prePedidoId, Guid intentId) =>
            GuardAsync(connection => connection.ExecuteAsync(@"
UPDATE cardapio_pedido_publico
   SET id_intent_atual = @Intent, updated_at = NOW()
 WHERE id = @Id AND id_intent_pago IS NULL AND id_intent_atual IS DISTINCT FROM @Intent;", new { Id = prePedidoId, Intent = intentId }));

        public async Task<IReadOnlyList<CardapioIntentResumo>> ListarIntentsAsync(Guid prePedidoId) =>
            (await GuardAsync(connection => connection.QueryAsync<CardapioIntentResumo>(@"
SELECT id, status, idempotency_key, created_at
  FROM pagamento_intent
 WHERE pre_pedido_id = @Pre AND origem = 'cardapio'
 ORDER BY created_at, id;", new { Pre = prePedidoId }))).ToList();

        public Task<CardapioPagamentoResumo?> ObterResumoPagamentoAsync(Guid prePedidoId) =>
            GuardAsync(connection => connection.QuerySingleOrDefaultAsync<CardapioPagamentoResumo>(@"
SELECT i.id AS intent_id, i.status AS status_intent, i.versao, i.valor, i.valor_pago, i.valor_estornado,
       p.metodo, p.mp_status_detail AS status_detalhe, p.cartao_bandeira, p.cartao_final, COALESCE(p.expira_em, i.expira_em) AS expira_em,
       i.pago_em, r.status AS status_reembolso, COALESCE(r.valor, 0) AS valor_reembolso
  FROM cardapio_pedido_publico w
  LEFT JOIN pagamento_intent i ON i.id = COALESCE(w.id_intent_pago, w.id_intent_atual)
  LEFT JOIN LATERAL (SELECT * FROM pagamento x WHERE x.id_intent = i.id ORDER BY x.created_at DESC LIMIT 1) p ON TRUE
  LEFT JOIN LATERAL (SELECT status, valor FROM cardapio_pedido_reembolso y
                      WHERE y.pre_pedido_id = w.id AND y.id_intent = w.id_intent_pago AND y.status <> 'negado'
                      ORDER BY y.created_at DESC LIMIT 1) r ON TRUE
 WHERE w.id = @Pre;", new { Pre = prePedidoId }));

        public async Task<IReadOnlyList<CardapioReembolso>> ListarReembolsosAsync(Guid estabelecimentoId, Guid prePedidoId) =>
            (await GuardAsync(connection => connection.QueryAsync<CardapioReembolso>($@"
SELECT {ColunasReembolso} FROM cardapio_pedido_reembolso
 WHERE id_estabelecimento = @Loja AND pre_pedido_id = @Pre ORDER BY created_at;",
                new { Loja = estabelecimentoId, Pre = prePedidoId }))).ToList();

        public Task<CardapioReembolso?> ObterReembolsoAsync(Guid estabelecimentoId, Guid reembolsoId) =>
            GuardAsync(connection => connection.QuerySingleOrDefaultAsync<CardapioReembolso>(
                $"SELECT {ColunasReembolso} FROM cardapio_pedido_reembolso WHERE id = @Id AND id_estabelecimento = @Loja;",
                new { Id = reembolsoId, Loja = estabelecimentoId }));

        public Task<CardapioReembolso?> SolicitarReembolsoAsync(Guid estabelecimentoId, Guid prePedidoId, string tipo, decimal? valor, string? justificativa,
            int usuarioId, string chave) =>
            TransacaoAsync(async (cx, tx) =>
            {
                var existente = await cx.QuerySingleOrDefaultAsync<CardapioReembolso>(
                    $"SELECT {ColunasReembolso} FROM cardapio_pedido_reembolso WHERE idempotency_key = @Chave;", new { Chave = chave }, tx);
                if (existente != null) return existente.IdEstabelecimento == estabelecimentoId ? existente : null;

                var pedido = await TravarAsync(cx, tx, prePedidoId, estabelecimentoId);
                if (pedido?.IdIntentPago == null) return null;
                var saldo = await cx.ExecuteScalarAsync<decimal>(@"
SELECT i.valor_pago - i.valor_estornado
       - COALESCE((SELECT SUM(r.valor) FROM cardapio_pedido_reembolso r
                    WHERE r.id_intent = i.id AND r.status IN ('aguardando_aprovacao', 'reembolso_pendente', 'reembolso_em_processamento')), 0)
  FROM pagamento_intent i WHERE i.id = @Intent AND i.status IN ('pago', 'estornado_parcial');",
                    new { Intent = pedido.IdIntentPago }, tx);
                var pedidoValor = valor ?? saldo;
                if (pedidoValor <= 0 || pedidoValor > saldo) return null;

                var id = await CardapioPagamentoSql.RegistrarReembolsoAsync(cx, tx, estabelecimentoId, prePedidoId, pedido.IdIntentPago.Value, tipo,
                    CardapioReembolsoMotivo.CancelamentoLoja, pedidoValor, CardapioReembolsoStatus.AguardandoAprovacao, justificativa, usuarioId, default, chave);
                return await cx.QuerySingleAsync<CardapioReembolso>(
                    $"SELECT {ColunasReembolso} FROM cardapio_pedido_reembolso WHERE id = @Id;", new { Id = id }, tx);
            });

        public Task<bool> DecidirReembolsoAsync(Guid estabelecimentoId, Guid reembolsoId, bool aprovar, int usuarioId, string? justificativa) =>
            TransacaoAsync(async (cx, tx) =>
            {
                var r = await cx.QuerySingleOrDefaultAsync<CardapioReembolso>($@"
SELECT {ColunasReembolso} FROM cardapio_pedido_reembolso WHERE id = @Id AND id_estabelecimento = @Loja FOR UPDATE;",
                    new { Id = reembolsoId, Loja = estabelecimentoId }, tx);
                if (r == null || r.Status != CardapioReembolsoStatus.AguardandoAprovacao) return false;

                await cx.ExecuteAsync(@"
UPDATE cardapio_pedido_reembolso
   SET status = @Status, decidido_por = @Usuario, decidido_em = NOW(), proxima_tentativa_em = NOW(),
       justificativa = CASE WHEN @Justificativa IS NULL THEN justificativa
                            ELSE concat_ws(' | ', justificativa, @Justificativa) END,
       updated_at = NOW()
 WHERE id = @Id;", new
                {
                    Id = reembolsoId, Status = aprovar ? CardapioReembolsoStatus.Pendente : CardapioReembolsoStatus.Negado,
                    Usuario = usuarioId, Justificativa = justificativa
                }, tx);

                // Devolver tudo depois do aceite e cancelar o pedido (aprovacao do gerente antes do preparo).
                if (aprovar && r.Tipo == CardapioReembolsoTipo.AprovacaoGerente)
                {
                    await cx.ExecuteAsync(@"
UPDATE cardapio_pedido_publico
   SET status = 'cancelado', cancelado_em = NOW(), motivo_cancelamento = COALESCE(motivo_cancelamento, 'cancelado pela loja'), updated_at = NOW()
 WHERE id = @Pre AND status = 'aceito';", new { Pre = r.PrePedidoId }, tx);
                }
                return true;
            });

        public async Task<IReadOnlyList<CardapioReembolso>> ReservarReembolsosPendentesAsync(int limite, Guid? somenteId = null) =>
            (await GuardAsync(connection => connection.QueryAsync<CardapioReembolso>($@"
UPDATE cardapio_pedido_reembolso
   SET proxima_tentativa_em = NOW() + make_interval(secs => @Lease), tentativas = tentativas + 1, updated_at = NOW()
 WHERE id IN (SELECT id FROM cardapio_pedido_reembolso
               WHERE status = 'reembolso_pendente' AND proxima_tentativa_em <= NOW() AND (@Id::uuid IS NULL OR id = @Id)
               ORDER BY proxima_tentativa_em
               LIMIT @Limite
               FOR UPDATE SKIP LOCKED)
RETURNING {ColunasReembolso};", new { Lease = LeaseReembolso.TotalSeconds, Limite = Math.Clamp(limite, 1, 100), Id = somenteId }))).ToList();

        public Task AtualizarReembolsoAsync(Guid reembolsoId, string status, Guid? estornoId, string? erro, TimeSpan? tentarDeNovoEm) =>
            GuardAsync(connection => connection.ExecuteAsync(@"
UPDATE cardapio_pedido_reembolso
   SET status = CASE WHEN status = 'reembolsado' THEN status ELSE @Status END,
       id_estorno = COALESCE(@Estorno, id_estorno),
       ultimo_erro = @Erro,
       proxima_tentativa_em = CASE WHEN @Segundos IS NULL THEN proxima_tentativa_em ELSE NOW() + make_interval(secs => @Segundos) END,
       concluido_em = CASE WHEN @Status = 'reembolsado' THEN COALESCE(concluido_em, NOW()) ELSE concluido_em END,
       updated_at = NOW()
 WHERE id = @Id;", new
            {
                Id = reembolsoId, Status = status, Estorno = estornoId, Erro = erro is { Length: > 300 } ? erro[..300] : erro,
                Segundos = tentarDeNovoEm?.TotalSeconds
            }));

        public async Task<IReadOnlyList<Guid>> ListarPagosSemConfirmacaoAsync(TimeSpan prazo, int limite) =>
            (await GuardAsync(connection => connection.QueryAsync<Guid>(@"
SELECT id FROM cardapio_pedido_publico
 WHERE pagamento_online AND id_intent_pago IS NOT NULL
   AND status IN ('aguardando_codigo', 'expirado', 'pendente')
   AND pago_em <= NOW() - make_interval(secs => @Segundos)
 ORDER BY pago_em
 LIMIT @Limite;", new { Segundos = prazo.TotalSeconds, Limite = Math.Clamp(limite, 1, 100) }))).ToList();

        public Task<string?> ObterStatusPedidoRealAsync(Guid estabelecimentoId, int pedidoId) =>
            GuardAsync(async connection =>
            {
                var status = await connection.ExecuteScalarAsync<int?>(
                    "SELECT COALESCE(status_pedido, 1) FROM pedido WHERE id = @Id AND id_estabelecimento = @Loja;", new { Id = pedidoId, Loja = estabelecimentoId });
                return status.HasValue ? APIBack.Model.Enum.StatusPedidoExtensions.ToApiKey(status) : null;
            });

        private sealed class ConversaRow
        {
            public Guid ConversaId { get; set; }
            public DateTimeOffset? JanelaFim { get; set; }
            public DateTimeOffset? UltimaEntrada { get; set; }
        }
    }
}
