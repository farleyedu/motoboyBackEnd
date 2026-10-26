using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Payments.MercadoPago;
using Dapper;
using Npgsql;

namespace APIBack.Payments.Chat
{
    /// <summary>SQL da cobranca do chat. Tudo filtrado pela loja: id de outra loja se comporta como inexistente.</summary>
    public static class CobrancaChatRepositorio
    {
        private const string SelectCobranca = @"
SELECT c.id, c.id_estabelecimento, c.conversa_id, c.id_intent, c.autor_usuario_id, ua.nome AS autor_nome, c.descricao, c.valor, c.moeda, c.tipo,
       c.pedido_id, c.pre_pedido_id, c.cliente_id, c.status, c.versao, c.validade_minutos, c.expira_em, c.enviado_em, c.id_mensagem, c.pago_em,
       c.cancelado_em, c.cancelado_por, uc.nome AS cancelado_por_nome, c.motivo, c.reemitida_de,
       (SELECT r.id FROM cobranca_chat r WHERE r.reemitida_de = c.id ORDER BY r.created_at DESC LIMIT 1) AS reemitida_por,
       c.idempotency_key, c.link_token_cifrado, COALESCE(i.valor_estornado, 0) AS valor_estornado, i.status AS status_intent,
       c.created_at, c.updated_at
  FROM cobranca_chat c
  LEFT JOIN pagamento_intent i ON i.id = c.id_intent
  LEFT JOIN usuario ua ON ua.id = c.autor_usuario_id
  LEFT JOIN usuario uc ON uc.id = c.cancelado_por";

        private const string SelectAprovacao = @"
SELECT a.id, a.id_estabelecimento, a.id_cobranca, c.conversa_id, a.acao, a.status, a.valor, c.valor AS valor_cobranca, c.descricao AS descricao_cobranca,
       a.pedido_id, a.pre_pedido_id, a.motivo, a.solicitado_por, us.nome AS solicitado_por_nome, a.decidido_por, ud.nome AS decidido_por_nome,
       a.decidido_em, a.motivo_decisao, a.idempotency_key, a.created_at
  FROM cobranca_chat_aprovacao a
  JOIN cobranca_chat c ON c.id = a.id_cobranca
  LEFT JOIN usuario us ON us.id = a.solicitado_por
  LEFT JOIN usuario ud ON ud.id = a.decidido_por";

        public static string Moeda(string? m) => string.IsNullOrEmpty(m) ? "BRL" : m;

        // ---- config ----------------------------------------------------------------------------------------------------------

        public static async Task<CobrancaChatConfigDto> ConfigAsync(NpgsqlConnection cx, Guid loja, CancellationToken ct) =>
            await cx.QueryFirstOrDefaultAsync<CobrancaChatConfigDto>(new CommandDefinition(@"
SELECT habilitada, permitir_avulsa, limite_sem_aprovacao, template_nome, template_idioma, confirmar_pagamento_cliente
  FROM cobranca_chat_config WHERE id_estabelecimento = @Loja", new { Loja = loja }, cancellationToken: ct)) ?? new CobrancaChatConfigDto();

        public static Task SalvarConfigAsync(NpgsqlConnection cx, Guid loja, CobrancaChatConfigDto c, int usuario, CancellationToken ct) =>
            cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO cobranca_chat_config (id_estabelecimento, habilitada, permitir_avulsa, limite_sem_aprovacao, template_nome, template_idioma,
                                  confirmar_pagamento_cliente, atualizado_por)
VALUES (@Loja, @Habilitada, @PermitirAvulsa, @LimiteSemAprovacao, @TemplateNome, @TemplateIdioma, @ConfirmarPagamentoCliente, @U)
ON CONFLICT (id_estabelecimento) DO UPDATE SET habilitada = EXCLUDED.habilitada, permitir_avulsa = EXCLUDED.permitir_avulsa,
       limite_sem_aprovacao = EXCLUDED.limite_sem_aprovacao, template_nome = EXCLUDED.template_nome, template_idioma = EXCLUDED.template_idioma,
       confirmar_pagamento_cliente = EXCLUDED.confirmar_pagamento_cliente, atualizado_por = EXCLUDED.atualizado_por, updated_at = NOW()",
                new { Loja = loja, c.Habilitada, c.PermitirAvulsa, c.LimiteSemAprovacao, c.TemplateNome, c.TemplateIdioma, c.ConfirmarPagamentoCliente, U = usuario },
                cancellationToken: ct));

        /// <summary>Pix ligado na loja e conta MP viva no ambiente ativo (pre-checagem antes de gravar a cobranca).</summary>
        public static Task<bool> PixDisponivelAsync(NpgsqlConnection cx, Guid loja, string ambiente, CancellationToken ct) =>
            cx.ExecuteScalarAsync<bool>(new CommandDefinition(@"
SELECT COALESCE((SELECT pix_ativo FROM pagamento_preferencia_loja WHERE id_estabelecimento = @Loja), FALSE)
   AND EXISTS (SELECT 1 FROM mp_conta_loja WHERE id_estabelecimento = @Loja AND ambiente = @Amb AND estado IN ('ativa', 'expirada'))",
                new { Loja = loja, Amb = ambiente }, cancellationToken: ct));

        /// <summary>Conta que recebe a intencao ainda tem token (nao foi desconectada/revogada).</summary>
        public static Task<bool> ContaDaIntentVivaAsync(NpgsqlConnection cx, Guid intentId, CancellationToken ct) =>
            cx.ExecuteScalarAsync<bool>(new CommandDefinition(@"
SELECT EXISTS (SELECT 1 FROM pagamento_intent i JOIN mp_conta_loja m ON m.id = i.id_conta_loja
                WHERE i.id = @Id AND m.estado IN ('ativa', 'expirada'))", new { Id = intentId }, cancellationToken: ct));

        // ---- conversa e pedido -----------------------------------------------------------------------------------------------

        /// <summary>Conversa operacional da loja a partir do id da rota (pode ser o do grupo): a aberta mais recente do grupo.</summary>
        public static Task<ConversaCobranca?> ConversaAsync(NpgsqlConnection cx, NpgsqlTransaction? tx, Guid id, Guid loja, CancellationToken ct) =>
            cx.QueryFirstOrDefaultAsync<ConversaCobranca>(new CommandDefinition(@"
WITH alvo AS (SELECT COALESCE(id_conversa_grupo, id) AS grupo FROM conversas WHERE id = @Id AND id_estabelecimento = @Loja)
SELECT c.id, c.id_estabelecimento, c.id_cliente, COALESCE(c.id_conversa_grupo, c.id) AS id_grupo, c.id_canal, c.estado::text AS estado, c.janela_24h_fim
  FROM conversas c JOIN alvo a ON COALESCE(c.id_conversa_grupo, c.id) = a.grupo
 WHERE c.id_estabelecimento = @Loja
 ORDER BY CASE WHEN c.estado::text IN ('fechado_automaticamente', 'fechado_agente', 'arquivada') THEN 1 ELSE 0 END,
          CASE WHEN c.id = @Id THEN 0 ELSE 1 END,
          COALESCE(c.data_ultima_mensagem, c.data_atualizacao, c.data_criacao) DESC
 LIMIT 1", new { Id = id, Loja = loja }, tx, cancellationToken: ct));

        /// <summary>Ids das conversas do mesmo cliente (ou grupo) na loja: a lista e a conferencia de dono do pedido usam todas.</summary>
        public static async Task<Guid[]> ConversasDoClienteAsync(NpgsqlConnection cx, NpgsqlTransaction? tx, ConversaCobranca c, CancellationToken ct) =>
            (await cx.QueryAsync<Guid>(new CommandDefinition(@"
SELECT id FROM conversas
 WHERE id_estabelecimento = @Loja AND (COALESCE(id_conversa_grupo, id) = @Grupo OR (CAST(@Cliente AS uuid) IS NOT NULL AND id_cliente = @Cliente))",
                new { Loja = c.IdEstabelecimento, Grupo = c.IdGrupo, Cliente = c.IdCliente }, tx, cancellationToken: ct))).ToArray();

        private const string ExpressaoTotal = @"CASE WHEN p.value::text ~ '^-?[0-9]+(\.[0-9]+)?$' THEN p.value::text::numeric END";

        public static async Task<AlvoCobranca?> PedidoAsync(NpgsqlConnection cx, NpgsqlTransaction? tx, int pedidoId, bool travar, CancellationToken ct)
        {
            var alvo = await cx.QueryFirstOrDefaultAsync<AlvoCobranca>(new CommandDefinition($@"
SELECT p.id AS pedido_id, p.id_estabelecimento, p.cliente_id, p.conversa_id, p.status_pedido::text AS status, p.status_pagamento::text AS status_pagamento,
       {ExpressaoTotal} AS total, p.id::text AS codigo
  FROM pedido p WHERE p.id = @Id{(travar ? " FOR UPDATE OF p" : "")}", new { Id = pedidoId }, tx, cancellationToken: ct));
            if (alvo != null) alvo.Recebido = await RecebidoAsync(cx, tx, alvo.IdEstabelecimento, alvo.PedidoId, null, ct);
            return alvo;
        }

        public static async Task<AlvoCobranca?> PrePedidoAsync(NpgsqlConnection cx, NpgsqlTransaction? tx, Guid id, CancellationToken ct)
        {
            var alvo = await cx.QueryFirstOrDefaultAsync<AlvoCobranca>(new CommandDefinition(@"
SELECT pp.id AS pre_pedido_id, pp.id_estabelecimento, pp.id_conversa AS conversa_id, pp.id_pedido AS pedido_id, pp.status,
       pp.status_pagamento, pp.total, pp.codigo, p.cliente_id
  FROM cardapio_pedido_publico pp LEFT JOIN pedido p ON p.id = pp.id_pedido
 WHERE pp.id = @Id", new { Id = id }, tx, cancellationToken: ct));
            if (alvo != null) alvo.Recebido = await RecebidoAsync(cx, tx, alvo.IdEstabelecimento, alvo.PedidoId, alvo.PrePedidoId, ct);
            return alvo;
        }

        /// <summary>Dinheiro que ja entrou (Pix/cartao de qualquer origem) para o pedido/pre-pedido, menos o estornado.</summary>
        public static Task<decimal> RecebidoAsync(NpgsqlConnection cx, NpgsqlTransaction? tx, Guid loja, int? pedidoId, Guid? prePedidoId, CancellationToken ct) =>
            cx.ExecuteScalarAsync<decimal>(new CommandDefinition(@"
SELECT COALESCE(SUM(valor_pago - valor_estornado), 0) FROM pagamento_intent
 WHERE id_estabelecimento = @Loja AND status IN ('pago', 'estornado_parcial', 'estornado', 'contestado')
   AND ((CAST(@Pedido AS int) IS NOT NULL AND pedido_id = @Pedido) OR (CAST(@Pre AS uuid) IS NOT NULL AND pre_pedido_id = @Pre))",
                new { Loja = loja, Pedido = pedidoId, Pre = prePedidoId }, tx, cancellationToken: ct));

        // ---- cobranca --------------------------------------------------------------------------------------------------------

        public static Task<CobrancaChatLinha?> ObterAsync(NpgsqlConnection cx, NpgsqlTransaction? tx, Guid id, Guid loja, bool travar, CancellationToken ct) =>
            cx.QueryFirstOrDefaultAsync<CobrancaChatLinha>(new CommandDefinition(
                SelectCobranca + $" WHERE c.id = @Id AND c.id_estabelecimento = @Loja{(travar ? " FOR UPDATE OF c" : "")}",
                new { Id = id, Loja = loja }, tx, cancellationToken: ct));

        public static Task<CobrancaChatLinha?> PorChaveAsync(NpgsqlConnection cx, NpgsqlTransaction? tx, Guid loja, string chave, CancellationToken ct) =>
            cx.QueryFirstOrDefaultAsync<CobrancaChatLinha>(new CommandDefinition(
                SelectCobranca + " WHERE c.id_estabelecimento = @Loja AND c.idempotency_key = @K", new { Loja = loja, K = chave }, tx, cancellationToken: ct));

        public static Task<CobrancaChatLinha?> PorIntentAsync(NpgsqlConnection cx, NpgsqlTransaction tx, Guid intentId, Guid loja, CancellationToken ct) =>
            cx.QueryFirstOrDefaultAsync<CobrancaChatLinha>(new CommandDefinition(
                SelectCobranca + " WHERE c.id_intent = @Id AND c.id_estabelecimento = @Loja", new { Id = intentId, Loja = loja }, tx, cancellationToken: ct));

        public static async Task<IReadOnlyList<CobrancaChatLinha>> ListarAsync(NpgsqlConnection cx, Guid loja, Guid[] conversas, string? status, CancellationToken ct) =>
            (await cx.QueryAsync<CobrancaChatLinha>(new CommandDefinition(SelectCobranca + @"
 WHERE c.id_estabelecimento = @Loja AND c.conversa_id = ANY(@Conversas) AND (CAST(@Status AS text) IS NULL OR c.status = @Status)
 ORDER BY c.created_at DESC LIMIT 200", new { Loja = loja, Conversas = conversas, Status = status }, cancellationToken: ct))).ToList();

        /// <summary>Cobranca aberta para o mesmo pedido/pre-pedido na loja (qualquer conversa).</summary>
        public static Task<Guid?> AbertaDoAlvoAsync(NpgsqlConnection cx, NpgsqlTransaction? tx, Guid loja, int? pedidoId, Guid? prePedidoId, CancellationToken ct) =>
            cx.ExecuteScalarAsync<Guid?>(new CommandDefinition(@"
SELECT id FROM cobranca_chat
 WHERE id_estabelecimento = @Loja AND status IN ('aguardando_aprovacao', 'criando', 'aguardando', 'enviado')
   AND ((CAST(@Pedido AS int) IS NOT NULL AND pedido_id = @Pedido) OR (CAST(@Pre AS uuid) IS NOT NULL AND pre_pedido_id = @Pre))
 LIMIT 1", new { Loja = loja, Pedido = pedidoId, Pre = prePedidoId }, tx, cancellationToken: ct));

        /// <summary>Insere a cobranca (status inicial), o historico, a outbox (cobrancaAtualizada) e o item da conversa. Chave repetida = null.</summary>
        public static async Task<bool> InserirAsync(NpgsqlConnection cx, NpgsqlTransaction tx, CobrancaChatLinha c, ConversaCobranca conversa, string? atorNome,
            CancellationToken ct)
        {
            var inserido = await cx.ExecuteScalarAsync<Guid?>(new CommandDefinition(@"
INSERT INTO cobranca_chat (id, id_estabelecimento, conversa_id, autor_usuario_id, descricao, valor, moeda, tipo, pedido_id, pre_pedido_id, cliente_id,
                           status, versao, validade_minutos, reemitida_de, idempotency_key)
VALUES (@Id, @IdEstabelecimento, @ConversaId, @AutorUsuarioId, @Descricao, @Valor, @Moeda, @Tipo, @PedidoId, @PrePedidoId, @ClienteId,
        @Status, 1, @ValidadeMinutos, @ReemitidaDe, @IdempotencyKey)
ON CONFLICT (id_estabelecimento, idempotency_key) DO NOTHING
RETURNING id", c, tx, cancellationToken: ct));
            if (!inserido.HasValue) return false;

            c.Versao = 1;
            await EventoAsync(cx, tx, c.Id, null, c.Status, "usuario", c.AutorUsuarioId, new { c.Valor, c.Tipo, c.PedidoId, c.PrePedidoId, c.ReemitidaDe }, ct);
            await PagamentoOutbox.CobrancaAtualizadaAsync(cx, tx, c.Registro(), ct);
            // O balao vive na conversa: um item por cobranca em conversa_eventos (vem no historico da conversa depois de recarregar).
            await cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO conversa_eventos (id, id_conversa, id_conversa_grupo, tipo, origem, ator_usuario_id, ator_nome, dados, data_criacao)
VALUES (@Id, @Conversa, @Grupo, 'cobranca_pix', 'api', @U, @Nome, CAST(@Dados AS jsonb), NOW())", new
            {
                Id = Guid.NewGuid(), Conversa = conversa.Id, Grupo = conversa.IdGrupo, U = c.AutorUsuarioId, Nome = atorNome,
                Dados = JsonSerializer.Serialize(new { cobrancaId = c.Id, valor = c.Valor, descricao = c.Descricao, tipo = c.Tipo, pedidoId = c.PedidoId, prePedidoId = c.PrePedidoId, reemitidaDe = c.ReemitidaDe })
            }, tx, cancellationToken: ct));
            return true;
        }

        public static Task EventoAsync(NpgsqlConnection cx, NpgsqlTransaction tx, Guid cobranca, string? anterior, string novo, string ator, int? usuario, object? detalhe,
            CancellationToken ct) =>
            cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO cobranca_chat_evento (id_cobranca, status_anterior, status_novo, ator_tipo, usuario_id, detalhe)
VALUES (@Id, @A, @N, @Ator, @U, CAST(@D AS jsonb))", new
            {
                Id = cobranca, A = anterior, N = novo, Ator = ator, U = usuario, D = detalhe == null ? null : JsonSerializer.Serialize(detalhe)
            }, tx, cancellationToken: ct));

        /// <summary>Registro sem troca de status (reenvio, troca de vinculo, falha): historico + versao + outbox.</summary>
        public static async Task MarcarAsync(NpgsqlConnection cx, NpgsqlTransaction tx, CobrancaChatLinha c, string ator, int? usuario, object detalhe, CancellationToken ct)
        {
            var r = c.Registro();
            await CobrancaChatSql.MudarStatusAsync(cx, tx, r, c.Status, ator, usuario, detalhe, ct);
            c.Versao = r.Versao;
        }

        public static async Task MudarStatusAsync(NpgsqlConnection cx, NpgsqlTransaction tx, CobrancaChatLinha c, string novo, string ator, int? usuario, object? detalhe,
            CancellationToken ct)
        {
            var r = c.Registro();
            await CobrancaChatSql.MudarStatusAsync(cx, tx, r, novo, ator, usuario, detalhe, ct);
            c.Status = r.Status;
            c.Versao = r.Versao;
        }

        public static async Task<IReadOnlyList<CobrancaHistoricoItemDto>> HistoricoAsync(NpgsqlConnection cx, Guid id, CancellationToken ct)
        {
            var linhas = await cx.QueryAsync<(string? Anterior, string Novo, string Ator, int? Usuario, string? Nome, string? Detalhe, DateTime Quando)>(
                new CommandDefinition(@"
SELECT e.status_anterior, e.status_novo, e.ator_tipo, e.usuario_id, u.nome, e.detalhe::text, e.created_at
  FROM cobranca_chat_evento e LEFT JOIN usuario u ON u.id = e.usuario_id
 WHERE e.id_cobranca = @Id ORDER BY e.id", new { Id = id }, cancellationToken: ct));
            return linhas.Select(l => new CobrancaHistoricoItemDto
            {
                StatusAnterior = l.Anterior, StatusNovo = l.Novo, AtorTipo = l.Ator,
                Usuario = l.Usuario.HasValue ? new UsuarioResumoDto { Id = l.Usuario.Value, Nome = l.Nome } : null,
                Detalhe = string.IsNullOrEmpty(l.Detalhe) ? null : JsonDocument.Parse(l.Detalhe).RootElement.Clone(),
                CriadoEm = DateTime.SpecifyKind(l.Quando, DateTimeKind.Utc)
            }).ToList();
        }

        public static Task<string?> NomeUsuarioAsync(NpgsqlConnection cx, int id, CancellationToken ct) =>
            cx.ExecuteScalarAsync<string?>(new CommandDefinition("SELECT nome FROM usuario WHERE id = @Id", new { Id = id }, cancellationToken: ct));

        // ---- aprovacao -------------------------------------------------------------------------------------------------------

        public static Task<CobrancaAprovacaoLinha?> AprovacaoAsync(NpgsqlConnection cx, NpgsqlTransaction? tx, Guid id, Guid loja, bool travar, CancellationToken ct) =>
            cx.QueryFirstOrDefaultAsync<CobrancaAprovacaoLinha>(new CommandDefinition(
                SelectAprovacao + $" WHERE a.id = @Id AND a.id_estabelecimento = @Loja{(travar ? " FOR UPDATE OF a" : "")}", new { Id = id, Loja = loja }, tx, cancellationToken: ct));

        public static Task<CobrancaAprovacaoLinha?> AprovacaoPendenteAsync(NpgsqlConnection cx, NpgsqlTransaction? tx, Guid cobranca, CancellationToken ct) =>
            cx.QueryFirstOrDefaultAsync<CobrancaAprovacaoLinha>(new CommandDefinition(
                SelectAprovacao + " WHERE a.id_cobranca = @C AND a.status = 'pendente' ORDER BY a.created_at LIMIT 1", new { C = cobranca }, tx, cancellationToken: ct));

        public static Task<CobrancaAprovacaoLinha?> AprovacaoPorChaveAsync(NpgsqlConnection cx, NpgsqlTransaction? tx, Guid loja, string chave, CancellationToken ct) =>
            cx.QueryFirstOrDefaultAsync<CobrancaAprovacaoLinha>(new CommandDefinition(
                SelectAprovacao + " WHERE a.id_estabelecimento = @Loja AND a.idempotency_key = @K", new { Loja = loja, K = chave }, tx, cancellationToken: ct));

        public static async Task<IReadOnlyList<CobrancaAprovacaoLinha>> AprovacoesAsync(NpgsqlConnection cx, Guid loja, string? status, CancellationToken ct) =>
            (await cx.QueryAsync<CobrancaAprovacaoLinha>(new CommandDefinition(SelectAprovacao + @"
 WHERE a.id_estabelecimento = @Loja AND (CAST(@S AS text) IS NULL OR a.status = @S) ORDER BY a.created_at DESC LIMIT 200",
                new { Loja = loja, S = status }, cancellationToken: ct))).ToList();

        /// <summary>Cria o pedido de aprovacao. Ja existe um pendente para a mesma cobranca e acao = devolve esse (null quando criou).</summary>
        public static async Task<Guid> InserirAprovacaoAsync(NpgsqlConnection cx, NpgsqlTransaction tx, Guid loja, Guid cobranca, string acao, decimal? valor,
            int? pedidoId, Guid? prePedidoId, string? motivo, int solicitante, string chave, CancellationToken ct)
        {
            var existente = await cx.ExecuteScalarAsync<Guid?>(new CommandDefinition(
                "SELECT id FROM cobranca_chat_aprovacao WHERE id_cobranca = @C AND acao = @A AND status = 'pendente'", new { C = cobranca, A = acao }, tx, cancellationToken: ct));
            if (existente.HasValue) return existente.Value;
            var id = Guid.NewGuid();
            await cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO cobranca_chat_aprovacao (id, id_estabelecimento, id_cobranca, acao, valor, pedido_id, pre_pedido_id, motivo, solicitado_por, idempotency_key)
VALUES (@Id, @Loja, @C, @A, @V, @P, @Pre, @M, @U, @K)", new
            {
                Id = id, Loja = loja, C = cobranca, A = acao, V = valor, P = pedidoId, Pre = prePedidoId, M = motivo, U = solicitante, K = chave
            }, tx, cancellationToken: ct));
            return id;
        }

        public static Task DecidirAprovacaoAsync(NpgsqlConnection cx, NpgsqlTransaction tx, Guid id, string status, int? usuario, string? motivo, object? resultado,
            CancellationToken ct) =>
            cx.ExecuteAsync(new CommandDefinition(@"
UPDATE cobranca_chat_aprovacao SET status = @S, decidido_por = COALESCE(@U, decidido_por), decidido_em = COALESCE(decidido_em, NOW()),
       motivo_decisao = COALESCE(@M, motivo_decisao), resultado = COALESCE(CAST(@R AS jsonb), resultado), updated_at = NOW()
 WHERE id = @Id", new { Id = id, S = status, U = usuario, M = motivo, R = resultado == null ? null : JsonSerializer.Serialize(resultado) }, tx, cancellationToken: ct));

        // ---- tarefas ---------------------------------------------------------------------------------------------------------

        public static Task AgendarTarefaAsync(NpgsqlConnection cx, NpgsqlTransaction tx, Guid cobranca, Guid loja, string tipo, CancellationToken ct) =>
            cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO cobranca_chat_tarefa (id_cobranca, id_estabelecimento, tipo) VALUES (@C, @Loja, @T)
ON CONFLICT (id_cobranca, tipo) DO NOTHING", new { C = cobranca, Loja = loja, T = tipo }, tx, cancellationToken: ct));
    }
}
