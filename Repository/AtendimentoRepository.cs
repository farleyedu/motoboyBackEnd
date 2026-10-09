using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using APIBack.DTOs.Atendimento;
using APIBack.Hubs;
using APIBack.Repository.Interface;
using APIBack.Service;
using Dapper;
using Npgsql;

namespace APIBack.Repository
{
    /// <summary>
    /// Fase 5. Leituras toleram o banco sem a migration 20260927_03 (devolvem os padroes); escritas
    /// respondem 503 MIGRATION_PENDING. Nada aqui altera as tabelas de conversa/mensagem do WhatsApp.
    /// </summary>
    public sealed class AtendimentoRepository : IAtendimentoRepository
    {
        private const string LocalTimeZone = "America/Sao_Paulo";
        private readonly NpgsqlDataSource _dataSource;
        private readonly TimeProvider _clock;

        public AtendimentoRepository(NpgsqlDataSource dataSource, TimeProvider? clock = null)
        {
            _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
            _clock = clock ?? TimeProvider.System;
        }

        private static bool IsMissingTable(PostgresException ex) => ex.SqlState == PostgresErrorCodes.UndefinedTable;

        private static DeliveryDomainException MigrationPending() =>
            new(503, "MIGRATION_PENDING", "O atendimento ainda nao foi habilitado neste ambiente (migration 20260927_03 pendente).");

        // =====================================================================
        // Configuracao
        // =====================================================================

        private sealed class ConfigRow
        {
            public string Modo { get; set; } = AtendimentoModos.Humano;
            public string? SaudacaoHumano { get; set; }
            public string? MensagemForaHorario { get; set; }
            public string? Mensagens { get; set; }
            public DateTimeOffset UpdatedAtUtc { get; set; }
        }

        private sealed class HorarioRow
        {
            public int DiaSemana { get; set; }
            public bool Fechado { get; set; }
            public TimeSpan? AbreAs { get; set; }
            public TimeSpan? FechaAs { get; set; }
        }

        /// <summary>
        /// Horario de funcionamento da loja: unica fonte (tela Negocio e identidade, tabela estabelecimento_horario), usada pelo
        /// cardapio web, pelo bot e pela tela de Atendimento. Nulo = loja sem horario cadastrado (sempre aberta); sem nenhum dia aberto = fechada.
        /// </summary>
        private async Task<HorarioAtendimentoDto?> LerHorarioDeFuncionamentoAsync(Guid estabelecimentoId, DateOnly hoje)
        {
            try
            {
                await using var connection = await _dataSource.OpenConnectionAsync();
                var rows = (await connection.QueryAsync<HorarioRow>(@"
SELECT dia_semana AS DiaSemana, fechado AS Fechado, abre_as AS AbreAs, fecha_as AS FechaAs
  FROM estabelecimento_horario WHERE estabelecimento_id = @EstabelecimentoId;",
                    new { EstabelecimentoId = estabelecimentoId })).ToList();
                var especiais = new List<HorarioAtendimentoEspecialDto>();
                try
                {
                    especiais = (await connection.QueryAsync<HorarioAtendimentoEspecialDto>(@"
SELECT data AS Data, fechado AS Fechado, LEFT(abre_as::text, 5) AS Abre, LEFT(fecha_as::text, 5) AS Fecha
FROM estabelecimento_horario_especial WHERE estabelecimento_id = @EstabelecimentoId
  AND data BETWEEN @Hoje::date - 1 AND @Hoje::date + 7;",
                        new { EstabelecimentoId = estabelecimentoId, Hoje = hoje })).ToList();
                }
                catch (PostgresException ex) when (IsMissingTable(ex)) { /* Ambiente anterior às exceções: preserva a semana. */ }
                if (rows.Count == 0 && especiais.Count == 0) return null;

                // Negocio guarda 0 = segunda; a regra de abertura usa 0 = domingo.
                return new HorarioAtendimentoDto
                {
                    SemHorarioSemanal = rows.Count == 0,
                    Especiais = especiais,
                    Dias = rows
                        .Where(r => !r.Fechado && r.AbreAs.HasValue && r.FechaAs.HasValue)
                        .Select(r => new HorarioDiaDto
                        {
                            Dia = (r.DiaSemana + 1) % 7,
                            Abre = r.AbreAs!.Value.ToString(@"hh\:mm"),
                            Fecha = r.FechaAs!.Value.ToString(@"hh\:mm")
                        })
                        .OrderBy(d => d.Dia)
                        .ToList()
                };
            }
            catch (PostgresException ex) when (IsMissingTable(ex))
            {
                return null;
            }
        }

        public async Task<AtendimentoConfigDto> GetConfigAsync(Guid estabelecimentoId)
        {
            var config = new AtendimentoConfigDto { EstabelecimentoId = estabelecimentoId, IsDefault = true };
            try
            {
                await using var connection = await _dataSource.OpenConnectionAsync();
                var row = await connection.QuerySingleOrDefaultAsync<ConfigRow>(@"
SELECT modo AS Modo, saudacao_humano AS SaudacaoHumano, mensagem_fora_horario AS MensagemForaHorario,
       COALESCE(mensagens, '{}'::jsonb)::text AS Mensagens,
       updated_at_utc AS UpdatedAtUtc
  FROM estabelecimento_atendimento_config WHERE estabelecimento_id = @EstabelecimentoId;",
                    new { EstabelecimentoId = estabelecimentoId });
                if (row != null)
                {
                    config.Modo = row.Modo;
                    config.SaudacaoHumano = row.SaudacaoHumano;
                    config.MensagemForaHorario = row.MensagemForaHorario;
                    config.Mensagens = string.IsNullOrWhiteSpace(row.Mensagens)
                        ? new Dictionary<string, string>()
                        : JsonSerializer.Deserialize<Dictionary<string, string>>(row.Mensagens, JsonOptions) ?? new Dictionary<string, string>();
                    config.UpdatedAtUtc = row.UpdatedAtUtc;
                    config.IsDefault = false;
                }
            }
            catch (PostgresException ex) when (IsMissingTable(ex))
            {
                // sem a migration: valem os padroes (modo humano, sempre aberto)
            }

            var localNow = AtendimentoConfigRules.ParaHorarioLocal(_clock.GetUtcNow().UtcDateTime);
            config.HorarioAtendimento = await LerHorarioDeFuncionamentoAsync(estabelecimentoId, DateOnly.FromDateTime(localNow));
            config.AbertoAgora = AtendimentoConfigRules.IsOpen(config.HorarioAtendimento, localNow);
            return config;
        }

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        public async Task<AtendimentoConfigDto> UpsertConfigAsync(Guid estabelecimentoId, int actorUserId, UpdateAtendimentoConfigRequest request)
        {
            try
            {
                await using var connection = await _dataSource.OpenConnectionAsync();
                await connection.ExecuteAsync(@"
INSERT INTO estabelecimento_atendimento_config
    (estabelecimento_id, modo, saudacao_humano, mensagem_fora_horario, mensagens, updated_by_user_id, updated_at_utc)
VALUES (@EstabelecimentoId, @Modo, @Saudacao, @ForaHorario, @Mensagens::jsonb, @ActorUserId, NOW())
ON CONFLICT (estabelecimento_id) DO UPDATE SET
    modo = EXCLUDED.modo,
    saudacao_humano = EXCLUDED.saudacao_humano,
    mensagem_fora_horario = EXCLUDED.mensagem_fora_horario,
    mensagens = EXCLUDED.mensagens,
    updated_by_user_id = EXCLUDED.updated_by_user_id,
    updated_at_utc = NOW();",
                    new
                    {
                        EstabelecimentoId = estabelecimentoId,
                        request.Modo,
                        Saudacao = request.SaudacaoHumano,
                        ForaHorario = request.MensagemForaHorario,
                        Mensagens = JsonSerializer.Serialize(request.Mensagens ?? new Dictionary<string, string>(), JsonOptions),
                        ActorUserId = actorUserId
                    });
            }
            catch (PostgresException ex) when (IsMissingTable(ex))
            {
                throw MigrationPending();
            }
            return await GetConfigAsync(estabelecimentoId);
        }

        // =====================================================================
        // Respostas rapidas
        // =====================================================================

        public async Task<IReadOnlyList<RespostaRapidaDto>> ListRespostasAsync(Guid estabelecimentoId, bool onlyActive)
        {
            try
            {
                await using var connection = await _dataSource.OpenConnectionAsync();
                return (await connection.QueryAsync<RespostaRapidaDto>(@"
SELECT id AS Id, titulo AS Titulo, atalho AS Atalho, texto AS Texto, ordem AS Ordem, ativo AS Ativo
  FROM atendimento_respostas_rapidas
 WHERE estabelecimento_id = @EstabelecimentoId AND (@OnlyActive = FALSE OR ativo = TRUE)
 ORDER BY ordem, titulo;", new { EstabelecimentoId = estabelecimentoId, OnlyActive = onlyActive })).ToList();
            }
            catch (PostgresException ex) when (IsMissingTable(ex))
            {
                return Array.Empty<RespostaRapidaDto>();
            }
        }

        public async Task<RespostaRapidaDto> CreateRespostaAsync(Guid estabelecimentoId, SalvarRespostaRapidaRequest request)
        {
            try
            {
                await using var connection = await _dataSource.OpenConnectionAsync();
                await EnsureUniqueShortcutAsync(connection, estabelecimentoId, request.Atalho, null);
                var id = Guid.NewGuid();
                await connection.ExecuteAsync(@"
INSERT INTO atendimento_respostas_rapidas (id, estabelecimento_id, titulo, atalho, texto, ordem, ativo)
VALUES (@Id, @EstabelecimentoId, @Titulo, @Atalho, @Texto, @Ordem, @Ativo);",
                    new { Id = id, EstabelecimentoId = estabelecimentoId, request.Titulo, request.Atalho, request.Texto, request.Ordem, request.Ativo });
                return new RespostaRapidaDto { Id = id, Titulo = request.Titulo!, Atalho = request.Atalho, Texto = request.Texto!, Ordem = request.Ordem, Ativo = request.Ativo };
            }
            catch (PostgresException ex) when (IsMissingTable(ex))
            {
                throw MigrationPending();
            }
        }

        public async Task<RespostaRapidaDto?> UpdateRespostaAsync(Guid estabelecimentoId, Guid id, SalvarRespostaRapidaRequest request)
        {
            try
            {
                await using var connection = await _dataSource.OpenConnectionAsync();
                await EnsureUniqueShortcutAsync(connection, estabelecimentoId, request.Atalho, id);
                var changed = await connection.ExecuteAsync(@"
UPDATE atendimento_respostas_rapidas
   SET titulo = @Titulo, atalho = @Atalho, texto = @Texto, ordem = @Ordem, ativo = @Ativo, updated_at_utc = NOW()
 WHERE id = @Id AND estabelecimento_id = @EstabelecimentoId;",
                    new { Id = id, EstabelecimentoId = estabelecimentoId, request.Titulo, request.Atalho, request.Texto, request.Ordem, request.Ativo });
                return changed == 0
                    ? null
                    : new RespostaRapidaDto { Id = id, Titulo = request.Titulo!, Atalho = request.Atalho, Texto = request.Texto!, Ordem = request.Ordem, Ativo = request.Ativo };
            }
            catch (PostgresException ex) when (IsMissingTable(ex))
            {
                throw MigrationPending();
            }
        }

        public async Task<bool> DeleteRespostaAsync(Guid estabelecimentoId, Guid id)
        {
            try
            {
                await using var connection = await _dataSource.OpenConnectionAsync();
                return await connection.ExecuteAsync(
                    "DELETE FROM atendimento_respostas_rapidas WHERE id = @Id AND estabelecimento_id = @EstabelecimentoId;",
                    new { Id = id, EstabelecimentoId = estabelecimentoId }) > 0;
            }
            catch (PostgresException ex) when (IsMissingTable(ex))
            {
                throw MigrationPending();
            }
        }

        private static async Task EnsureUniqueShortcutAsync(NpgsqlConnection connection, Guid estabelecimentoId, string? atalho, Guid? exceptId)
        {
            if (atalho == null) return;
            var exists = await connection.ExecuteScalarAsync<bool>(@"
SELECT EXISTS (SELECT 1 FROM atendimento_respostas_rapidas
                WHERE estabelecimento_id = @EstabelecimentoId AND atalho = @Atalho AND (@ExceptId IS NULL OR id <> @ExceptId));",
                new { EstabelecimentoId = estabelecimentoId, Atalho = atalho, ExceptId = exceptId });
            if (exists) throw new DeliveryDomainException(409, "SHORTCUT_IN_USE", "Ja existe uma resposta rapida com este atalho.");
        }

        // =====================================================================
        // Variaveis do pedido (para renderizar respostas)
        // =====================================================================

        private sealed class PedidoVarsRow
        {
            public int Id { get; set; }
            public string? NomeCliente { get; set; }
            public string? MotoboyNome { get; set; }
            public string? PrevisaoRaw { get; set; }
            public decimal? Total { get; set; }
            public string? Loja { get; set; }
            public string? Items { get; set; }
            public string? EnderecoEntrega { get; set; }
        }

        public async Task<IReadOnlyDictionary<string, string?>> GetPedidoVariablesAsync(Guid estabelecimentoId, int? pedidoId)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            var loja = await connection.ExecuteScalarAsync<string?>(
                "SELECT nome_fantasia::text FROM estabelecimentos WHERE id = @Id;", new { Id = estabelecimentoId });
            var values = new Dictionary<string, string?> { ["loja"] = loja };
            if (!pedidoId.HasValue) return values;

            const string numeric = @"'^-?[0-9]+(\.[0-9]+)?$'";
            var row = await connection.QuerySingleOrDefaultAsync<PedidoVarsRow>($@"
SELECT p.id AS Id, p.nome_cliente::text AS NomeCliente, m.nome::text AS MotoboyNome,
       p.previsao_entrega::text AS PrevisaoRaw,
       CASE WHEN p.value::text ~ {numeric} THEN p.value::text::NUMERIC END AS Total,
       p.items::text AS Items, p.endereco_entrega::text AS EnderecoEntrega
  FROM pedido p LEFT JOIN motoboy m ON m.id = p.motoboy_responsavel
 WHERE p.id = @PedidoId AND p.id_estabelecimento = @EstabelecimentoId;",
                new { PedidoId = pedidoId.Value, EstabelecimentoId = estabelecimentoId })
                ?? throw new DeliveryDomainException(404, "PEDIDO_NOT_FOUND", "Pedido nao encontrado neste estabelecimento.");

            var previsao = DeliveryRules.ParseStoredDateTime(row.PrevisaoRaw);
            values["numero"] = row.Id.ToString();
            values["cliente"] = row.NomeCliente;
            values["motoboy"] = row.MotoboyNome;
            values["previsao"] = QuickReplyRenderer.FormatTime(previsao);
            values["total"] = QuickReplyRenderer.FormatMoney(row.Total);
            values["itens"] = row.Items;
            values["endereco"] = row.EnderecoEntrega;
            return values;
        }

        // =====================================================================
        // Conversa <-> pedido
        // =====================================================================

        private sealed class ConversaRow
        {
            public Guid Id { get; set; }
            public string? Estado { get; set; }
            public string? StatusAtendimento { get; set; }
            public int QtdNaoLidas { get; set; }
            public string? ClienteNome { get; set; }
            public string? Telefone { get; set; }
            public DateTimeOffset? JanelaFim { get; set; }
        }

        private static ConversaDoPedidoDto ToDto(ConversaRow row) => new()
        {
            ConversaId = row.Id,
            Estado = row.Estado,
            StatusAtendimento = row.StatusAtendimento,
            QtdNaoLidas = row.QtdNaoLidas,
            ClienteNome = row.ClienteNome,
            Telefone = row.Telefone,
            JanelaFimUtc = row.JanelaFim,
            JanelaAberta = row.JanelaFim.HasValue && row.JanelaFim.Value > DateTimeOffset.UtcNow
        };

        private sealed class PedidoContatoRow
        {
            public string? Telefone { get; set; }
            public string? Nome { get; set; }
            public Guid? ConversaId { get; set; }
        }

        private const string ConversaSelect = @"
SELECT c.id AS Id, c.estado::text AS Estado, c.status_atendimento AS StatusAtendimento,
       COALESCE(c.qtd_nao_lidas, 0) AS QtdNaoLidas, cl.nome::text AS ClienteNome,
       cl.telefone_e164 AS Telefone, c.janela_24h_fim AS JanelaFim
  FROM conversas c
  JOIN clientes cl ON cl.id = c.id_cliente";

        /// <summary>Conversa ligada ao pedido (pela coluna) ou, sem ela, a conversa do mesmo telefone mais recente.</summary>
        public async Task<ConversaDoPedidoDto> GetConversaDoPedidoAsync(Guid estabelecimentoId, int pedidoId)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            return await FindConversaAsync(connection, estabelecimentoId, pedidoId) ?? new ConversaDoPedidoDto();
        }

        private static async Task<ConversaDoPedidoDto?> FindConversaAsync(NpgsqlConnection connection, Guid estabelecimentoId, int pedidoId)
        {
            var core = await PedidoColumnTypes.HasCoreSchemaAsync(connection, null);
            var pedido = await connection.QuerySingleOrDefaultAsync<PedidoContatoRow>($@"
SELECT p.telefone_cliente::text AS Telefone, p.nome_cliente::text AS Nome, {(core ? "p.conversa_id" : "NULL::uuid")} AS ConversaId
  FROM pedido p WHERE p.id = @PedidoId AND p.id_estabelecimento = @EstabelecimentoId;",
                new { PedidoId = pedidoId, EstabelecimentoId = estabelecimentoId })
                ?? throw new DeliveryDomainException(404, "PEDIDO_NOT_FOUND", "Pedido nao encontrado neste estabelecimento.");

            var key = PhoneKey.From(pedido.Telefone);
            var row = await connection.QueryFirstOrDefaultAsync<ConversaRow>($@"{ConversaSelect}
 WHERE c.id_estabelecimento = @EstabelecimentoId
   AND (c.id = @ConversaId OR (@Key IS NOT NULL AND RIGHT(regexp_replace(cl.telefone_e164, '\D', '', 'g'), 11) = @Key))
 ORDER BY (c.id = @ConversaId) DESC, c.data_ultima_mensagem DESC NULLS LAST
 LIMIT 1;", new { EstabelecimentoId = estabelecimentoId, ConversaId = pedido.ConversaId, Key = key });
            return row == null ? null : ToDto(row);
        }

        private sealed class PedidoCanalRow
        {
            public string? Nome { get; set; }
            public string? Origem { get; set; }
        }

        public async Task<PedidoCanalDto> GetCanalDoPedidoAsync(Guid estabelecimentoId, int pedidoId)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            var core = await PedidoColumnTypes.HasCoreSchemaAsync(connection, null);
            // Pedido antigo (antes do nucleo) nao tem a coluna origem: la, quem tem id_ifood e do iFood.
            var origemExpr = core ? "p.origem::text" : "CASE WHEN p.id_ifood IS NOT NULL THEN 'ifood' ELSE 'atendente' END";
            var pedido = await connection.QuerySingleOrDefaultAsync<PedidoCanalRow>($@"
SELECT p.nome_cliente::text AS Nome, {origemExpr} AS Origem
  FROM pedido p WHERE p.id = @PedidoId AND p.id_estabelecimento = @EstabelecimentoId;",
                new { PedidoId = pedidoId, EstabelecimentoId = estabelecimentoId })
                ?? throw new DeliveryDomainException(404, "PEDIDO_NOT_FOUND", "Pedido nao encontrado neste estabelecimento.");

            var conversa = await FindConversaAsync(connection, estabelecimentoId, pedidoId);
            var (podeReceber, motivo) = ClienteCanalRules.Evaluate(pedido.Origem, conversa?.ConversaId, conversa?.JanelaFimUtc, DateTimeOffset.UtcNow);
            return new PedidoCanalDto
            {
                PedidoId = pedidoId,
                Origem = pedido.Origem,
                ClienteNome = string.IsNullOrWhiteSpace(pedido.Nome) ? conversa?.ClienteNome : pedido.Nome,
                ConversaId = conversa?.ConversaId,
                JanelaFimUtc = conversa?.JanelaFimUtc,
                PodeReceber = podeReceber,
                Motivo = motivo
            };
        }

        public async Task<IReadOnlyList<PedidoEntregueConversaDto>> GetPedidosEntreguesComConversaAbertaAsync()
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            if (!await PedidoColumnTypes.HasCoreSchemaAsync(connection, null)) return Array.Empty<PedidoEntregueConversaDto>();
            // Sem filtro por horario_entrega: a coluna e texto em algumas instalacoes (ver LocalNowSqlAsync), entao
            // nao da pra comparar com seguranca em SQL. O filtro por conversa ainda aberta ja encolhe a lista
            // sozinho (conversa fechada sai do resultado); o limite so protege o pior caso de um backlog grande.
            var rows = await connection.QueryAsync<PedidoEntregueConversaDto>(@"
SELECT p.id AS PedidoId, p.id_estabelecimento AS EstabelecimentoId, p.conversa_id AS ConversaId
  FROM pedido p
  JOIN conversas c ON c.id = p.conversa_id
 WHERE p.status_pedido = 3 -- Concluido
   AND p.conversa_id IS NOT NULL
   AND c.status_atendimento IN ('com_bot', 'em_andamento', 'aguardando_cliente', 'aguardando_interno')
 ORDER BY p.id DESC
 LIMIT 200;");
            return rows.ToList();
        }

        /// <summary>Liga o pedido a uma conversa (e ao cliente dela). Nao apaga vinculo anterior de outro pedido.</summary>
        public async Task<ConversaDoPedidoDto> VincularAsync(Guid estabelecimentoId, Guid conversaId, int pedidoId)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            if (!await PedidoColumnTypes.HasCoreSchemaAsync(connection, null)) throw MigrationPending();

            var row = await connection.QuerySingleOrDefaultAsync<ConversaRow>($@"{ConversaSelect}
 WHERE c.id = @ConversaId AND c.id_estabelecimento = @EstabelecimentoId;",
                new { ConversaId = conversaId, EstabelecimentoId = estabelecimentoId })
                ?? throw new DeliveryDomainException(404, "CONVERSA_NOT_FOUND", "Conversa nao encontrada neste estabelecimento.");
            var changed = await connection.ExecuteAsync(@"
UPDATE pedido SET conversa_id = @ConversaId,
                  cliente_id = COALESCE((SELECT id_cliente FROM conversas WHERE id = @ConversaId), cliente_id)
 WHERE id = @PedidoId AND id_estabelecimento = @EstabelecimentoId;",
                new { ConversaId = conversaId, PedidoId = pedidoId, EstabelecimentoId = estabelecimentoId });
            if (changed == 0) throw new DeliveryDomainException(404, "PEDIDO_NOT_FOUND", "Pedido nao encontrado neste estabelecimento.");
            return ToDto(row);
        }

        /// <summary>
        /// Abre a conversa do cliente do pedido: usa a existente ou cria cliente + conversa (aguardando atendente,
        /// sem janela de 24h: a primeira mensagem sera template). Liga o pedido a ela.
        /// </summary>
        public async Task<ConversaDoPedidoDto> AbrirConversaDoPedidoAsync(Guid estabelecimentoId, int pedidoId)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            var existing = await FindConversaAsync(connection, estabelecimentoId, pedidoId);
            if (existing?.ConversaId != null)
            {
                if (await PedidoColumnTypes.HasCoreSchemaAsync(connection, null))
                {
                    await connection.ExecuteAsync(
                        "UPDATE pedido SET conversa_id = @ConversaId WHERE id = @PedidoId AND id_estabelecimento = @EstabelecimentoId AND conversa_id IS NULL;",
                        new { ConversaId = existing.ConversaId, PedidoId = pedidoId, EstabelecimentoId = estabelecimentoId });
                }
                return existing;
            }

            var data = await connection.QuerySingleAsync<PedidoContatoRow>(
                "SELECT telefone_cliente::text AS Telefone, nome_cliente::text AS Nome FROM pedido WHERE id = @PedidoId AND id_estabelecimento = @EstabelecimentoId;",
                new { PedidoId = pedidoId, EstabelecimentoId = estabelecimentoId });
            var e164 = PhoneKey.ToE164(data.Telefone)
                ?? throw new DeliveryDomainException(422, "PEDIDO_SEM_TELEFONE", "O pedido nao tem um telefone valido para abrir a conversa.");

            await using var transaction = await connection.BeginTransactionAsync();
            var clienteId = await connection.ExecuteScalarAsync<Guid?>(
                "SELECT id FROM clientes WHERE id_estabelecimento = @EstabelecimentoId AND telefone_e164 = @Telefone LIMIT 1;",
                new { EstabelecimentoId = estabelecimentoId, Telefone = e164 }, transaction);
            if (!clienteId.HasValue)
            {
                clienteId = Guid.NewGuid();
                await connection.ExecuteAsync(@"
INSERT INTO clientes (id, id_estabelecimento, telefone_e164, nome, data_criacao, data_atualizacao)
VALUES (@Id, @EstabelecimentoId, @Telefone, @Nome, NOW(), NOW());",
                    new { Id = clienteId, EstabelecimentoId = estabelecimentoId, Telefone = e164, Nome = data.Nome }, transaction);
            }

            var conversaId = Guid.NewGuid();
            await connection.ExecuteAsync(@"
INSERT INTO conversas (id, id_conversa_grupo, id_estabelecimento, id_cliente, canal, estado, status_atendimento,
                       qtd_nao_lidas, data_criacao, data_atualizacao)
VALUES (@Id, @Id, @EstabelecimentoId, @ClienteId, 'whatsapp'::canal_chat_enum, 'em_atendimento'::estado_conversa_enum,
        'aguardando_interno', 0, NOW(), NOW());",
                new { Id = conversaId, EstabelecimentoId = estabelecimentoId, ClienteId = clienteId }, transaction);
            if (await PedidoColumnTypes.HasCoreSchemaAsync(connection, transaction))
            {
                await connection.ExecuteAsync(
                    "UPDATE pedido SET conversa_id = @ConversaId, cliente_id = @ClienteId WHERE id = @PedidoId AND id_estabelecimento = @EstabelecimentoId;",
                    new { ConversaId = conversaId, ClienteId = clienteId, PedidoId = pedidoId, EstabelecimentoId = estabelecimentoId }, transaction);
            }
            await transaction.CommitAsync();

            return new ConversaDoPedidoDto
            {
                ConversaId = conversaId,
                Estado = "em_atendimento",
                StatusAtendimento = "aguardando_interno",
                ClienteNome = data.Nome,
                Telefone = e164,
                Criada = true
            };
        }

        public async Task<Guid> EnsureConversaParaClienteAsync(Guid estabelecimentoId, Guid clienteId)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            var existing = await connection.ExecuteScalarAsync<Guid?>(
                "SELECT id FROM conversas WHERE id_cliente = @ClienteId AND id_estabelecimento = @EstabelecimentoId ORDER BY data_criacao LIMIT 1;",
                new { ClienteId = clienteId, EstabelecimentoId = estabelecimentoId });
            if (existing.HasValue) return existing.Value;

            var conversaId = Guid.NewGuid();
            await connection.ExecuteAsync(@"
INSERT INTO conversas (id, id_conversa_grupo, id_estabelecimento, id_cliente, canal, estado, status_atendimento,
                       qtd_nao_lidas, data_criacao, data_atualizacao)
VALUES (@Id, @Id, @EstabelecimentoId, @ClienteId, 'whatsapp'::canal_chat_enum, 'em_atendimento'::estado_conversa_enum,
        'aguardando_interno', 0, NOW(), NOW());",
                new { Id = conversaId, EstabelecimentoId = estabelecimentoId, ClienteId = clienteId });
            return conversaId;
        }

        // =====================================================================
        // Mensagens atendente <-> motoboy
        // =====================================================================

        private sealed class MessageRow
        {
            public long Id { get; set; }
            public int MotoboyId { get; set; }
            public int? PedidoId { get; set; }
            public string Direction { get; set; } = "operator";
            public string Body { get; set; } = string.Empty;
            public string? QuickKey { get; set; }
            public DateTimeOffset CreatedAtUtc { get; set; }
            public DateTimeOffset? ReadAtUtc { get; set; }
        }

        private const string MessageColumns = @"
       id AS Id, motoboy_id AS MotoboyId, pedido_id AS PedidoId, direction AS Direction, body AS Body,
       quick_key AS QuickKey, created_at_utc AS CreatedAtUtc, read_at_utc AS ReadAtUtc";

        public async Task<string?> ObterNomeMotoboyAsync(int motoboyId)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            return await connection.QuerySingleOrDefaultAsync<string?>(@"
SELECT nome FROM motoboy
 WHERE id = COALESCE((SELECT canonical_motoboy_id FROM motoboy WHERE id = @MotoboyId), @MotoboyId);",
                new { MotoboyId = motoboyId });
        }

        // =====================================================================
        // Lista de motoboys vinculados (contatos) e grupo da loja (Fase D/E)
        // =====================================================================

        public async Task<IReadOnlyList<MotoboyRosterEntryDto>> ListMotoboysVinculadosAsync(Guid estabelecimentoId)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            var rows = await connection.QueryAsync<MotoboyRosterEntryDto>(@"
SELECT m.id AS MotoboyId, m.nome AS Nome, m.avatar AS Avatar,
       EXISTS (
           SELECT 1 FROM motoboy_active_sessions s
            WHERE s.motoboy_id = m.id AND s.id_estabelecimento = @EstabelecimentoId
              AND s.ended_at_utc IS NULL AND s.expires_at_utc > NOW()
       ) AS Online
  FROM motoboy_estabelecimento me
  JOIN motoboy m ON m.id = me.motoboy_id
 WHERE me.estabelecimento_id = @EstabelecimentoId AND me.ativo = TRUE
 ORDER BY Online DESC, m.nome;",
                new { EstabelecimentoId = estabelecimentoId });
            return rows.ToList();
        }

        private sealed class GroupMessageRow
        {
            public long Id { get; set; }
            public string SenderType { get; set; } = "operator";
            public int? MotoboyId { get; set; }
            public string? MotoboyNome { get; set; }
            public string Body { get; set; } = string.Empty;
            public DateTimeOffset CreatedAtUtc { get; set; }
        }

        private const string GroupMessageSelect = @"
SELECT gm.id AS Id, gm.sender_type AS SenderType, gm.motoboy_id AS MotoboyId, m.nome::text AS MotoboyNome,
       gm.body AS Body, gm.created_at_utc AS CreatedAtUtc
  FROM motoboy_group_message gm
  LEFT JOIN motoboy m ON m.id = gm.motoboy_id";

        private static MotoboyGroupMessageDto ToGroupDto(GroupMessageRow row) => new()
        {
            Id = row.Id, SenderType = row.SenderType, MotoboyId = row.MotoboyId, MotoboyNome = row.MotoboyNome,
            Body = row.Body, CreatedAtUtc = row.CreatedAtUtc
        };

        public async Task<IReadOnlyList<MotoboyGroupMessageDto>> ListGroupMessagesAsync(Guid estabelecimentoId, int limit)
        {
            try
            {
                await using var connection = await _dataSource.OpenConnectionAsync();
                var rows = await connection.QueryAsync<GroupMessageRow>($@"{GroupMessageSelect}
 WHERE gm.estabelecimento_id = @EstabelecimentoId
 ORDER BY gm.created_at_utc DESC
 LIMIT @Limit;",
                    new { EstabelecimentoId = estabelecimentoId, Limit = limit });
                return rows.Select(ToGroupDto).Reverse().ToList();
            }
            catch (PostgresException ex) when (IsMissingTable(ex))
            {
                return Array.Empty<MotoboyGroupMessageDto>();
            }
        }

        public async Task<MotoboyGroupMessageDto> SendGroupMessageAsync(Guid estabelecimentoId, string senderType, int? motoboyId, int? sentByUserId, string body)
        {
            try
            {
                await using var connection = await _dataSource.OpenConnectionAsync();
                if (motoboyId.HasValue)
                {
                    var linked = await connection.ExecuteScalarAsync<bool>(@"
SELECT EXISTS (SELECT 1 FROM motoboy_estabelecimento me
                WHERE me.motoboy_id = @MotoboyId AND me.estabelecimento_id = @EstabelecimentoId AND me.ativo = TRUE);",
                        new { MotoboyId = motoboyId, EstabelecimentoId = estabelecimentoId });
                    if (!linked) throw new DeliveryDomainException(404, "MOTOBOY_NOT_FOUND", "Motoboy nao encontrado neste estabelecimento.");
                }

                var row = await connection.QuerySingleAsync<GroupMessageRow>($@"
WITH inserted AS (
    INSERT INTO motoboy_group_message (estabelecimento_id, sender_type, motoboy_id, sent_by_user_id, body)
    VALUES (@EstabelecimentoId, @SenderType, @MotoboyId, @SentByUserId, @Body)
    RETURNING id, estabelecimento_id, sender_type, motoboy_id, sent_by_user_id, body, created_at_utc
)
SELECT inserted.id AS Id, inserted.sender_type AS SenderType, inserted.motoboy_id AS MotoboyId, m.nome::text AS MotoboyNome,
       inserted.body AS Body, inserted.created_at_utc AS CreatedAtUtc
  FROM inserted
  LEFT JOIN motoboy m ON m.id = inserted.motoboy_id;",
                    new { EstabelecimentoId = estabelecimentoId, SenderType = senderType, MotoboyId = motoboyId, SentByUserId = sentByUserId, Body = body });
                return ToGroupDto(row);
            }
            catch (PostgresException ex) when (IsMissingTable(ex))
            {
                throw MigrationPending();
            }
        }

        private static MotoboyMessageDto ToDto(MessageRow row) => new()
        {
            Id = row.Id, MotoboyId = row.MotoboyId, PedidoId = row.PedidoId, Direction = row.Direction,
            Body = row.Body, QuickKey = row.QuickKey, CreatedAtUtc = row.CreatedAtUtc, ReadAtUtc = row.ReadAtUtc
        };

        public async Task<MotoboyMessageDto> SendMotoboyMessageAsync(
            Guid estabelecimentoId, int motoboyId, int? pedidoId, string direction, string body, string? quickKey, int? actorUserId)
        {
            try
            {
                await using var connection = await _dataSource.OpenConnectionAsync();
                await using var transaction = await connection.BeginTransactionAsync();

                var linked = await connection.ExecuteScalarAsync<bool>(@"
SELECT EXISTS (SELECT 1 FROM motoboy_estabelecimento me
                WHERE me.motoboy_id = @MotoboyId AND me.estabelecimento_id = @EstabelecimentoId AND me.ativo = TRUE);",
                    new { MotoboyId = motoboyId, EstabelecimentoId = estabelecimentoId }, transaction);
                if (!linked) throw new DeliveryDomainException(404, "MOTOBOY_NOT_FOUND", "Motoboy nao encontrado neste estabelecimento.");
                if (pedidoId.HasValue)
                {
                    var ok = await connection.ExecuteScalarAsync<bool>(
                        "SELECT EXISTS (SELECT 1 FROM pedido WHERE id = @PedidoId AND id_estabelecimento = @EstabelecimentoId);",
                        new { PedidoId = pedidoId, EstabelecimentoId = estabelecimentoId }, transaction);
                    if (!ok) throw new DeliveryDomainException(404, "PEDIDO_NOT_FOUND", "Pedido nao encontrado neste estabelecimento.");
                }

                var row = await connection.QuerySingleAsync<MessageRow>($@"
INSERT INTO delivery_motoboy_message (estabelecimento_id, motoboy_id, pedido_id, direction, body, quick_key, sent_by_user_id)
VALUES (@EstabelecimentoId, @MotoboyId, @PedidoId, @Direction, @Body, @QuickKey, @ActorUserId)
RETURNING {MessageColumns};",
                    new { EstabelecimentoId = estabelecimentoId, MotoboyId = motoboyId, PedidoId = pedidoId, Direction = direction, Body = body, QuickKey = quickKey, ActorUserId = actorUserId },
                    transaction);

                var sessionId = await connection.ExecuteScalarAsync<Guid?>(@"
SELECT s.session_id FROM motoboy_active_sessions s
 WHERE s.motoboy_id = @MotoboyId AND s.id_estabelecimento = @EstabelecimentoId
   AND s.ended_at_utc IS NULL AND s.expires_at_utc > NOW() LIMIT 1;",
                    new { MotoboyId = motoboyId, EstabelecimentoId = estabelecimentoId }, transaction);
                await EmitMessageEventAsync(connection, transaction, estabelecimentoId, row, sessionId);
                await transaction.CommitAsync();
                return ToDto(row);
            }
            catch (PostgresException ex) when (IsMissingTable(ex))
            {
                throw MigrationPending();
            }
        }

        private static async Task EmitMessageEventAsync(
            NpgsqlConnection connection, NpgsqlTransaction transaction, Guid estabelecimentoId, MessageRow row, Guid? sessionId)
        {
            var payload = JsonSerializer.Serialize(new
            {
                schemaVersion = 2,
                eventId = Guid.NewGuid(),
                estabelecimentoId,
                motoboyId = row.MotoboyId,
                pedidoId = row.PedidoId,
                messageId = row.Id,
                direction = row.Direction,
                body = row.Body,
                quickKey = row.QuickKey,
                occurredAtUtc = row.CreatedAtUtc
            });
            var targets = new List<string> { DeliveryRealtimeEvents.EstablishmentGroup(estabelecimentoId) };
            if (sessionId.HasValue) targets.Add(DeliveryRealtimeEvents.SessionGroup(sessionId.Value));
            foreach (var target in targets)
            {
                await connection.ExecuteAsync(@"
INSERT INTO delivery_realtime_outbox (event_id, event_name, target_group, estabelecimento_id, motoboy_id,
    session_id, session_epoch, aggregate_version, payload, occurred_at_utc)
VALUES (@EventId, @EventName, @Target, @EstabelecimentoId, @MotoboyId, NULL, NULL, @Version, @Payload::jsonb, NOW());",
                    new
                    {
                        EventId = Guid.NewGuid(),
                        EventName = DeliveryRealtimeEvents.DeliveryMotoboyMessage,
                        Target = target,
                        EstabelecimentoId = estabelecimentoId,
                        MotoboyId = row.MotoboyId,
                        Version = row.Id,
                        Payload = payload
                    }, transaction);
            }
        }

        public async Task<IReadOnlyList<MotoboyMessageDto>> ListMotoboyMessagesAsync(
            Guid estabelecimentoId, int? motoboyId, int? pedidoId, int limit)
        {
            try
            {
                await using var connection = await _dataSource.OpenConnectionAsync();
                var rows = await connection.QueryAsync<MessageRow>($@"
SELECT {MessageColumns} FROM delivery_motoboy_message
 WHERE estabelecimento_id = @EstabelecimentoId
   AND (@MotoboyId IS NULL OR motoboy_id = @MotoboyId)
   AND (@PedidoId IS NULL OR pedido_id = @PedidoId)
 ORDER BY id DESC LIMIT @Limit;",
                    new { EstabelecimentoId = estabelecimentoId, MotoboyId = motoboyId, PedidoId = pedidoId, Limit = Math.Clamp(limit, 1, 200) });
                return rows.Reverse().Select(ToDto).ToList();
            }
            catch (PostgresException ex) when (IsMissingTable(ex))
            {
                return Array.Empty<MotoboyMessageDto>();
            }
        }

        /// <summary>Marca como lidas as mensagens que o OUTRO lado enviou (o motoboy le as do atendente e vice-versa).</summary>
        public async Task<int> MarkReadAsync(Guid estabelecimentoId, int motoboyId, string readerSide)
        {
            var fromDirection = readerSide == "motoboy" ? "operator" : "motoboy";
            try
            {
                await using var connection = await _dataSource.OpenConnectionAsync();
                return await connection.ExecuteAsync(@"
UPDATE delivery_motoboy_message SET read_at_utc = NOW()
 WHERE estabelecimento_id = @EstabelecimentoId AND motoboy_id = @MotoboyId
   AND direction = @FromDirection AND read_at_utc IS NULL;",
                    new { EstabelecimentoId = estabelecimentoId, MotoboyId = motoboyId, FromDirection = fromDirection });
            }
            catch (PostgresException ex) when (IsMissingTable(ex))
            {
                return 0;
            }
        }
    }
}
