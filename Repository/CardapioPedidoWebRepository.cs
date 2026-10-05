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
    /// Leituras e transicoes do pre-pedido do cardapio web. Sem a migration 20261002_03 as consultas
    /// respondem 503 MIGRATION_PENDING (o resto do cardapio segue funcionando).
    /// </summary>
    public sealed class CardapioPedidoWebRepository : ICardapioPedidoWebRepository
    {
        private const string Colunas = @"
id, id_estabelecimento, codigo, status, tipo_entrega, nome_cliente, telefone_cliente, email_cliente,
forma_pagamento, observacoes, subtotal_produtos, subtotal_adicionais, taxa_entrega, total,
itens_json::text AS itens_json, endereco_entrega_json::text AS endereco_entrega_json, status_pagamento,
created_at, updated_at, codigo_confirmacao, codigo_expira_em, canal_confirmacao, telefone_contato,
id_conversa, confirmado_em, aceito_em, recusado_em, motivo_recusa, id_pedido";

        private readonly NpgsqlDataSource _dataSource;

        public CardapioPedidoWebRepository(NpgsqlDataSource dataSource)
        {
            _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        }

        private static DeliveryDomainException MigrationPending() =>
            new(503, "MIGRATION_PENDING", "A confirmacao de pedidos do cardapio ainda nao foi habilitada neste ambiente (migration 20261002_03 pendente).");

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

        public async Task<bool> MarcarAguardandoAceiteAsync(Guid id, string telefoneContato, Guid conversaId) =>
            await GuardAsync(connection => connection.ExecuteAsync(@"
UPDATE cardapio_pedido_publico
   SET status = 'aguardando_aceite',
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
   SET status = 'aguardando_aceite',
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
   AND status = 'aguardando_aceite'
 ORDER BY confirmado_em ASC, created_at ASC
 LIMIT @Limite;", new { EstabelecimentoId = estabelecimentoId, Limite = Math.Clamp(limite, 1, 100) }))).ToList();

        public async Task<bool> MarcarAceitoAsync(Guid estabelecimentoId, Guid id, int? pedidoId) =>
            await GuardAsync(connection => connection.ExecuteAsync(@"
UPDATE cardapio_pedido_publico
   SET status = 'aceito', aceito_em = NOW(), id_pedido = @PedidoId, updated_at = NOW()
 WHERE id = @Id
   AND id_estabelecimento = @EstabelecimentoId
   AND status = 'aguardando_aceite';", new { Id = id, EstabelecimentoId = estabelecimentoId, PedidoId = pedidoId })) > 0;

        public async Task<bool> MarcarRecusadoAsync(Guid estabelecimentoId, Guid id, string? motivo) =>
            await GuardAsync(connection => connection.ExecuteAsync(@"
UPDATE cardapio_pedido_publico
   SET status = 'recusado', recusado_em = NOW(), motivo_recusa = @Motivo, updated_at = NOW()
 WHERE id = @Id
   AND id_estabelecimento = @EstabelecimentoId
   AND status = 'aguardando_aceite';", new { Id = id, EstabelecimentoId = estabelecimentoId, Motivo = motivo })) > 0;

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
 ORDER BY c.janela_24h_fim DESC NULLS LAST, c.data_ultima_entrada DESC NULLS LAST, c.data_ultima_mensagem DESC NULLS LAST
 LIMIT 1;", new { EstabelecimentoId = estabelecimentoId, Variantes = variantes.ToArray() });
                return row == null ? null : new ConversaPorTelefone(row.ConversaId, row.JanelaFim, row.UltimaEntrada);
            });
        }

        private sealed class ConversaRow
        {
            public Guid ConversaId { get; set; }
            public DateTimeOffset? JanelaFim { get; set; }
            public DateTimeOffset? UltimaEntrada { get; set; }
        }
    }
}
