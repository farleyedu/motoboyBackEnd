// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using APIBack.Automation.Dtos;
using APIBack.Automation.Helpers;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Models;
using APIBack.Automation.Services;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace APIBack.Automation.Infra
{
    public class SqlConversationRepository : IConversationRepository
    {
        private const string SelectConversation = @"
SELECT c.id AS Id, COALESCE(c.id_conversa_grupo, c.id) AS IdConversaGrupo, c.id_estabelecimento AS IdEstabelecimento,
       c.id_cliente AS IdCliente, c.estado::text AS Estado, c.id_agente_atribuido AS IdAgenteAtribuido,
       c.data_primeira_mensagem AS DataPrimeiraMensagem, c.data_ultima_mensagem AS DataUltimaMensagem,
       c.data_ultima_entrada AS DataUltimaEntrada, c.janela_24h_fim AS Janela24hFim,
       c.motivo_fechamento AS MotivoFechamento, c.fechado_por_id AS FechadoPorId, c.data_fechamento AS DataFechamento,
       c.data_criacao AS DataCriacao, c.data_atualizacao AS DataAtualizacao, c.contexto_estado::text AS ContextoEstadoJson,
       cl.telefone_e164 AS TelefoneCliente, COALESCE(cl.nome, cl.telefone_e164) AS ClienteNome
  FROM conversas c
  LEFT JOIN clientes cl ON cl.id = c.id_cliente";

        private readonly string _connectionString;
        private readonly ILogger<SqlConversationRepository> _logger;
        private readonly Guid? _centralEstabelecimentoId;
        private static bool _indexesEnsured;

        public SqlConversationRepository(IConfiguration config, ILogger<SqlConversationRepository> logger)
        {
            _connectionString = config.GetConnectionString("DefaultConnection") ?? config["ConnectionStrings:DefaultConnection"] ?? throw new InvalidOperationException("Connection string DefaultConnection nao encontrada.");
            _logger = logger;
            _centralEstabelecimentoId = Guid.TryParse(config["WhatsApp:CentralEstabelecimentoId"], out var parsed) ? parsed : null;
            if (_indexesEnsured) return;
            try
            {
                using var cx = new NpgsqlConnection(_connectionString);
                cx.Execute("CREATE UNIQUE INDEX IF NOT EXISTS ux_mensagens_id_provedor ON mensagens (id_provedor);");
                cx.Execute("CREATE INDEX IF NOT EXISTS ix_conversas_id_conversa_grupo ON conversas (id_conversa_grupo, data_criacao DESC);");
                _indexesEnsured = true;
            }
            catch { }
        }

        public async Task<Conversation?> ObterPorIdAsync(Guid id, Guid? idEstabelecimento = null)
        {
            await using var cx = new NpgsqlConnection(_connectionString);
            var row = idEstabelecimento.HasValue
                ? await ResolveForEstablishmentAsync(cx, id, idEstabelecimento.Value)
                : await ExactAsync(cx, id);
            return row == null ? null : ToConversation(row);
        }

        public async Task<bool> InserirOuAtualizarAsync(Conversation conversa)
        {
            if (conversa.IdEstabelecimento == Guid.Empty) throw new ArgumentException("id_estabelecimento obrigatorio", nameof(conversa));
            var criado = conversa.CriadoEm == default ? DateTime.UtcNow : DateTime.SpecifyKind(conversa.CriadoEm, DateTimeKind.Utc);
            var atualizado = conversa.AtualizadoEm.HasValue && conversa.AtualizadoEm.Value != default ? DateTime.SpecifyKind(conversa.AtualizadoEm.Value, DateTimeKind.Utc) : criado;
            const string sql = @"
INSERT INTO conversas (id, id_conversa_grupo, id_estabelecimento, id_cliente, canal, estado, id_agente_atribuido, data_primeira_mensagem, data_ultima_mensagem, data_ultima_entrada, data_ultima_saida, janela_24h_inicio, janela_24h_fim, qtd_nao_lidas, motivo_fechamento, fechado_por_id, data_fechamento, data_criacao, data_atualizacao)
VALUES (@Id, @IdConversaGrupo, @IdEstabelecimento, @IdCliente, @Canal::canal_chat_enum, @Estado::estado_conversa_enum, @IdAgenteAtribuido, @DataPrimeiraMensagem, @DataUltimaMensagem, @DataUltimaEntrada, @DataUltimaSaida, @Janela24hInicio, @Janela24hFim, @QtdNaoLidas, @MotivoFechamento, @FechadoPorId, @DataFechamento, @DataCriacao, @DataAtualizacao)
ON CONFLICT (id) DO UPDATE SET
  id_conversa_grupo = COALESCE(EXCLUDED.id_conversa_grupo, conversas.id_conversa_grupo),
  id_estabelecimento = EXCLUDED.id_estabelecimento,
  id_cliente = EXCLUDED.id_cliente,
  data_ultima_mensagem = GREATEST(COALESCE(conversas.data_ultima_mensagem, EXCLUDED.data_ultima_mensagem), EXCLUDED.data_ultima_mensagem),
  data_atualizacao = EXCLUDED.data_atualizacao,
  id_agente_atribuido = EXCLUDED.id_agente_atribuido,
  motivo_fechamento = EXCLUDED.motivo_fechamento,
  fechado_por_id = EXCLUDED.fechado_por_id,
  data_fechamento = EXCLUDED.data_fechamento;";
            await using var cx = new NpgsqlConnection(_connectionString);
            return await cx.ExecuteAsync(sql, new
            {
                Id = conversa.IdConversa,
                IdConversaGrupo = conversa.IdConversaGrupo == Guid.Empty ? conversa.IdConversa : conversa.IdConversaGrupo,
                IdEstabelecimento = conversa.IdEstabelecimento,
                IdCliente = conversa.IdCliente,
                Canal = conversa.Canal,
                Estado = EstadoDb(conversa.Estado),
                IdAgenteAtribuido = conversa.Modo == ModoConversa.Humano ? conversa.AgenteDesignadoId : null,
                DataPrimeiraMensagem = criado,
                DataUltimaMensagem = criado,
                DataUltimaEntrada = criado,
                DataUltimaSaida = criado,
                Janela24hInicio = criado,
                Janela24hFim = criado.AddHours(24),
                QtdNaoLidas = 0,
                MotivoFechamento = conversa.MotivoFechamento,
                FechadoPorId = conversa.FechadoPorId,
                DataFechamento = conversa.DataFechamento,
                DataCriacao = criado,
                DataAtualizacao = atualizado
            }) > 0;
        }

        public async Task DefinirModoAsync(Guid id, ModoConversa modo, int? agenteId)
        {
            const string sql = @"
UPDATE conversas
   SET id_agente_atribuido = @IdAgente,
       estado = @Estado::estado_conversa_enum,
       data_atualizacao = NOW()
 WHERE id = @Id;";
            await using var cx = new NpgsqlConnection(_connectionString);
            await cx.ExecuteAsync(sql, new { Id = id, IdAgente = modo == ModoConversa.Humano ? agenteId : null, Estado = modo == ModoConversa.Humano ? EstadoDb(EstadoConversa.EmAtendimento) : EstadoDb(EstadoConversa.Aberto) });
        }

        public async Task<bool> ExisteIdMensagemPorProvedorWaAsync(string idMensagemWa)
        {
            await using var cx = new NpgsqlConnection(_connectionString);
            return (await cx.ExecuteScalarAsync<int?>("SELECT 1 FROM mensagens WHERE id_provedor = @Id LIMIT 1;", new { Id = idMensagemWa })).HasValue;
        }

        public async Task<Guid> GarantirClienteAsync(string telefoneE164, Guid idEstabelecimento)
        {
            await using var cx = new NpgsqlConnection(_connectionString);
            return await GarantirClienteTxAsync(cx, null, telefoneE164, idEstabelecimento);
        }

        public async Task<Guid> ObterIdConversaPorClienteAsync(Guid idCliente, Guid idEstabelecimento)
        {
            const string sql = @"SELECT id FROM conversas WHERE id_cliente = @IdCliente AND id_estabelecimento = @IdEstabelecimento AND estado NOT IN ('fechado_automaticamente'::estado_conversa_enum, 'fechado_agente'::estado_conversa_enum, 'arquivada'::estado_conversa_enum) ORDER BY data_criacao DESC LIMIT 1;";
            await using var cx = new NpgsqlConnection(_connectionString);
            return (await cx.ExecuteScalarAsync<Guid?>(sql, new { IdCliente = idCliente, IdEstabelecimento = idEstabelecimento })) ?? Guid.Empty;
        }

        public async Task<Guid> ObterIdConversaAbertaPorGrupoAsync(Guid idConversaGrupo, Guid idEstabelecimento)
        {
            const string sql = @"SELECT id FROM conversas WHERE COALESCE(id_conversa_grupo, id) = @IdConversaGrupo AND id_estabelecimento = @IdEstabelecimento AND id <> COALESCE(id_conversa_grupo, id) AND estado NOT IN ('fechado_automaticamente'::estado_conversa_enum, 'fechado_agente'::estado_conversa_enum, 'arquivada'::estado_conversa_enum) ORDER BY COALESCE(data_ultima_mensagem, data_atualizacao, data_criacao) DESC LIMIT 1;";
            await using var cx = new NpgsqlConnection(_connectionString);
            return (await cx.ExecuteScalarAsync<Guid?>(sql, new { IdConversaGrupo = idConversaGrupo, IdEstabelecimento = idEstabelecimento })) ?? Guid.Empty;
        }

        public async Task AtualizarEstadoAsync(Guid idConversa, EstadoConversa novoEstado)
        {
            await using var cx = new NpgsqlConnection(_connectionString);
            await cx.ExecuteAsync("UPDATE conversas SET estado = @Estado::estado_conversa_enum, data_atualizacao = NOW() WHERE id = @Id;", new { Id = idConversa, Estado = EstadoDb(novoEstado) });
        }

        public async Task AcrescentarMensagemAsync(Message mensagem, string? phoneNumberId, string? idWa = null)
        {
            if (mensagem.IdConversa == Guid.Empty) throw new ArgumentException("IdConversa obrigatorio", nameof(mensagem));
            var quando = mensagem.DataHora == default ? DateTime.UtcNow : mensagem.DataHora.Kind == DateTimeKind.Utc ? mensagem.DataHora : DateTime.SpecifyKind(mensagem.DataHora, DateTimeKind.Utc);
            var direcao = mensagem.Direcao == DirecaoMensagem.Entrada ? "entrada" : "saida";
            var criadaPor = string.IsNullOrWhiteSpace(mensagem.CriadaPor) ? (mensagem.Direcao == DirecaoMensagem.Entrada ? "cliente" : "sistema") : mensagem.CriadaPor!;
            var tipoOriginal = string.IsNullOrWhiteSpace(mensagem.TipoOriginal) ? mensagem.Tipo : mensagem.TipoOriginal;
            var tipo = MessageTypeMapper.MapType(tipoOriginal, mensagem.Direcao, criadaPor);
            await using var cx = new NpgsqlConnection(_connectionString);
            await cx.OpenAsync();
            await using var tx = await cx.BeginTransactionAsync();
            var exists = await cx.ExecuteScalarAsync<int?>("SELECT 1 FROM conversas WHERE id = @Id LIMIT 1;", new { Id = mensagem.IdConversa }, tx);
            if (!exists.HasValue)
            {
                var idEstab = await cx.ExecuteScalarAsync<Guid?>("SELECT id_estabelecimento FROM waba_phone WHERE display_phone_number = @Display LIMIT 1;", new { Display = phoneNumberId }, tx);
                if (!idEstab.HasValue || idEstab.Value == Guid.Empty) throw new InvalidOperationException("Estabelecimento nao encontrado para este WABA");
                var telefone = TelefoneHelper.ToE164(idWa ?? string.Empty);
                var idCliente = await GarantirClienteTxAsync(cx, tx, telefone, idEstab.Value);
                await cx.ExecuteAsync(@"INSERT INTO conversas (id, id_conversa_grupo, id_estabelecimento, id_cliente, canal, estado, data_primeira_mensagem, data_ultima_mensagem, data_ultima_entrada, janela_24h_inicio, janela_24h_fim, qtd_nao_lidas, data_criacao, data_atualizacao)
VALUES (@Id, @Id, @IdEstabelecimento, @IdCliente, 'whatsapp', 'aberto'::estado_conversa_enum, @Quando, @Quando, @Quando, @Quando, @Quando + interval '24 hour', 1, @Quando, @Quando)
ON CONFLICT (id) DO NOTHING;", new { Id = mensagem.IdConversa, IdEstabelecimento = idEstab.Value, IdCliente = idCliente, Quando = quando }, tx);
            }
            await cx.ExecuteAsync(@"INSERT INTO mensagens (id, id_conversa, direcao, tipo, status, id_provedor, codigo_erro, mensagem_erro, tentativas, criada_por, data_envio, data_entrega, data_leitura, data_criacao, conteudo)
VALUES (@Id, @IdConversa, @Direcao::direcao_mensagem_enum, @Tipo::tipo_mensagem_enum, @Status::status_mensagem_enum, @IdProvedor, @CodigoErro, @MensagemErro, @Tentativas, @CriadaPor, @DataEnvio, @DataEntrega, @DataLeitura, @DataCriacao, @Conteudo)
ON CONFLICT DO NOTHING;", new
            {
                Id = mensagem.Id == Guid.Empty ? Guid.NewGuid() : mensagem.Id,
                IdConversa = mensagem.IdConversa,
                Direcao = direcao,
                Tipo = string.IsNullOrWhiteSpace(tipo) ? "texto" : tipo,
                Status = string.IsNullOrWhiteSpace(mensagem.Status) ? (mensagem.Direcao == DirecaoMensagem.Entrada ? "entregue" : "fila") : mensagem.Status,
                IdProvedor = !string.IsNullOrWhiteSpace(mensagem.IdProvedor) ? mensagem.IdProvedor : mensagem.IdMensagemWa,
                CodigoErro = mensagem.CodigoErro,
                MensagemErro = mensagem.MensagemErro,
                Tentativas = mensagem.Tentativas,
                CriadaPor = criadaPor,
                DataEnvio = mensagem.DataEnvio ?? quando,
                DataEntrega = mensagem.DataEntrega,
                DataLeitura = mensagem.DataLeitura,
                DataCriacao = mensagem.DataCriacao == default ? quando : mensagem.DataCriacao,
                Conteudo = mensagem.Conteudo
            }, tx);
            await cx.ExecuteAsync(@"UPDATE conversas
   SET data_primeira_mensagem = COALESCE(data_primeira_mensagem, @Quando),
       data_ultima_mensagem = GREATEST(COALESCE(data_ultima_mensagem, @Quando), @Quando),
       data_ultima_entrada = CASE WHEN @Direcao = 'entrada' THEN @Quando ELSE data_ultima_entrada END,
       data_ultima_saida = CASE WHEN @Direcao = 'saida' THEN @Quando ELSE data_ultima_saida END,
       janela_24h_inicio = CASE WHEN @Direcao = 'entrada' AND janela_24h_inicio IS NULL THEN @Quando ELSE janela_24h_inicio END,
       janela_24h_fim = CASE WHEN @Direcao = 'entrada' THEN GREATEST(COALESCE(janela_24h_fim, @Quando + interval '24 hour'), @Quando + interval '24 hour') ELSE janela_24h_fim END,
       qtd_nao_lidas = CASE WHEN @Direcao = 'entrada' THEN COALESCE(qtd_nao_lidas, 0) + 1 ELSE qtd_nao_lidas END,
       data_atualizacao = NOW()
 WHERE id = @IdConversa;", new { IdConversa = mensagem.IdConversa, Quando = quando, Direcao = direcao }, tx);
            await tx.CommitAsync();
        }

        public async Task<IReadOnlyList<ConversationListItemDto>> ListarConversasAsync(string? estado, int? idAgente, bool incluirArquivadas, Guid? idEstabelecimento = null)
        {
            await using var cx = new NpgsqlConnection(_connectionString);
            var sql = SelectConversation + (idEstabelecimento.HasValue ? " WHERE c.id_estabelecimento = @IdEstabelecimento" : string.Empty);
            var rows = (await cx.QueryAsync<ConversationRow>(sql, new { IdEstabelecimento = idEstabelecimento })).ToList();
            var itens = new List<ConversationListItemDto>();
            if (idEstabelecimento.HasValue && IsCentral(idEstabelecimento.Value))
            {
                foreach (var root in rows.Where(r => r.Id == r.IdConversaGrupo && r.IdEstabelecimento == idEstabelecimento.Value))
                {
                    itens.Add(await BuildCentralListItemAsync(cx, root));
                }
            }
            else
            {
                foreach (var row in rows)
                {
                    itens.Add(await BuildStandardListItemAsync(cx, row));
                }
            }
            var filtro = NormalizeEstado(estado);
            if (!string.IsNullOrWhiteSpace(filtro)) itens = itens.Where(i => NormalizeEstado(i.Estado) == filtro).ToList();
            if (idAgente.HasValue) itens = itens.Where(i => i.IdAgenteAtribuido == idAgente.Value).ToList();
            if (!incluirArquivadas) itens = itens.Where(i => !string.Equals(i.Estado, "arquivada", StringComparison.OrdinalIgnoreCase)).ToList();
            return itens.OrderByDescending(i => i.UltimaMensagemData ?? i.DataUltimaMensagem ?? i.DataAtualizacao).ToList();
        }

        public async Task<ConversationHistoryDto?> ObterHistoricoConversaAsync(Guid idConversa, int page, int pageSize, Guid? idEstabelecimento = null)
        {
            page = Math.Max(1, page);
            pageSize = pageSize <= 0 ? 50 : pageSize;
            await using var cx = new NpgsqlConnection(_connectionString);
            var row = await ExactAsync(cx, idConversa);
            if (row == null) return null;
            if (idEstabelecimento.HasValue && IsCentral(idEstabelecimento.Value))
            {
                var root = await RootAsync(cx, row);
                if (root == null || root.IdEstabelecimento != idEstabelecimento.Value) return null;
                var mensagens = (await GroupMessagesAsync(cx, root.IdConversaGrupo, page, pageSize)).ToList();
                var total = await cx.ExecuteScalarAsync<int>("SELECT COUNT(1) FROM mensagens m JOIN conversas c ON c.id = m.id_conversa WHERE COALESCE(c.id_conversa_grupo, c.id) = @GroupId;", new { GroupId = root.IdConversaGrupo });
                return new ConversationHistoryDto { Conversa = await BuildCentralDetailsAsync(cx, root), Mensagens = mensagens, Page = page, PageSize = pageSize, Total = total };
            }
            if (idEstabelecimento.HasValue && row.IdEstabelecimento != idEstabelecimento.Value) return null;
            var count = await cx.ExecuteScalarAsync<int>("SELECT COUNT(1) FROM mensagens WHERE id_conversa = @Id;", new { Id = row.Id });
            return new ConversationHistoryDto { Conversa = Details(row), Mensagens = (await ExactMessagesAsync(cx, row.Id, page, pageSize)).ToList(), Page = page, PageSize = pageSize, Total = count };
        }

        public async Task<bool> AtribuirConversaAsync(Guid idConversa, int idAgente, Guid? idEstabelecimento = null)
        {
            var alvo = await ResolveOperationTargetAsync(idConversa, idEstabelecimento);
            if (alvo == null) return false;
            await using var cx = new NpgsqlConnection(_connectionString);
            return await cx.ExecuteAsync("UPDATE conversas SET id_agente_atribuido = @IdAgente, estado = 'em_atendimento'::estado_conversa_enum, data_atualizacao = NOW() WHERE id = @Id;", new { Id = alvo.Id, IdAgente = idAgente }) > 0;
        }

        public async Task<bool> FecharConversaAsync(Guid idConversa, int? idAgente, string? motivo, Guid? idEstabelecimento = null)
        {
            var alvo = await ResolveOperationTargetAsync(idConversa, idEstabelecimento);
            if (alvo == null) return false;
            await using var cx = new NpgsqlConnection(_connectionString);
            return await cx.ExecuteAsync("UPDATE conversas SET estado = @Estado::estado_conversa_enum, motivo_fechamento = @Motivo, fechado_por_id = @FechadoPorId, data_fechamento = NOW(), data_atualizacao = NOW() WHERE id = @Id;", new { Id = alvo.Id, Estado = idAgente.HasValue ? "fechado_agente" : "fechado_automaticamente", Motivo = string.IsNullOrWhiteSpace(motivo) ? null : motivo.Trim(), FechadoPorId = idAgente }) > 0;
        }

        public async Task<ConversationDetailsDto?> ArquivarConversaAsync(Guid idConversa, Guid? idEstabelecimento = null)
        {
            var alvo = await ResolveOperationTargetAsync(idConversa, idEstabelecimento);
            if (alvo == null) return null;
            await using var cx = new NpgsqlConnection(_connectionString);
            if (await cx.ExecuteAsync("UPDATE conversas SET estado = 'arquivada'::estado_conversa_enum, data_atualizacao = NOW() WHERE id = @Id;", new { Id = alvo.Id }) == 0) return null;
            return await ObterDetalhesConversaAsync(idConversa, idEstabelecimento);
        }

        public async Task<ConversationDetailsDto?> ObterDetalhesConversaAsync(Guid idConversa, Guid? idEstabelecimento = null)
        {
            await using var cx = new NpgsqlConnection(_connectionString);
            var row = await ExactAsync(cx, idConversa);
            if (row == null) return null;
            if (idEstabelecimento.HasValue && IsCentral(idEstabelecimento.Value))
            {
                var root = await RootAsync(cx, row);
                return root != null && root.IdEstabelecimento == idEstabelecimento.Value ? await BuildCentralDetailsAsync(cx, root) : null;
            }
            return !idEstabelecimento.HasValue || row.IdEstabelecimento == idEstabelecimento.Value ? Details(row) : null;
        }

        public async Task SalvarContextoAsync(Guid idConversa, ConversationContext contexto)
        {
            await using var cx = new NpgsqlConnection(_connectionString);
            var atual = Deserialize(await cx.ExecuteScalarAsync<string?>("SELECT contexto_estado::text FROM conversas WHERE id = @Id;", new { Id = idConversa }));
            var json = JsonSerializer.Serialize(CentralRoutingService.MergeCentralSelection(atual, contexto));
            await cx.ExecuteAsync("UPDATE conversas SET contexto_estado = @Json::jsonb, data_atualizacao = NOW() WHERE id = @Id;", new { Id = idConversa, Json = json });
        }

        public async Task<ConversationContext?> ObterContextoAsync(Guid idConversa)
        {
            await using var cx = new NpgsqlConnection(_connectionString);
            return Deserialize(await cx.ExecuteScalarAsync<string?>("SELECT contexto_estado::text FROM conversas WHERE id = @Id;", new { Id = idConversa }));
        }

        public async Task LimparContextoAsync(Guid idConversa)
        {
            await using var cx = new NpgsqlConnection(_connectionString);
            var preservado = CentralRoutingService.BuildPreservedSelectionContext(Deserialize(await cx.ExecuteScalarAsync<string?>("SELECT contexto_estado::text FROM conversas WHERE id = @Id;", new { Id = idConversa })));
            if (preservado != null)
            {
                await cx.ExecuteAsync("UPDATE conversas SET contexto_estado = @Json::jsonb, data_atualizacao = NOW() WHERE id = @Id;", new { Id = idConversa, Json = JsonSerializer.Serialize(preservado) });
                return;
            }
            await cx.ExecuteAsync("UPDATE conversas SET contexto_estado = NULL, data_atualizacao = NOW() WHERE id = @Id;", new { Id = idConversa });
        }

        private async Task<ConversationRow?> ResolveForEstablishmentAsync(NpgsqlConnection cx, Guid id, Guid idEstabelecimento)
        {
            var exact = await ExactAsync(cx, id);
            if (exact == null) return null;
            if (IsCentral(idEstabelecimento))
            {
                var root = await RootAsync(cx, exact);
                if (root == null || root.IdEstabelecimento != idEstabelecimento) return null;
                return exact.Id == root.Id ? await OperationalAsync(cx, root.IdConversaGrupo) ?? root : exact;
            }
            return exact.IdEstabelecimento == idEstabelecimento ? exact : null;
        }

        private async Task<ConversationRow?> ResolveOperationTargetAsync(Guid idConversa, Guid? idEstabelecimento)
        {
            await using var cx = new NpgsqlConnection(_connectionString);
            var exact = await ExactAsync(cx, idConversa);
            if (exact == null) return null;
            if (!idEstabelecimento.HasValue) return exact;
            if (IsCentral(idEstabelecimento.Value))
            {
                var root = await RootAsync(cx, exact);
                if (root == null || root.IdEstabelecimento != idEstabelecimento.Value) return null;
                return exact.Id == root.Id ? await OperationalAsync(cx, root.IdConversaGrupo) ?? root : exact;
            }
            return exact.IdEstabelecimento == idEstabelecimento.Value ? exact : null;
        }

        private Task<ConversationRow?> ExactAsync(NpgsqlConnection cx, Guid id) => cx.QueryFirstOrDefaultAsync<ConversationRow>(SelectConversation + " WHERE c.id = @Id LIMIT 1;", new { Id = id });
        private Task<ConversationRow?> RootAsync(NpgsqlConnection cx, ConversationRow row) => cx.QueryFirstOrDefaultAsync<ConversationRow>(SelectConversation + " WHERE c.id = @Id LIMIT 1;", new { Id = row.IdConversaGrupo });
        private Task<ConversationRow?> OperationalAsync(NpgsqlConnection cx, Guid groupId) => cx.QueryFirstOrDefaultAsync<ConversationRow>(SelectConversation + " WHERE COALESCE(c.id_conversa_grupo, c.id) = @GroupId ORDER BY CASE WHEN c.id <> COALESCE(c.id_conversa_grupo, c.id) AND c.estado NOT IN ('fechado_automaticamente'::estado_conversa_enum, 'fechado_agente'::estado_conversa_enum, 'arquivada'::estado_conversa_enum) THEN 0 WHEN c.id <> COALESCE(c.id_conversa_grupo, c.id) THEN 1 ELSE 2 END, COALESCE(c.data_ultima_mensagem, c.data_atualizacao, c.data_criacao) DESC LIMIT 1;", new { GroupId = groupId });

        private async Task<ConversationListItemDto> BuildStandardListItemAsync(NpgsqlConnection cx, ConversationRow row)
        {
            var last = await LastMessageAsync(cx, row.Id, false);
            return new ConversationListItemDto
            {
                Id = row.Id,
                IdCliente = row.IdCliente,
                ClienteNome = row.ClienteNome,
                ClienteNumero = row.TelefoneCliente,
                Estado = EstadoApi(row.Estado),
                IdAgenteAtribuido = row.IdAgenteAtribuido,
                DataPrimeiraMensagem = row.DataPrimeiraMensagem ?? row.DataCriacao,
                DataUltimaMensagem = row.DataUltimaMensagem ?? row.DataAtualizacao,
                DataCriacao = row.DataCriacao,
                DataAtualizacao = row.DataAtualizacao,
                DataFechamento = row.DataFechamento,
                UltimaMensagemConteudo = last?.Conteudo,
                UltimaMensagemData = last?.DataCriacao,
                UltimaMensagemCriadaPor = last?.CriadaPor
            };
        }

        private async Task<ConversationListItemDto> BuildCentralListItemAsync(NpgsqlConnection cx, ConversationRow root)
        {
            var op = await OperationalAsync(cx, root.IdConversaGrupo) ?? root;
            var agg = await GroupAggregateAsync(cx, root.IdConversaGrupo);
            var last = await LastMessageAsync(cx, root.IdConversaGrupo, true);
            return new ConversationListItemDto
            {
                Id = root.Id,
                IdCliente = root.IdCliente,
                ClienteNome = root.ClienteNome,
                ClienteNumero = root.TelefoneCliente ?? op.TelefoneCliente,
                Estado = EstadoApi(op.Estado),
                IdAgenteAtribuido = op.IdAgenteAtribuido,
                DataPrimeiraMensagem = agg.FirstMessage ?? root.DataPrimeiraMensagem ?? root.DataCriacao,
                DataUltimaMensagem = agg.LastMessage ?? op.DataUltimaMensagem ?? root.DataAtualizacao,
                DataCriacao = root.DataCriacao,
                DataAtualizacao = agg.LastUpdate ?? op.DataAtualizacao,
                DataFechamento = op.DataFechamento,
                UltimaMensagemConteudo = last?.Conteudo,
                UltimaMensagemData = last?.DataCriacao,
                UltimaMensagemCriadaPor = last?.CriadaPor
            };
        }

        private async Task<ConversationDetailsDto> BuildCentralDetailsAsync(NpgsqlConnection cx, ConversationRow root)
        {
            var op = await OperationalAsync(cx, root.IdConversaGrupo) ?? root;
            var agg = await GroupAggregateAsync(cx, root.IdConversaGrupo);
            return new ConversationDetailsDto
            {
                Id = root.Id,
                IdCliente = root.IdCliente,
                ClienteNome = root.ClienteNome,
                ClienteNumero = root.TelefoneCliente ?? op.TelefoneCliente,
                Estado = EstadoApi(op.Estado),
                IdAgenteAtribuido = op.IdAgenteAtribuido,
                DataPrimeiraMensagem = agg.FirstMessage ?? root.DataPrimeiraMensagem ?? root.DataCriacao,
                DataUltimaMensagem = agg.LastMessage ?? op.DataUltimaMensagem ?? root.DataAtualizacao,
                DataCriacao = root.DataCriacao,
                DataAtualizacao = agg.LastUpdate ?? op.DataAtualizacao,
                DataFechamento = op.DataFechamento,
                FechadoPorId = op.FechadoPorId,
                MotivoFechamento = op.MotivoFechamento
            };
        }

        private ConversationDetailsDto Details(ConversationRow row) => new ConversationDetailsDto
        {
            Id = row.Id,
            IdCliente = row.IdCliente,
            ClienteNome = row.ClienteNome,
            ClienteNumero = row.TelefoneCliente,
            Estado = EstadoApi(row.Estado),
            IdAgenteAtribuido = row.IdAgenteAtribuido,
            DataPrimeiraMensagem = row.DataPrimeiraMensagem ?? row.DataCriacao,
            DataUltimaMensagem = row.DataUltimaMensagem ?? row.DataAtualizacao,
            DataCriacao = row.DataCriacao,
            DataAtualizacao = row.DataAtualizacao,
            DataFechamento = row.DataFechamento,
            FechadoPorId = row.FechadoPorId,
            MotivoFechamento = row.MotivoFechamento
        };

        private Task<GroupAggregate> GroupAggregateAsync(NpgsqlConnection cx, Guid groupId) => cx.QueryFirstAsync<GroupAggregate>("SELECT MIN(COALESCE(data_primeira_mensagem, data_criacao)) AS FirstMessage, MAX(COALESCE(data_ultima_mensagem, data_atualizacao, data_criacao)) AS LastMessage, MAX(data_atualizacao) AS LastUpdate FROM conversas WHERE COALESCE(id_conversa_grupo, id) = @GroupId;", new { GroupId = groupId });
        private Task<MessagePreview?> LastMessageAsync(NpgsqlConnection cx, Guid id, bool byGroup) => cx.QueryFirstOrDefaultAsync<MessagePreview>(@"SELECT m.conteudo AS Conteudo, m.data_criacao AS DataCriacao, COALESCE(m.criada_por, '') AS CriadaPor FROM mensagens m JOIN conversas c ON c.id = m.id_conversa WHERE " + (byGroup ? "COALESCE(c.id_conversa_grupo, c.id) = @Id" : "m.id_conversa = @Id") + " ORDER BY m.data_criacao DESC LIMIT 1;", new { Id = id });
        private Task<IEnumerable<ConversationMessageItemDto>> ExactMessagesAsync(NpgsqlConnection cx, Guid id, int page, int pageSize) => cx.QueryAsync<ConversationMessageItemDto>(@"SELECT m.id AS Id, COALESCE(m.criada_por, '') AS CriadaPor, COALESCE(m.conteudo, '') AS Conteudo, m.data_envio AS DataEnvio, m.data_criacao AS DataCriacao FROM mensagens m WHERE m.id_conversa = @Id ORDER BY m.data_criacao ASC, m.data_envio ASC LIMIT @PageSize OFFSET @Offset;", new { Id = id, PageSize = pageSize, Offset = (page - 1) * pageSize });
        private Task<IEnumerable<ConversationMessageItemDto>> GroupMessagesAsync(NpgsqlConnection cx, Guid groupId, int page, int pageSize) => cx.QueryAsync<ConversationMessageItemDto>(@"SELECT m.id AS Id, COALESCE(m.criada_por, '') AS CriadaPor, COALESCE(m.conteudo, '') AS Conteudo, m.data_envio AS DataEnvio, m.data_criacao AS DataCriacao FROM mensagens m JOIN conversas c ON c.id = m.id_conversa WHERE COALESCE(c.id_conversa_grupo, c.id) = @GroupId ORDER BY m.data_criacao ASC, m.data_envio ASC LIMIT @PageSize OFFSET @Offset;", new { GroupId = groupId, PageSize = pageSize, Offset = (page - 1) * pageSize });

        private async Task<Guid> GarantirClienteTxAsync(NpgsqlConnection cx, NpgsqlTransaction? tx, string telefoneE164, Guid idEstabelecimento)
        {
            var existente = await cx.ExecuteScalarAsync<Guid?>(@"SELECT id FROM clientes WHERE id_estabelecimento = @IdEstabelecimento AND telefone_e164 = @Telefone LIMIT 1;", new { IdEstabelecimento = idEstabelecimento, Telefone = telefoneE164 }, tx);
            if (existente.HasValue && existente.Value != Guid.Empty) return existente.Value;
            var novoId = Guid.NewGuid();
            var agora = DateTime.UtcNow;
            await cx.ExecuteAsync(@"INSERT INTO clientes (id, id_estabelecimento, telefone_e164, data_criacao, data_atualizacao) SELECT @Id, @IdEstabelecimento, @Telefone, @CriadoEm, @AtualizadoEm WHERE NOT EXISTS (SELECT 1 FROM clientes WHERE id_estabelecimento = @IdEstabelecimento AND telefone_e164 = @Telefone);", new { Id = novoId, IdEstabelecimento = idEstabelecimento, Telefone = telefoneE164, CriadoEm = agora, AtualizadoEm = agora }, tx);
            return (await cx.ExecuteScalarAsync<Guid?>(@"SELECT id FROM clientes WHERE id_estabelecimento = @IdEstabelecimento AND telefone_e164 = @Telefone LIMIT 1;", new { IdEstabelecimento = idEstabelecimento, Telefone = telefoneE164 }, tx)) ?? novoId;
        }

        private Conversation ToConversation(ConversationRow row) => new Conversation
        {
            IdConversa = row.Id,
            IdConversaGrupo = row.IdConversaGrupo,
            IdEstabelecimento = row.IdEstabelecimento,
            IdCliente = row.IdCliente,
            TelefoneCliente = row.TelefoneCliente,
            IdWa = row.TelefoneCliente ?? string.Empty,
            Modo = row.IdAgenteAtribuido.HasValue ? ModoConversa.Humano : ModoConversa.Bot,
            AgenteDesignadoId = row.IdAgenteAtribuido,
            UltimoUsuarioEm = row.DataUltimaEntrada,
            Janela24hExpiraEm = row.Janela24hFim,
            CriadoEm = row.DataCriacao,
            AtualizadoEm = row.DataAtualizacao,
            Estado = EstadoModel(row.Estado),
            MotivoFechamento = row.MotivoFechamento,
            FechadoPorId = row.FechadoPorId,
            DataFechamento = row.DataFechamento,
            ContextoEstadoJson = row.ContextoEstadoJson
        };

        private bool IsCentral(Guid idEstabelecimento) => _centralEstabelecimentoId.HasValue && _centralEstabelecimentoId.Value == idEstabelecimento;
        private static string EstadoDb(EstadoConversa estado) => estado switch { EstadoConversa.Aberto => "aberto", EstadoConversa.EmAtendimento => "em_atendimento", EstadoConversa.FechadoAutomaticamente => "fechado_automaticamente", EstadoConversa.FechadoAgente => "fechado_agente", EstadoConversa.Arquivada => "arquivada", _ => "aberto" };
        private static EstadoConversa EstadoModel(string? estado) => NormalizeEstado(estado) switch { "aberto" => EstadoConversa.Aberto, "em_atendimento" => EstadoConversa.EmAtendimento, "fechado_agente" => EstadoConversa.FechadoAgente, "fechado_automaticamente" => EstadoConversa.FechadoAutomaticamente, "arquivada" => EstadoConversa.Arquivada, _ => EstadoConversa.Aberto };
        private static string EstadoApi(string? estado) => NormalizeEstado(estado) switch { "fechado_automaticamente" => "fechado_bot", var value => value };
        private static string NormalizeEstado(string? estado) => (estado ?? string.Empty).Trim().ToLowerInvariant() switch { "aguardando_atendimento" => "em_atendimento", "fechado_bot" => "fechado_automaticamente", "arquivado" => "arquivada", _ => (estado ?? string.Empty).Trim().ToLowerInvariant() };
        private static ConversationContext? Deserialize(string? json) { try { return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<ConversationContext>(json); } catch { return null; } }

        private sealed class ConversationRow
        {
            public Guid Id { get; set; }
            public Guid IdConversaGrupo { get; set; }
            public Guid IdEstabelecimento { get; set; }
            public Guid IdCliente { get; set; }
            public string? Estado { get; set; }
            public int? IdAgenteAtribuido { get; set; }
            public DateTime? DataPrimeiraMensagem { get; set; }
            public DateTime? DataUltimaMensagem { get; set; }
            public DateTime? DataUltimaEntrada { get; set; }
            public DateTime? Janela24hFim { get; set; }
            public string? MotivoFechamento { get; set; }
            public int? FechadoPorId { get; set; }
            public DateTime? DataFechamento { get; set; }
            public DateTime DataCriacao { get; set; }
            public DateTime DataAtualizacao { get; set; }
            public string? ContextoEstadoJson { get; set; }
            public string? TelefoneCliente { get; set; }
            public string? ClienteNome { get; set; }
        }

        private sealed class MessagePreview
        {
            public string? Conteudo { get; set; }
            public DateTime? DataCriacao { get; set; }
            public string? CriadaPor { get; set; }
        }

        private sealed class GroupAggregate
        {
            public DateTime? FirstMessage { get; set; }
            public DateTime? LastMessage { get; set; }
            public DateTime? LastUpdate { get; set; }
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
