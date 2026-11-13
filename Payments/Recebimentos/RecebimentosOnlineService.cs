using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Payments.Gestao;
using APIBack.Payments.InfinitePay.Webhooks;
using APIBack.Payments.MercadoPago;
using APIBack.Payments.Provedores;
using APIBack.Service;
using Dapper;
using Npgsql;

namespace APIBack.Payments.Recebimentos
{
    /// <summary>
    /// Leitura de recebimentos online sobre pagamento_intent (nenhuma transacao paralela): so vendas das lojas (conta_tipo = 'loja', cardapio e chat),
    /// nunca mensalidade da Zippy nem pagamento presencial. A venda no Caixa continua sendo a do pedido, publicada uma vez na conclusao
    /// (venda:pedido:{id}); aqui so se mostra se ela ja existe. Com <c>Loja</c> preenchida, tudo fica preso aquela loja.
    /// Estorno informado: a InfinitePay nao tem API de estorno, entao a loja informa, fica pendente, e so a Zippy confirma com evidencia.
    /// </summary>
    public interface IRecebimentosOnlineService
    {
        Task<RecebimentosDto> ListarAsync(FiltroRecebimentos filtro, CancellationToken ct = default);
        Task<AlertasRecebimentosDto> AlertasAsync(Guid? loja, CancellationToken ct = default);
        Task<DiagnosticoRecebimentoDto> DiagnosticoAsync(Guid intentId, Guid? loja, CancellationToken ct = default);
        Task<Pagina<PendenciaDto>> PendenciasAsync(Guid? loja, string? status, int pagina, CancellationToken ct = default);
        Task<Pagina<EstornoInformadoDto>> EstornosInformadosAsync(Guid? loja, string? status, int pagina, CancellationToken ct = default);
        Task<EstornoInformadoDto> InformarEstornoAsync(Guid intentId, Guid loja, InformarEstornoRequest request, int usuario, CancellationToken ct = default);
        Task<EstornoInformadoDto> DescartarEstornoAsync(Guid id, Guid? loja, int usuario, CancellationToken ct = default);
        Task<EstornoInformadoDto> ConfirmarEstornoAsync(Guid id, ConfirmarEstornoRequest request, int usuario, CancellationToken ct = default);
        Task ResolverPendenciaAsync(Guid id, ResolverPendenciaRequest request, int usuario, CancellationToken ct = default);
        Task<bool> ReverificarPendenciaAsync(Guid id, int usuario, CancellationToken ct = default);
    }

    public sealed class RecebimentosOnlineService : IRecebimentosOnlineService
    {
        private const int TamanhoPagina = 25;
        private const int MinutosSemConfirmacao = 30;
        private const int MinutosAvisoParado = 15;

        // Aviso sem verificacao: parado na fila, ou encerrado sem conferencia e sem pendencia ja resolvida. Descartado por "nao_confirmado" nao conta: nada provou dinheiro.
        private const string AvisoSemVerificacao = @"
 ((v.status IN ('pendente', 'processando') AND v.recebido_em < NOW() - make_interval(mins => @MinAviso))
  OR (v.status = 'pendencia' AND NOT EXISTS (SELECT 1 FROM infinitepay_pendencia z WHERE z.id_evento = v.id AND z.status = 'resolvida')))";

        private const string De = @"
  FROM pagamento_intent i
  LEFT JOIN estabelecimentos e ON e.id = i.id_estabelecimento
  LEFT JOIN LATERAL (SELECT * FROM pagamento x WHERE x.id_intent = i.id ORDER BY x.created_at DESC LIMIT 1) p ON TRUE
  LEFT JOIN infinitepay_checkout c ON c.id_intent = i.id
  LEFT JOIN cardapio_pedido_publico w ON w.id = i.pre_pedido_id AND w.id_estabelecimento = i.id_estabelecimento
  LEFT JOIN LATERAL (SELECT count(*)::int AS n FROM infinitepay_pendencia q WHERE q.id_intent = i.id AND q.status = 'aberta') pend ON TRUE
  LEFT JOIN LATERAL (SELECT count(*)::int AS n FROM infinitepay_webhook_evento v WHERE v.id_intent = i.id AND" + AvisoSemVerificacao + @") av ON TRUE
  LEFT JOIN pagamento_estorno_informado ei ON ei.id_intent = i.id AND ei.status = 'pendente_confirmacao'
 WHERE i.conta_tipo = 'loja' AND i.origem IN ('cardapio', 'chat')
   AND (i.provedor = 'infinitepay' OR i.ambiente = @Amb)
   AND (@Loja::uuid IS NULL OR i.id_estabelecimento = @Loja)
   AND (@Provedor::text IS NULL OR i.provedor = @Provedor)
   AND (@De::timestamptz IS NULL OR i.created_at >= @De)
   AND (@Ate::timestamptz IS NULL OR i.created_at < @Ate)
   AND (NOT @SoAtencao OR pend.n > 0 OR av.n > 0 OR ei.id IS NOT NULL
        OR (i.provedor = 'infinitepay' AND i.status IN ('criado', 'aguardando_pagamento', 'processando') AND i.created_at < NOW() - make_interval(mins => @MinCobranca)))";

        private const string Campos = @"
SELECT i.id, i.id_estabelecimento AS estabelecimento_id, e.nome_fantasia AS loja_nome, i.provedor, i.origem, i.status, i.descricao, i.valor, i.valor_pago,
       i.valor_estornado, c.paid_amount_centavos,
       COALESCE(p.metodo, CASE c.capture_method WHEN 'pix' THEN 'pix' WHEN 'credit_card' THEN 'cartao' END) AS metodo,
       COALESCE(c.transaction_nsu, p.mp_payment_id::text) AS id_externo, c.handle_snapshot AS conta_recebedora, c.confirmado_em,
       COALESCE(i.pedido_id, w.id_pedido) AS pedido_id,
       EXISTS (SELECT 1 FROM gestao_acontecimentos a WHERE a.estabelecimento_id = i.id_estabelecimento AND a.tipo = 'venda' AND a.origem_tipo = 'pedido'
                  AND a.origem_id = COALESCE(i.pedido_id, w.id_pedido)::text AND a.corrige_id IS NULL) AS no_caixa,
       pend.n AS pendencias_abertas, av.n AS avisos_sem_verificacao, ei.valor AS estorno_informado_valor, i.pago_em, i.created_at AS criado_em";

        private const string SqlEstorno = @"
SELECT r.id, r.id_estabelecimento AS estabelecimento_id, e.nome_fantasia AS loja_nome, r.id_intent AS intent_id, c.order_nsu, r.valor, r.motivo, r.referencia_externa,
       r.status, r.evidencia, r.informado_por, r.created_at AS criado_em, r.resolvido_em
  FROM pagamento_estorno_informado r
  LEFT JOIN estabelecimentos e ON e.id = r.id_estabelecimento
  LEFT JOIN infinitepay_checkout c ON c.id_intent = r.id_intent";

        private const string SqlPendencia = @"
SELECT q.id, q.id_estabelecimento AS estabelecimento_id, e.nome_fantasia AS loja_nome, q.id_intent AS intent_id, c.order_nsu, q.tipo, q.status,
       q.created_at AS criado_em, q.resolvida_em
  FROM infinitepay_pendencia q
  LEFT JOIN estabelecimentos e ON e.id = q.id_estabelecimento
  LEFT JOIN infinitepay_checkout c ON c.id_intent = q.id_intent";

        private readonly NpgsqlDataSource _db;
        private readonly IMercadoPagoCredencialResolver _credenciais;
        private readonly IEnumerable<IPagamentoEfeitoHandler> _efeitos;
        private readonly IInfinitePayWebhookProcessor _processor;

        public RecebimentosOnlineService(NpgsqlDataSource db, IMercadoPagoCredencialResolver credenciais, IEnumerable<IPagamentoEfeitoHandler> efeitos,
            IInfinitePayWebhookProcessor processor)
        {
            _db = db;
            _credenciais = credenciais;
            _efeitos = efeitos;
            _processor = processor;
        }

        private static int Offset(int pagina) => Math.Max(0, pagina - 1) * TamanhoPagina;
        private static string? Limpo(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        private object Args(FiltroRecebimentos f, string? provedor, int offset, int limite, Guid? intent = null) => new
        {
            Amb = _credenciais.AmbienteAtivo, f.Loja, Provedor = provedor, De = f.De, Ate = f.Ate?.AddDays(1), f.SoAtencao,
            MinAviso = MinutosAvisoParado, MinCobranca = MinutosSemConfirmacao, Offset = offset, Limite = limite, Intent = intent
        };

        public async Task<RecebimentosDto> ListarAsync(FiltroRecebimentos f, CancellationToken ct = default)
        {
            var provedor = Limpo(f.Provedor);
            if (provedor != null && !ProvedoresPagamento.Valido(provedor)) throw PagamentoException.Invalida("Provedor desconhecido.", "provedor");
            if (f.De > f.Ate) throw PagamentoException.Invalida("Periodo invalido.", "de");
            await using var cx = await _db.OpenConnectionAsync(ct);
            var args = Args(f, provedor, Offset(f.Pagina), TamanhoPagina);
            var somas = (await cx.QueryAsync<SomaProvedorDto>(new CommandDefinition(@"
SELECT i.provedor, count(*)::int AS quantidade, COALESCE(sum(i.valor_pago), 0) AS pago, COALESCE(sum(i.valor_estornado), 0) AS estornado" + De + " GROUP BY i.provedor ORDER BY i.provedor",
                args, cancellationToken: ct))).ToList();
            var linhas = await cx.QueryAsync<LinhaRecebimento>(new CommandDefinition(
                Campos + De + " ORDER BY i.created_at DESC LIMIT @Limite OFFSET @Offset", args, cancellationToken: ct));
            return new RecebimentosDto { Somas = somas, Total = somas.Sum(s => s.Quantidade), Itens = linhas.Select(Montar).ToList() };
        }

        public async Task<AlertasRecebimentosDto> AlertasAsync(Guid? loja, CancellationToken ct = default)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            return await cx.QueryFirstAsync<AlertasRecebimentosDto>(new CommandDefinition(@"
SELECT (SELECT count(*)::int FROM infinitepay_webhook_evento v WHERE (@Loja::uuid IS NULL OR v.id_estabelecimento = @Loja) AND v.recebido_em > NOW() - INTERVAL '30 days' AND" + AvisoSemVerificacao + @") AS avisos_sem_verificacao,
       (SELECT count(*)::int FROM pagamento_intent i WHERE (@Loja::uuid IS NULL OR i.id_estabelecimento = @Loja) AND i.provedor = 'infinitepay' AND i.conta_tipo = 'loja'
           AND i.status IN ('criado', 'aguardando_pagamento', 'processando') AND i.created_at < NOW() - make_interval(mins => @MinCobranca)) AS cobrancas_sem_confirmacao,
       (SELECT count(*)::int FROM infinitepay_pendencia q WHERE (@Loja::uuid IS NULL OR q.id_estabelecimento = @Loja) AND q.status = 'aberta') AS pendencias_abertas,
       (SELECT count(*)::int FROM pagamento_estorno_informado r WHERE (@Loja::uuid IS NULL OR r.id_estabelecimento = @Loja) AND r.status = 'pendente_confirmacao') AS estornos_informados",
                new { Loja = loja, MinAviso = MinutosAvisoParado, MinCobranca = MinutosSemConfirmacao }, cancellationToken: ct));
        }

        public async Task<DiagnosticoRecebimentoDto> DiagnosticoAsync(Guid intentId, Guid? loja, CancellationToken ct = default)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            var linha = await cx.QueryFirstOrDefaultAsync<LinhaRecebimento>(new CommandDefinition(Campos + De + " AND i.id = @Intent",
                Args(new FiltroRecebimentos { Loja = loja }, null, 0, 1, intentId), cancellationToken: ct)) ?? throw PagamentoException.NaoEncontrado();
            var ck = await cx.QueryFirstOrDefaultAsync<(string? OrderNsu, string? Slug, string? Nsu, DateTime? Confirmado)>(new CommandDefinition(
                "SELECT order_nsu, invoice_slug, transaction_nsu, confirmado_em FROM infinitepay_checkout WHERE id_intent = @Id", new { Id = intentId }, cancellationToken: ct));
            var dono = new { Id = intentId, Loja = linha.EstabelecimentoId };
            var eventos = await cx.QueryAsync<EventoRow>(new CommandDefinition(@"
SELECT id, tipo_evento, status, resultado, ultimo_erro, transaction_nsu, tentativas, recebido_em, processado_em
  FROM infinitepay_webhook_evento WHERE id_intent = @Id AND id_estabelecimento = @Loja ORDER BY recebido_em DESC LIMIT 30", dono, cancellationToken: ct));
            return new DiagnosticoRecebimentoDto
            {
                Recebimento = Montar(linha), OrderNsu = ck.OrderNsu, Slug = ck.Slug, TransactionNsuMascarado = RecebimentosRegras.Mascarar(ck.Nsu), ConfirmadoEm = ck.Confirmado,
                Eventos = eventos.Select(v => new EventoDiagnosticoDto
                {
                    Id = v.Id, Tipo = v.TipoEvento, Status = v.Status, Resultado = RecebimentosRegras.ErroSeguro(v.Resultado), Erro = RecebimentosRegras.ErroSeguro(v.UltimoErro),
                    TransactionNsuMascarado = RecebimentosRegras.Mascarar(v.TransactionNsu), Tentativas = v.Tentativas, RecebidoEm = v.RecebidoEm, ProcessadoEm = v.ProcessadoEm
                }).ToList(),
                Pendencias = (await cx.QueryAsync<PendenciaDto>(new CommandDefinition(SqlPendencia + " WHERE q.id_intent = @Id AND q.id_estabelecimento = @Loja ORDER BY q.created_at DESC",
                    dono, cancellationToken: ct))).ToList(),
                Estornos = (await cx.QueryAsync<EstornoInformadoDto>(new CommandDefinition(SqlEstorno + " WHERE r.id_intent = @Id AND r.id_estabelecimento = @Loja ORDER BY r.created_at DESC",
                    dono, cancellationToken: ct))).ToList(),
                Historico = (await cx.QueryAsync<HistoricoRecebimentoDto>(new CommandDefinition(@"
SELECT acao, ator_tipo, created_at AS criado_em FROM pagamento_auditoria
 WHERE id_estabelecimento = @Loja AND entidade IN ('pagamento_intent', 'pagamento_estorno_informado')
   AND (entidade_id = @Texto OR entidade_id IN (SELECT id::text FROM pagamento_estorno_informado WHERE id_intent = @Id))
 ORDER BY created_at DESC LIMIT 50", new { dono.Id, dono.Loja, Texto = intentId.ToString() }, cancellationToken: ct))).ToList()
            };
        }

        public async Task<Pagina<PendenciaDto>> PendenciasAsync(Guid? loja, string? status, int pagina, CancellationToken ct = default)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            const string Filtro = " WHERE (@Loja::uuid IS NULL OR q.id_estabelecimento = @Loja) AND (@Status::text IS NULL OR q.status = @Status)";
            var args = new { Loja = loja, Status = Limpo(status), Offset = Offset(pagina), Limite = TamanhoPagina };
            return new Pagina<PendenciaDto>
            {
                Total = await cx.ExecuteScalarAsync<int>(new CommandDefinition("SELECT count(*)::int FROM infinitepay_pendencia q" + Filtro, args, cancellationToken: ct)),
                Itens = (await cx.QueryAsync<PendenciaDto>(new CommandDefinition(SqlPendencia + Filtro + " ORDER BY q.created_at DESC LIMIT @Limite OFFSET @Offset", args, cancellationToken: ct))).ToList()
            };
        }

        public async Task<Pagina<EstornoInformadoDto>> EstornosInformadosAsync(Guid? loja, string? status, int pagina, CancellationToken ct = default)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            const string Filtro = " WHERE (@Loja::uuid IS NULL OR r.id_estabelecimento = @Loja) AND (@Status::text IS NULL OR r.status = @Status)";
            var args = new { Loja = loja, Status = Limpo(status), Offset = Offset(pagina), Limite = TamanhoPagina };
            return new Pagina<EstornoInformadoDto>
            {
                Total = await cx.ExecuteScalarAsync<int>(new CommandDefinition("SELECT count(*)::int FROM pagamento_estorno_informado r" + Filtro, args, cancellationToken: ct)),
                Itens = (await cx.QueryAsync<EstornoInformadoDto>(new CommandDefinition(SqlEstorno + Filtro + " ORDER BY r.created_at DESC LIMIT @Limite OFFSET @Offset", args, cancellationToken: ct))).ToList()
            };
        }

        public async Task<EstornoInformadoDto> InformarEstornoAsync(Guid intentId, Guid loja, InformarEstornoRequest r, int usuario, CancellationToken ct = default)
        {
            RecebimentosRegras.ValidarInforme(r.Valor, r.Motivo, r.Referencia, r.IdempotencyKey);
            await using var cx = await _db.OpenConnectionAsync(ct);
            await using var tx = await cx.BeginTransactionAsync(ct);
            var intent = await PagamentoIntentSql.ObterAsync(cx, tx, intentId, true, ct) ?? throw PagamentoException.NaoEncontrado();
            if (intent.IdEstabelecimento != loja) throw PagamentoException.NaoEncontrado();

            var existente = await cx.ExecuteScalarAsync<Guid?>(new CommandDefinition(
                "SELECT id FROM pagamento_estorno_informado WHERE id_estabelecimento = @Loja AND idempotency_key = @K", new { Loja = loja, K = r.IdempotencyKey }, tx, cancellationToken: ct));
            if (existente is { } repetido)
            {
                await tx.CommitAsync(ct);
                return await EstornoAsync(cx, repetido, ct);
            }

            if (intent.Provedor != ProvedoresPagamento.InfinitePay)
                throw PagamentoException.Regra("PAG_RECURSO_NAO_SUPORTADO", "Este pagamento e devolvido pelo estorno do Mercado Pago.");
            if (!StatusIntent.FamiliaPaga(intent.Status)) throw PagamentoException.Estado("So da para informar estorno de pagamento confirmado.", intent.Status);
            if (r.Valor > intent.ValorPago - intent.ValorEstornado) throw PagamentoException.Regra(PagamentoCodigos.ValorInvalido, "O estorno passa do valor pago.");
            if (await cx.ExecuteScalarAsync<bool>(new CommandDefinition(
                    "SELECT EXISTS (SELECT 1 FROM pagamento_estorno_informado WHERE id_intent = @Id AND status = 'pendente_confirmacao')", new { intent.Id }, tx, cancellationToken: ct)))
                throw PagamentoException.Estado("Ja existe um estorno informado esperando confirmacao.", StatusEstornoInformado.Pendente);

            var id = Guid.NewGuid();
            await cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO pagamento_estorno_informado (id, id_estabelecimento, id_intent, provedor, valor, motivo, referencia_externa, idempotency_key, informado_por)
VALUES (@Id, @Loja, @Intent, @Provedor, @Valor, @Motivo, @Referencia, @K, @Usuario)", new
            {
                Id = id, Loja = loja, Intent = intent.Id, intent.Provedor, r.Valor, Motivo = r.Motivo!.Trim(), Referencia = Limpo(r.Referencia), K = r.IdempotencyKey, Usuario = usuario
            }, tx, cancellationToken: ct));
            await PagamentoAuditoria.RegistrarAsync(cx, tx, loja, "pagamento_estorno_informado", id.ToString(), "estorno_informado", "usuario", usuario,
                new { intentId = intent.Id, r.Valor }, ct);
            await tx.CommitAsync(ct);
            return await EstornoAsync(cx, id, ct);
        }

        public async Task<EstornoInformadoDto> DescartarEstornoAsync(Guid id, Guid? loja, int usuario, CancellationToken ct = default)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            await using var tx = await cx.BeginTransactionAsync(ct);
            var e = await TravarAsync(cx, tx, id, loja, ct);
            if (e.Status == StatusEstornoInformado.Pendente)
            {
                await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE pagamento_estorno_informado SET status = 'descartado', resolvido_por = @U, resolvido_em = NOW(), updated_at = NOW() WHERE id = @Id",
                    new { U = usuario, Id = id }, tx, cancellationToken: ct));
                await PagamentoAuditoria.RegistrarAsync(cx, tx, e.IdEstabelecimento, "pagamento_estorno_informado", id.ToString(), "estorno_descartado", "usuario", usuario, null, ct);
            }
            else if (e.Status != StatusEstornoInformado.Descartado) throw PagamentoException.Estado("Estorno ja confirmado nao pode ser descartado.", e.Status);
            await tx.CommitAsync(ct);
            return await EstornoAsync(cx, id, ct);
        }

        public async Task<EstornoInformadoDto> ConfirmarEstornoAsync(Guid id, ConfirmarEstornoRequest r, int usuario, CancellationToken ct = default)
        {
            RecebimentosRegras.ValidarEvidencia(r.Evidencia);
            await using var cx = await _db.OpenConnectionAsync(ct);
            await using var tx = await cx.BeginTransactionAsync(ct);
            // Mesma ordem do resto do fluxo: intencao antes da linha do estorno.
            var idIntent = await cx.ExecuteScalarAsync<Guid?>(new CommandDefinition("SELECT id_intent FROM pagamento_estorno_informado WHERE id = @Id", new { Id = id }, tx, cancellationToken: ct))
                           ?? throw PagamentoException.NaoEncontrado();
            var intent = await PagamentoIntentSql.ObterAsync(cx, tx, idIntent, true, ct) ?? throw PagamentoException.NaoEncontrado();
            var e = await TravarAsync(cx, tx, id, null, ct);

            if (e.Status == StatusEstornoInformado.Confirmado)
            {
                await tx.CommitAsync(ct);
                return await EstornoAsync(cx, id, ct);
            }
            if (e.Status != StatusEstornoInformado.Pendente) throw PagamentoException.Estado("Este estorno foi descartado.", e.Status);
            if (e.InformadoPor == usuario) throw PagamentoException.Estado("Quem informou o estorno nao pode confirmar.", e.Status);

            var (estornado, status) = RecebimentosRegras.AposConfirmar(intent.ValorPago, intent.ValorEstornado, e.Valor);
            if (!PagamentoStatusRules.PodeIntent(intent.Status, status)) throw PagamentoException.Estado("A cobranca nao aceita estorno neste estado.", intent.Status);

            var anterior = intent.Status;
            intent.Status = status;
            intent.ValorEstornado = estornado;
            intent.Versao++;
            await cx.ExecuteAsync(new CommandDefinition(
                "UPDATE pagamento_intent SET status = @Status, versao = @Versao, valor_estornado = @ValorEstornado, updated_at = NOW() WHERE id = @Id", intent, tx, cancellationToken: ct));
            await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE pagamento_estorno_informado SET status = 'confirmado', evidencia = @Evidencia, resolvido_por = @U, resolvido_em = NOW(), updated_at = NOW() WHERE id = @Id",
                new { Evidencia = r.Evidencia!.Trim(), U = usuario, Id = id }, tx, cancellationToken: ct));

            Guid? cobrancaId = null;
            foreach (var efeito in _efeitos.Where(x => x.Origem == intent.Origem))
                cobrancaId = await efeito.AplicarAsync(cx, tx, intent, anterior, "estorno_informado_confirmado", ct) ?? cobrancaId;
            // O reembolso do cardapio que nao tem como passar pela API fecha aqui; pagamento_estorno nao existe para a InfinitePay.
            await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE cardapio_pedido_reembolso SET status = @Feito, concluido_em = COALESCE(concluido_em, NOW()), updated_at = NOW()
 WHERE id = (SELECT id FROM cardapio_pedido_reembolso
              WHERE id_intent = @Intent AND valor = @Valor AND status IN (@Pendente, @Falhou) ORDER BY created_at LIMIT 1)",
                new { Feito = CardapioReembolsoStatus.Reembolsado, Intent = intent.Id, e.Valor, Pendente = CardapioReembolsoStatus.Pendente, Falhou = CardapioReembolsoStatus.Falhou },
                tx, cancellationToken: ct));
            await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE infinitepay_pendencia SET status = 'resolvida', resolvida_em = NOW(), resolvida_por = @U
 WHERE id_intent = @Intent AND tipo = @Tipo AND status = 'aberta'",
                new { U = usuario, Intent = intent.Id, Tipo = TiposPendenciaInfinitePay.EstornoNaoVerificavel }, tx, cancellationToken: ct));
            await PagamentoOutbox.PagamentoAtualizadoAsync(cx, tx, intent, cobrancaId, ct);
            await PagamentoAuditoria.RegistrarAsync(cx, tx, intent.IdEstabelecimento, "pagamento_estorno_informado", id.ToString(), "estorno_confirmado", "usuario", usuario,
                new { intentId = intent.Id, e.Valor, de = anterior, para = status }, ct);
            await tx.CommitAsync(ct);
            return await EstornoAsync(cx, id, ct);
        }

        public async Task ResolverPendenciaAsync(Guid id, ResolverPendenciaRequest r, int usuario, CancellationToken ct = default)
        {
            var observacao = Limpo(r.Observacao);
            if (observacao == null || observacao.Length > RecebimentosRegras.MaxMotivo) throw PagamentoException.Invalida("Diga o que foi conferido.", "observacao");
            await using var cx = await _db.OpenConnectionAsync(ct);
            await using var tx = await cx.BeginTransactionAsync(ct);
            var p = await cx.QueryFirstOrDefaultAsync<(Guid Loja, Guid Intent, string Tipo, string Status)?>(new CommandDefinition(
                "SELECT id_estabelecimento, id_intent, tipo, status FROM infinitepay_pendencia WHERE id = @Id FOR UPDATE", new { Id = id }, tx, cancellationToken: ct))
                ?? throw PagamentoException.NaoEncontrado();
            if (p.Status == "aberta")
            {
                await cx.ExecuteAsync(new CommandDefinition("UPDATE infinitepay_pendencia SET status = 'resolvida', resolvida_em = NOW(), resolvida_por = @U WHERE id = @Id",
                    new { U = usuario, Id = id }, tx, cancellationToken: ct));
                await PagamentoAuditoria.RegistrarAsync(cx, tx, p.Loja, "pagamento_intent", p.Intent.ToString(), "pendencia_resolvida", "usuario", usuario, new { p.Tipo, observacao }, ct);
            }
            await tx.CommitAsync(ct);
        }

        public async Task<bool> ReverificarPendenciaAsync(Guid id, int usuario, CancellationToken ct = default)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            var p = await cx.QueryFirstOrDefaultAsync<(Guid Loja, Guid Intent, Guid? Evento, string Tipo, string Status)?>(new CommandDefinition(
                "SELECT id_estabelecimento, id_intent, id_evento, tipo, status FROM infinitepay_pendencia WHERE id = @Id", new { Id = id }, cancellationToken: ct))
                ?? throw PagamentoException.NaoEncontrado();
            // So falha de conferencia vale repetir. O caminho continua sendo /payment_check; nada aqui marca pago.
            if (p.Status != "aberta" || p.Tipo != TiposPendenciaInfinitePay.VerificacaoFalhou || p.Evento is not { } evento)
                throw PagamentoException.Estado("Esta pendencia nao pode ser conferida de novo.", p.Status);
            var voltou = await _processor.ReenfileirarAsync(evento, p.Loja, ct);
            if (voltou)
            {
                await cx.ExecuteAsync(new CommandDefinition("UPDATE infinitepay_pendencia SET status = 'resolvida', resolvida_em = NOW(), resolvida_por = @U WHERE id = @Id",
                    new { U = usuario, Id = id }, cancellationToken: ct));
                await PagamentoAuditoria.RegistrarAsync(cx, null, p.Loja, "pagamento_intent", p.Intent.ToString(), "pendencia_reverificada", "usuario", usuario, new { eventoId = evento }, ct);
            }
            return voltou;
        }

        private static async Task<EstornoLinha> TravarAsync(NpgsqlConnection cx, NpgsqlTransaction tx, Guid id, Guid? loja, CancellationToken ct) =>
            await cx.QueryFirstOrDefaultAsync<EstornoLinha>(new CommandDefinition(@"
SELECT id_estabelecimento, valor, status, informado_por FROM pagamento_estorno_informado
 WHERE id = @Id AND (@Loja::uuid IS NULL OR id_estabelecimento = @Loja) FOR UPDATE", new { Id = id, Loja = loja }, tx, cancellationToken: ct))
            ?? throw PagamentoException.NaoEncontrado();

        private static Task<EstornoInformadoDto> EstornoAsync(NpgsqlConnection cx, Guid id, CancellationToken ct) =>
            cx.QueryFirstAsync<EstornoInformadoDto>(new CommandDefinition(SqlEstorno + " WHERE r.id = @Id", new { Id = id }, cancellationToken: ct));

        private static RecebimentoOnlineDto Montar(LinhaRecebimento l) => new()
        {
            Id = l.Id, EstabelecimentoId = l.EstabelecimentoId, LojaNome = l.LojaNome, Provedor = l.Provedor, Origem = l.Origem, Status = l.Status, Descricao = l.Descricao,
            Situacao = RecebimentosRegras.Situacao(l.Provedor, l.Status, l.ConfirmadoEm != null, l.EstornoInformadoValor != null, l.PendenciasAbertas, l.AvisosSemVerificacao),
            Valor = l.Valor, ValorPago = l.ValorPago, ValorEstornado = l.ValorEstornado, ValorComprovante = l.PaidAmountCentavos / 100m, Metodo = l.Metodo,
            IdExternoMascarado = RecebimentosRegras.Mascarar(l.IdExterno), ContaRecebedora = l.ContaRecebedora, PedidoId = l.PedidoId, NoCaixa = l.NoCaixa,
            PendenciasAbertas = l.PendenciasAbertas, AvisosSemVerificacao = l.AvisosSemVerificacao, EstornoInformadoValor = l.EstornoInformadoValor, PagoEm = l.PagoEm, CriadoEm = l.CriadoEm
        };

        private sealed class LinhaRecebimento
        {
            public Guid Id { get; set; }
            public Guid EstabelecimentoId { get; set; }
            public string? LojaNome { get; set; }
            public string Provedor { get; set; } = string.Empty;
            public string Origem { get; set; } = string.Empty;
            public string Status { get; set; } = string.Empty;
            public string Descricao { get; set; } = string.Empty;
            public decimal Valor { get; set; }
            public decimal ValorPago { get; set; }
            public decimal ValorEstornado { get; set; }
            public long? PaidAmountCentavos { get; set; }
            public string? Metodo { get; set; }
            public string? IdExterno { get; set; }
            public string? ContaRecebedora { get; set; }
            public DateTime? ConfirmadoEm { get; set; }
            public int? PedidoId { get; set; }
            public bool NoCaixa { get; set; }
            public int PendenciasAbertas { get; set; }
            public int AvisosSemVerificacao { get; set; }
            public decimal? EstornoInformadoValor { get; set; }
            public DateTime? PagoEm { get; set; }
            public DateTime CriadoEm { get; set; }
        }

        private sealed class EventoRow
        {
            public Guid Id { get; set; }
            public string? TipoEvento { get; set; }
            public string Status { get; set; } = string.Empty;
            public string? Resultado { get; set; }
            public string? UltimoErro { get; set; }
            public string? TransactionNsu { get; set; }
            public int Tentativas { get; set; }
            public DateTime RecebidoEm { get; set; }
            public DateTime? ProcessadoEm { get; set; }
        }

        private sealed class EstornoLinha
        {
            public Guid IdEstabelecimento { get; set; }
            public decimal Valor { get; set; }
            public string Status { get; set; } = string.Empty;
            public int InformadoPor { get; set; }
        }
    }
}
