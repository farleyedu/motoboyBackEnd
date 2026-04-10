// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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
        private const string WindowExpiredReason = "Janela de 24 horas do WhatsApp expirada";
        private const string WindowExpiredBlockCode = "whatsapp_window_expired";
        private static (bool PhoneNumberId, bool DisplayPhoneNumber)? _cachedWabaPhoneColumns;

        private const string SelectConversation = @"
SELECT c.id AS Id,
       COALESCE(c.id_conversa_grupo, c.id) AS IdConversaGrupo,
       c.id_estabelecimento AS IdEstabelecimento,
       c.id_cliente AS IdCliente,
       c.estado::text AS Estado,
       COALESCE(NULLIF(c.status_atendimento, ''), '') AS StatusAtendimento,
       c.id_agente_atribuido AS IdAgenteAtribuido,
       COALESCE(c.qtd_nao_lidas, 0) AS QtdNaoLidas,
       c.data_ultima_leitura AS DataUltimaLeitura,
       c.data_primeira_mensagem AS DataPrimeiraMensagem,
       c.data_ultima_mensagem AS DataUltimaMensagem,
       c.data_ultima_entrada AS DataUltimaEntrada,
       c.data_ultima_saida AS DataUltimaSaida,
       c.janela_24h_fim AS Janela24hFim,
       c.motivo_fechamento AS MotivoFechamento,
       c.fechado_por_id AS FechadoPorId,
       c.data_fechamento AS DataFechamento,
       c.data_criacao AS DataCriacao,
       c.data_atualizacao AS DataAtualizacao,
       c.contexto_estado::text AS ContextoEstadoJson,
       cl.telefone_e164 AS TelefoneCliente,
       COALESCE(cl.nome, cl.telefone_e164) AS ClienteNome,
       CASE
           WHEN c.id_agente_atribuido IS NULL THEN NULL
           ELSE COALESCE(u.nome, CONCAT('Agente ', c.id_agente_atribuido::text))
       END AS AgenteNome
  FROM conversas c
  LEFT JOIN clientes cl ON cl.id = c.id_cliente
  LEFT JOIN agentes a ON a.id = c.id_agente_atribuido
  LEFT JOIN usuario u ON u.id = a.usuarioid";

        private readonly string _connectionString;
        private readonly ILogger<SqlConversationRepository> _logger;
        private readonly Guid? _centralEstabelecimentoId;
        private static bool _indexesEnsured;

        public SqlConversationRepository(IConfiguration config, ILogger<SqlConversationRepository> logger)
        {
            _connectionString = config.GetConnectionString("DefaultConnection")
                ?? config["ConnectionStrings:DefaultConnection"]
                ?? throw new InvalidOperationException("Connection string DefaultConnection nao encontrada.");
            _logger = logger;
            _centralEstabelecimentoId = Guid.TryParse(config["WhatsApp:CentralEstabelecimentoId"], out var parsed)
                ? parsed
                : null;

            if (_indexesEnsured)
            {
                return;
            }

            try
            {
                using var cx = new NpgsqlConnection(_connectionString);
                cx.Execute("CREATE UNIQUE INDEX IF NOT EXISTS ux_mensagens_id_provedor ON mensagens (id_provedor);");
                cx.Execute("CREATE INDEX IF NOT EXISTS ix_conversas_id_conversa_grupo ON conversas (id_conversa_grupo, data_criacao DESC);");
                cx.Execute("CREATE INDEX IF NOT EXISTS ix_conversa_eventos_group_created ON conversa_eventos (id_conversa_grupo, data_criacao DESC);");
                cx.Execute("CREATE INDEX IF NOT EXISTS ix_conversas_cliente_estabelecimento ON conversas (id_cliente, id_estabelecimento);");
                _indexesEnsured = true;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Nao foi possivel garantir indexes auxiliares de conversa.");
            }
        }

        public async Task<Conversation?> ObterPorIdAsync(Guid id, Guid? idEstabelecimento = null)
        {
            await using var cx = new NpgsqlConnection(_connectionString);
            var row = idEstabelecimento.HasValue
                ? await ResolveForEstablishmentAsync(cx, id, idEstabelecimento.Value)
                : await ExactSyncedAsync(cx, id);

            return row == null ? null : ToConversation(row);
        }

        public async Task<bool> InserirOuAtualizarAsync(Conversation conversa)
        {
            await using var cx = new NpgsqlConnection(_connectionString);
            return await InserirOuAtualizarAsync(cx, null, conversa);
        }

        public async Task DefinirModoAsync(Guid id, ModoConversa modo, int? agenteId)
        {
            const string sql = @"
UPDATE conversas
   SET id_agente_atribuido = @IdAgente,
       estado = @Estado::estado_conversa_enum,
       status_atendimento = @StatusAtendimento,
       data_fechamento = CASE WHEN @Estado IN ('aberto', 'em_atendimento') THEN NULL ELSE data_fechamento END,
       motivo_fechamento = CASE WHEN @Estado IN ('aberto', 'em_atendimento') THEN NULL ELSE motivo_fechamento END,
       fechado_por_id = CASE WHEN @Estado IN ('aberto', 'em_atendimento') THEN NULL ELSE fechado_por_id END,
       data_atualizacao = NOW()
 WHERE id = @Id;";

            await using var cx = new NpgsqlConnection(_connectionString);
            await cx.ExecuteAsync(sql, new
            {
                Id = id,
                IdAgente = modo == ModoConversa.Humano ? agenteId : null,
                Estado = modo == ModoConversa.Humano ? "em_atendimento" : "aberto",
                StatusAtendimento = modo == ModoConversa.Humano ? "em_andamento" : "com_bot"
            });
        }

        private async Task<bool> InserirOuAtualizarAsync(NpgsqlConnection cx, NpgsqlTransaction? tx, Conversation conversa)
        {
            if (conversa.IdEstabelecimento == Guid.Empty)
            {
                throw new ArgumentException("id_estabelecimento obrigatorio", nameof(conversa));
            }

            var criado = conversa.CriadoEm == default
                ? DateTime.UtcNow
                : DateTime.SpecifyKind(conversa.CriadoEm, DateTimeKind.Utc);
            var atualizado = conversa.AtualizadoEm.HasValue && conversa.AtualizadoEm.Value != default
                ? DateTime.SpecifyKind(conversa.AtualizadoEm.Value, DateTimeKind.Utc)
                : criado;

            const string sql = @"
INSERT INTO conversas (
    id, id_conversa_grupo, id_estabelecimento, id_cliente, canal, estado, status_atendimento,
    id_agente_atribuido, data_primeira_mensagem, data_ultima_mensagem, data_ultima_entrada,
    data_ultima_saida, janela_24h_inicio, janela_24h_fim, qtd_nao_lidas, motivo_fechamento,
    fechado_por_id, data_fechamento, data_ultima_leitura, data_criacao, data_atualizacao
)
VALUES (
    @Id, @IdConversaGrupo, @IdEstabelecimento, @IdCliente, @Canal::canal_chat_enum,
    @Estado::estado_conversa_enum, @StatusAtendimento, @IdAgenteAtribuido, @DataPrimeiraMensagem,
    @DataUltimaMensagem, @DataUltimaEntrada, @DataUltimaSaida, @Janela24hInicio, @Janela24hFim,
    @QtdNaoLidas, @MotivoFechamento, @FechadoPorId, @DataFechamento, @DataUltimaLeitura,
    @DataCriacao, @DataAtualizacao
)
ON CONFLICT (id) DO UPDATE SET
  id_conversa_grupo = COALESCE(EXCLUDED.id_conversa_grupo, conversas.id_conversa_grupo),
  id_estabelecimento = EXCLUDED.id_estabelecimento,
  id_cliente = EXCLUDED.id_cliente,
  estado = EXCLUDED.estado,
  status_atendimento = COALESCE(NULLIF(EXCLUDED.status_atendimento, ''), conversas.status_atendimento),
  id_agente_atribuido = EXCLUDED.id_agente_atribuido,
  data_primeira_mensagem = COALESCE(conversas.data_primeira_mensagem, EXCLUDED.data_primeira_mensagem),
  data_ultima_mensagem = GREATEST(COALESCE(conversas.data_ultima_mensagem, EXCLUDED.data_ultima_mensagem), EXCLUDED.data_ultima_mensagem),
  data_ultima_entrada = COALESCE(EXCLUDED.data_ultima_entrada, conversas.data_ultima_entrada),
  data_ultima_saida = COALESCE(EXCLUDED.data_ultima_saida, conversas.data_ultima_saida),
  janela_24h_fim = COALESCE(EXCLUDED.janela_24h_fim, conversas.janela_24h_fim),
  motivo_fechamento = EXCLUDED.motivo_fechamento,
  fechado_por_id = EXCLUDED.fechado_por_id,
  data_fechamento = EXCLUDED.data_fechamento,
  data_ultima_leitura = COALESCE(EXCLUDED.data_ultima_leitura, conversas.data_ultima_leitura),
  data_atualizacao = EXCLUDED.data_atualizacao;";

            return await cx.ExecuteAsync(sql, new
            {
                Id = conversa.IdConversa,
                IdConversaGrupo = conversa.IdConversaGrupo == Guid.Empty ? conversa.IdConversa : conversa.IdConversaGrupo,
                IdEstabelecimento = conversa.IdEstabelecimento,
                IdCliente = conversa.IdCliente,
                Canal = conversa.Canal,
                Estado = EstadoDb(conversa.Estado),
                StatusAtendimento = CanonicalStatus(conversa.StatusAtendimento, EstadoDb(conversa.Estado), conversa.Modo),
                IdAgenteAtribuido = conversa.Modo == ModoConversa.Humano ? conversa.AgenteDesignadoId : null,
                DataPrimeiraMensagem = criado,
                DataUltimaMensagem = criado,
                DataUltimaEntrada = conversa.UltimoUsuarioEm,
                DataUltimaSaida = atualizado,
                Janela24hInicio = criado,
                Janela24hFim = conversa.Janela24hExpiraEm ?? criado.AddHours(24),
                QtdNaoLidas = 0,
                MotivoFechamento = conversa.MotivoFechamento,
                FechadoPorId = conversa.FechadoPorId,
                DataFechamento = conversa.DataFechamento,
                DataUltimaLeitura = conversa.DataUltimaLeitura,
                DataCriacao = criado,
                DataAtualizacao = atualizado
            }, tx) > 0;
        }

        public async Task AcrescentarMensagemAsync(Message mensagem, string? phoneNumberId, string? idWa = null)
        {
            if (mensagem.IdConversa == Guid.Empty)
            {
                throw new ArgumentException("IdConversa obrigatorio", nameof(mensagem));
            }

            var quando = mensagem.DataHora == default
                ? DateTime.UtcNow
                : mensagem.DataHora.Kind == DateTimeKind.Utc
                    ? mensagem.DataHora
                    : DateTime.SpecifyKind(mensagem.DataHora, DateTimeKind.Utc);

            var direcao = mensagem.Direcao == DirecaoMensagem.Entrada ? "entrada" : "saida";
            var criadaPor = string.IsNullOrWhiteSpace(mensagem.CriadaPor)
                ? mensagem.Direcao == DirecaoMensagem.Entrada ? "cliente" : "sistema"
                : mensagem.CriadaPor!;
            var tipoOriginal = string.IsNullOrWhiteSpace(mensagem.TipoOriginal) ? mensagem.Tipo : mensagem.TipoOriginal;
            var tipo = MessageTypeMapper.MapType(tipoOriginal, mensagem.Direcao, criadaPor);

            await using var cx = new NpgsqlConnection(_connectionString);
            await cx.OpenAsync();
            await using var tx = await cx.BeginTransactionAsync();

            var exists = await cx.ExecuteScalarAsync<int?>("SELECT 1 FROM conversas WHERE id = @Id LIMIT 1;", new { Id = mensagem.IdConversa }, tx);
            if (!exists.HasValue)
            {
                var digitsOnly = string.IsNullOrWhiteSpace(phoneNumberId)
                    ? string.Empty
                    : new string(phoneNumberId.Where(char.IsDigit).ToArray());
                var columns = await ObterColunasWabaPhoneAsync(cx, tx);
                var comparisons = new List<string>();

                if (columns.PhoneNumberId)
                {
                    comparisons.Add("phone_number_id = @Raw");
                    if (!string.IsNullOrWhiteSpace(digitsOnly))
                    {
                        comparisons.Add("regexp_replace(phone_number_id, '[^0-9]', '', 'g') = @Digits");
                    }
                }

                if (columns.DisplayPhoneNumber)
                {
                    comparisons.Add("display_phone_number = @Raw");
                    if (!string.IsNullOrWhiteSpace(digitsOnly))
                    {
                        comparisons.Add("regexp_replace(display_phone_number, '[^0-9]', '', 'g') = @Digits");
                    }
                }

                if (comparisons.Count == 0)
                {
                    throw new InvalidOperationException("Tabela waba_phone sem colunas de roteamento configuradas");
                }

                var sqlWaba = $@"
SELECT id_estabelecimento
  FROM waba_phone
 WHERE ativo = TRUE
   AND ({string.Join(" OR ", comparisons)})
 ORDER BY data_atualizacao DESC
 LIMIT 1;";
                var idEstab = await cx.ExecuteScalarAsync<Guid?>(
                    sqlWaba,
                    new { Raw = phoneNumberId, Digits = digitsOnly },
                    tx);
                if (!idEstab.HasValue || idEstab.Value == Guid.Empty)
                {
                    throw new InvalidOperationException("Estabelecimento nao encontrado para este WABA");
                }

                var telefone = TelefoneHelper.ToE164(idWa ?? string.Empty);
                var idCliente = await GarantirClienteTxAsync(cx, tx, telefone, idEstab.Value);

                await cx.ExecuteAsync(@"
INSERT INTO conversas (
    id, id_conversa_grupo, id_estabelecimento, id_cliente, canal, estado, status_atendimento,
    data_primeira_mensagem, data_ultima_mensagem, data_ultima_entrada, janela_24h_inicio,
    janela_24h_fim, qtd_nao_lidas, data_criacao, data_atualizacao
)
VALUES (
    @Id, @Id, @IdEstabelecimento, @IdCliente, 'whatsapp', 'aberto'::estado_conversa_enum,
    'com_bot', @Quando, @Quando, @Quando, @Quando, @Quando + interval '24 hour', 1, @Quando, @Quando
)
ON CONFLICT (id) DO NOTHING;",
                    new { Id = mensagem.IdConversa, IdEstabelecimento = idEstab.Value, IdCliente = idCliente, Quando = quando },
                    tx);
            }

            await cx.ExecuteAsync(@"
INSERT INTO mensagens (
    id, id_conversa, direcao, tipo, status, id_provedor, codigo_erro, mensagem_erro,
    tentativas, criada_por, data_envio, data_entrega, data_leitura, data_criacao, conteudo
)
VALUES (
    @Id, @IdConversa, @Direcao::direcao_mensagem_enum, @Tipo::tipo_mensagem_enum,
    @Status::status_mensagem_enum, @IdProvedor, @CodigoErro, @MensagemErro, @Tentativas,
    @CriadaPor, @DataEnvio, @DataEntrega, @DataLeitura, @DataCriacao, @Conteudo
)
ON CONFLICT DO NOTHING;",
                new
                {
                    Id = mensagem.Id == Guid.Empty ? Guid.NewGuid() : mensagem.Id,
                    IdConversa = mensagem.IdConversa,
                    Direcao = direcao,
                    Tipo = string.IsNullOrWhiteSpace(tipo) ? "texto" : tipo,
                    Status = MessageStatusMapper.NormalizeForDatabase(mensagem.Status, mensagem.Direcao),
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
                },
                tx);

            await cx.ExecuteAsync(@"
UPDATE conversas
   SET data_primeira_mensagem = COALESCE(data_primeira_mensagem, @Quando),
       data_ultima_mensagem = GREATEST(COALESCE(data_ultima_mensagem, @Quando), @Quando),
       data_ultima_entrada = CASE WHEN @Direcao = 'entrada' THEN @Quando ELSE data_ultima_entrada END,
       data_ultima_saida = CASE WHEN @Direcao = 'saida' THEN @Quando ELSE data_ultima_saida END,
       janela_24h_inicio = CASE WHEN @Direcao = 'entrada' AND janela_24h_inicio IS NULL THEN @Quando ELSE janela_24h_inicio END,
       janela_24h_fim = CASE WHEN @Direcao = 'entrada' THEN GREATEST(COALESCE(janela_24h_fim, @Quando + interval '24 hour'), @Quando + interval '24 hour') ELSE janela_24h_fim END,
       qtd_nao_lidas = CASE WHEN @Direcao = 'entrada' THEN COALESCE(qtd_nao_lidas, 0) + 1 ELSE qtd_nao_lidas END,
       data_atualizacao = NOW()
 WHERE id = @IdConversa;",
                new { IdConversa = mensagem.IdConversa, Quando = quando, Direcao = direcao },
                tx);

            await tx.CommitAsync();
        }

        private static async Task<(bool PhoneNumberId, bool DisplayPhoneNumber)> ObterColunasWabaPhoneAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction)
        {
            if (_cachedWabaPhoneColumns.HasValue)
            {
                return _cachedWabaPhoneColumns.Value;
            }

            var rows = await connection.QueryAsync<string>(
                @"SELECT column_name
                    FROM information_schema.columns
                   WHERE table_name = 'waba_phone';",
                transaction: transaction);

            var hash = rows.Select(r => r.Trim().ToLowerInvariant()).ToHashSet();
            _cachedWabaPhoneColumns = (
                hash.Contains("phone_number_id"),
                hash.Contains("display_phone_number"));
            return _cachedWabaPhoneColumns.Value;
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
            await ExpireExpiredConversationsForEstablishmentAsync(cx, idEstabelecimento);
            return (await cx.ExecuteScalarAsync<Guid?>(sql, new { IdCliente = idCliente, IdEstabelecimento = idEstabelecimento })) ?? Guid.Empty;
        }

        public async Task<Guid> ObterIdConversaAbertaPorGrupoAsync(Guid idConversaGrupo, Guid idEstabelecimento)
        {
            const string sql = @"SELECT id FROM conversas WHERE COALESCE(id_conversa_grupo, id) = @IdConversaGrupo AND id_estabelecimento = @IdEstabelecimento AND id <> COALESCE(id_conversa_grupo, id) AND estado NOT IN ('fechado_automaticamente'::estado_conversa_enum, 'fechado_agente'::estado_conversa_enum, 'arquivada'::estado_conversa_enum) ORDER BY COALESCE(data_ultima_mensagem, data_atualizacao, data_criacao) DESC LIMIT 1;";
            await using var cx = new NpgsqlConnection(_connectionString);
            await ExpireExpiredConversationGroupAsync(cx, idConversaGrupo, idEstabelecimento);
            return (await cx.ExecuteScalarAsync<Guid?>(sql, new { IdConversaGrupo = idConversaGrupo, IdEstabelecimento = idEstabelecimento })) ?? Guid.Empty;
        }

        public async Task<IReadOnlyList<Conversation>> ListarConversasAbertasPorTelefoneAsync(string telefoneE164)
        {
            if (string.IsNullOrWhiteSpace(telefoneE164))
            {
                return Array.Empty<Conversation>();
            }

            await using var cx = new NpgsqlConnection(_connectionString);
            var rows = (await cx.QueryAsync<ConversationRow>(
                SelectConversation + @"
 WHERE cl.telefone_e164 = @Telefone
    AND c.estado NOT IN ('fechado_automaticamente'::estado_conversa_enum, 'fechado_agente'::estado_conversa_enum, 'arquivada'::estado_conversa_enum)
  ORDER BY COALESCE(c.data_ultima_mensagem, c.data_atualizacao, c.data_criacao) DESC;",
                new { Telefone = telefoneE164 })).ToList();

            var abertas = new List<Conversation>(rows.Count);
            foreach (var row in rows)
            {
                var synced = await ExactSyncedAsync(cx, row.Id);
                if (synced == null)
                {
                    continue;
                }

                var status = CanonicalStatus(
                    synced.StatusAtendimento,
                    synced.Estado,
                    synced.IdAgenteAtribuido.HasValue ? ModoConversa.Humano : ModoConversa.Bot);
                if (IsClosedStatus(status))
                {
                    continue;
                }

                abertas.Add(ToConversation(synced));
            }

            return abertas;
        }

        public async Task<int> FecharConversasAbertasPorTelefoneAsync(string telefoneE164, int? idAgente, string? motivo, string? tipoFechamento = null, Guid? preservarConversaId = null)
        {
            if (string.IsNullOrWhiteSpace(telefoneE164))
            {
                return 0;
            }

            var closeType = NormalizeCloseType(tipoFechamento);
            await using var cx = new NpgsqlConnection(_connectionString);
            await cx.OpenAsync();
            return await FecharConversasAbertasPorTelefoneInternalAsync(cx, null, telefoneE164, idAgente, motivo, closeType, preservarConversaId);
        }

        public async Task<bool> ReiniciarConversaPorTelefoneAsync(string telefoneE164, Conversation novaConversa, int? idAgente, string? motivo, string? tipoFechamento = null)
        {
            if (string.IsNullOrWhiteSpace(telefoneE164))
            {
                throw new ArgumentException("telefoneE164 obrigatorio", nameof(telefoneE164));
            }

            await using var cx = new NpgsqlConnection(_connectionString);
            await cx.OpenAsync();
            await using var tx = await cx.BeginTransactionAsync();

            await FecharConversasAbertasPorTelefoneInternalAsync(
                cx,
                tx,
                telefoneE164,
                idAgente,
                motivo,
                NormalizeCloseType(tipoFechamento),
                preservarConversaId: null);

            var inserida = await InserirOuAtualizarAsync(cx, tx, novaConversa);
            await tx.CommitAsync();
            return inserida;
        }

        public async Task AtualizarEstadoAsync(Guid idConversa, EstadoConversa novoEstado)
        {
            await using var cx = new NpgsqlConnection(_connectionString);
            await cx.ExecuteAsync("UPDATE conversas SET estado = @Estado::estado_conversa_enum, status_atendimento = @StatusAtendimento, data_atualizacao = NOW() WHERE id = @Id;", new { Id = idConversa, Estado = EstadoDb(novoEstado), StatusAtendimento = LegacyStatusToCanonical(EstadoDb(novoEstado)) });
        }

        public async Task<IReadOnlyList<ConversationListItemDto>> ListarConversasAsync(string? estado, int? idAgente, bool incluirArquivadas, Guid? idEstabelecimento = null)
        {
            await using var cx = new NpgsqlConnection(_connectionString);
            await ExpireExpiredConversationsForEstablishmentAsync(cx, idEstabelecimento);
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

            var filtro = NormalizeStatus(estado);
            if (!string.IsNullOrWhiteSpace(filtro))
            {
                itens = itens.Where(i => NormalizeStatus(i.Estado) == filtro).ToList();
            }

            if (idAgente.HasValue)
            {
                itens = itens.Where(i => i.IdAgenteAtribuido == idAgente.Value).ToList();
            }

            if (!incluirArquivadas)
            {
                itens = itens.Where(i => !string.Equals(i.Estado, "arquivada", StringComparison.OrdinalIgnoreCase)).ToList();
            }

            return itens.OrderByDescending(i => i.UltimaMensagemData ?? i.DataUltimaMensagem ?? i.DataAtualizacao).ToList();
        }

        public async Task<ConversationHistoryDto?> ObterHistoricoConversaAsync(Guid idConversa, int page, int pageSize, Guid? idEstabelecimento = null)
        {
            page = Math.Max(1, page);
            pageSize = pageSize <= 0 ? 50 : pageSize;

            await using var cx = new NpgsqlConnection(_connectionString);
            var exact = await ExactSyncedAsync(cx, idConversa);
            if (exact == null)
            {
                return null;
            }

            if (idEstabelecimento.HasValue && IsCentral(idEstabelecimento.Value))
            {
                var root = await RootAsync(cx, exact);
                if (root == null || root.IdEstabelecimento != idEstabelecimento.Value)
                {
                    return null;
                }

                var mensagens = (await GroupMessagesAsync(cx, root.IdConversaGrupo, page, pageSize)).ToList();
                var total = await cx.ExecuteScalarAsync<int>("SELECT COUNT(1) FROM mensagens m JOIN conversas c ON c.id = m.id_conversa WHERE COALESCE(c.id_conversa_grupo, c.id) = @GroupId;", new { GroupId = root.IdConversaGrupo });

                return new ConversationHistoryDto
                {
                    Conversa = await BuildCentralDetailsAsync(cx, root),
                    Controle = await BuildControlAsync(cx, exact, idEstabelecimento.Value),
                    Eventos = await ListarEventosInternalAsync(cx, root.IdConversaGrupo),
                    Mensagens = mensagens,
                    Page = page,
                    PageSize = pageSize,
                    Total = total
                };
            }

            if (idEstabelecimento.HasValue && exact.IdEstabelecimento != idEstabelecimento.Value)
            {
                return null;
            }

            var count = await cx.ExecuteScalarAsync<int>(@"SELECT COUNT(1) FROM mensagens m JOIN conversas c ON c.id = m.id_conversa WHERE c.id_cliente = @IdCliente AND c.id_estabelecimento = @IdEstabelecimento;", new { IdCliente = exact.IdCliente, IdEstabelecimento = exact.IdEstabelecimento });
            return new ConversationHistoryDto
            {
                Conversa = Details(exact),
                Controle = await BuildControlAsync(cx, exact, exact.IdEstabelecimento),
                Eventos = await ListarEventosClienteAsync(cx, exact.IdCliente, exact.IdEstabelecimento),
                Mensagens = (await ClientMessagesAsync(cx, exact.IdCliente, exact.IdEstabelecimento, page, pageSize)).ToList(),
                Page = page,
                PageSize = pageSize,
                Total = count
            };
        }

        public async Task<bool> AtribuirConversaAsync(Guid idConversa, int idAgente, Guid? idEstabelecimento = null)
        {
            var alvo = await ResolveOperationTargetAsync(idConversa, idEstabelecimento);
            if (alvo == null)
            {
                return false;
            }

            await using var cx = new NpgsqlConnection(_connectionString);
            return await cx.ExecuteAsync(@"
UPDATE conversas
   SET id_agente_atribuido = @IdAgente,
       estado = 'em_atendimento'::estado_conversa_enum,
       status_atendimento = 'em_andamento',
       data_fechamento = NULL,
       motivo_fechamento = NULL,
       fechado_por_id = NULL,
       data_atualizacao = NOW()
 WHERE id = @Id;",
                new { Id = alvo.Id, IdAgente = idAgente }) > 0;
        }

        public async Task<bool> FecharConversaAsync(Guid idConversa, int? idAgente, string? motivo, Guid? idEstabelecimento = null, string? tipoFechamento = null)
        {
            var alvo = await ResolveOperationTargetAsync(idConversa, idEstabelecimento);
            if (alvo == null)
            {
                return false;
            }

            var closeType = NormalizeCloseType(tipoFechamento);
            var legacyState = string.Equals(closeType, "inatividade", StringComparison.OrdinalIgnoreCase) ? "fechado_automaticamente" : "fechado_agente";
            var canonicalStatus = string.Equals(closeType, "inatividade", StringComparison.OrdinalIgnoreCase) ? "encerrada_inatividade" : "encerrada_manual";

            await using var cx = new NpgsqlConnection(_connectionString);
            return await cx.ExecuteAsync(@"
UPDATE conversas
   SET estado = @Estado::estado_conversa_enum,
       status_atendimento = @StatusAtendimento,
       motivo_fechamento = @Motivo,
       fechado_por_id = @FechadoPorId,
       data_fechamento = NOW(),
       data_atualizacao = NOW()
 WHERE id = @Id;",
                new { Id = alvo.Id, Estado = legacyState, StatusAtendimento = canonicalStatus, Motivo = string.IsNullOrWhiteSpace(motivo) ? null : motivo.Trim(), FechadoPorId = idAgente }) > 0;
        }

        public async Task<bool> FecharGrupoConversaAsync(Guid idConversaGrupo, int? idAgente, string? motivo, string? tipoFechamento = null)
        {
            if (idConversaGrupo == Guid.Empty)
            {
                return false;
            }

            var closeType = NormalizeCloseType(tipoFechamento);
            var legacyState = string.Equals(closeType, "inatividade", StringComparison.OrdinalIgnoreCase) ? "fechado_automaticamente" : "fechado_agente";
            var canonicalStatus = string.Equals(closeType, "inatividade", StringComparison.OrdinalIgnoreCase) ? "encerrada_inatividade" : "encerrada_manual";

            await using var cx = new NpgsqlConnection(_connectionString);
            return await cx.ExecuteAsync(@"
UPDATE conversas
   SET estado = @Estado::estado_conversa_enum,
       status_atendimento = @StatusAtendimento,
       motivo_fechamento = @Motivo,
       fechado_por_id = @FechadoPorId,
       data_fechamento = COALESCE(data_fechamento, NOW()),
       data_atualizacao = NOW()
 WHERE COALESCE(id_conversa_grupo, id) = @GroupId
   AND estado NOT IN ('fechado_automaticamente'::estado_conversa_enum, 'fechado_agente'::estado_conversa_enum, 'arquivada'::estado_conversa_enum);",
                new
                {
                    GroupId = idConversaGrupo,
                    Estado = legacyState,
                    StatusAtendimento = canonicalStatus,
                    Motivo = string.IsNullOrWhiteSpace(motivo) ? null : motivo.Trim(),
                    FechadoPorId = idAgente
                }) > 0;
        }

        public async Task<ConversationDetailsDto?> ArquivarConversaAsync(Guid idConversa, Guid? idEstabelecimento = null)
        {
            var alvo = await ResolveOperationTargetAsync(idConversa, idEstabelecimento);
            if (alvo == null)
            {
                return null;
            }

            await using var cx = new NpgsqlConnection(_connectionString);
            if (await cx.ExecuteAsync("UPDATE conversas SET estado = 'arquivada'::estado_conversa_enum, status_atendimento = 'encerrada_manual', data_atualizacao = NOW() WHERE id = @Id;", new { Id = alvo.Id }) == 0)
            {
                return null;
            }

            return await ObterDetalhesConversaAsync(idConversa, idEstabelecimento);
        }

        public async Task<ConversationDetailsDto?> ObterDetalhesConversaAsync(Guid idConversa, Guid? idEstabelecimento = null)
        {
            await using var cx = new NpgsqlConnection(_connectionString);
            var row = await ExactSyncedAsync(cx, idConversa);
            if (row == null)
            {
                return null;
            }

            if (idEstabelecimento.HasValue && IsCentral(idEstabelecimento.Value))
            {
                var root = await RootAsync(cx, row);
                return root != null && root.IdEstabelecimento == idEstabelecimento.Value ? await BuildCentralDetailsAsync(cx, root) : null;
            }

            return !idEstabelecimento.HasValue || row.IdEstabelecimento == idEstabelecimento.Value ? Details(row) : null;
        }

        public async Task<bool> AtualizarStatusAtendimentoAsync(Guid idConversa, string status, int? idAgente, string? agenteNome, Guid? idEstabelecimento = null)
        {
            var alvo = await ResolveOperationTargetAsync(idConversa, idEstabelecimento);
            if (alvo == null)
            {
                return false;
            }

            var canonicalStatus = CanonicalStatus(status, alvo.Estado, alvo.IdAgenteAtribuido.HasValue ? ModoConversa.Humano : ModoConversa.Bot);
            var clearAgent = string.Equals(canonicalStatus, "com_bot", StringComparison.OrdinalIgnoreCase);

            await using var cx = new NpgsqlConnection(_connectionString);
            return await cx.ExecuteAsync(@"
UPDATE conversas
   SET status_atendimento = @StatusAtendimento,
       estado = @Estado::estado_conversa_enum,
       id_agente_atribuido = CASE WHEN @ClearAgent THEN NULL ELSE COALESCE(@IdAgente, id_agente_atribuido) END,
       data_fechamento = CASE WHEN @Estado IN ('aberto', 'em_atendimento') THEN NULL ELSE data_fechamento END,
       motivo_fechamento = CASE WHEN @Estado IN ('aberto', 'em_atendimento') THEN NULL ELSE motivo_fechamento END,
       fechado_por_id = CASE WHEN @Estado IN ('aberto', 'em_atendimento') THEN NULL ELSE fechado_por_id END,
       data_atualizacao = NOW()
 WHERE id = @Id;",
                new { Id = alvo.Id, StatusAtendimento = canonicalStatus, Estado = LegacyStateFromCanonicalStatus(canonicalStatus), IdAgente = idAgente, ClearAgent = clearAgent }) > 0;
        }

        public async Task<bool> VoltarParaBotAsync(Guid idConversa, Guid? idEstabelecimento = null)
        {
            var alvo = await ResolveOperationTargetAsync(idConversa, idEstabelecimento);
            if (alvo == null)
            {
                return false;
            }

            await using var cx = new NpgsqlConnection(_connectionString);
            return await cx.ExecuteAsync(@"
UPDATE conversas
   SET id_agente_atribuido = NULL,
       estado = 'aberto'::estado_conversa_enum,
       status_atendimento = 'com_bot',
       data_fechamento = NULL,
       motivo_fechamento = NULL,
       fechado_por_id = NULL,
       data_atualizacao = NOW()
 WHERE id = @Id;",
                new { Id = alvo.Id }) > 0;
        }

        public async Task<bool> ReabrirConversaAsync(Guid idConversa, Guid? idEstabelecimento = null)
        {
            var alvo = await ResolveOperationTargetAsync(idConversa, idEstabelecimento);
            if (alvo == null)
            {
                return false;
            }

            await using var cx = new NpgsqlConnection(_connectionString);
            return await cx.ExecuteAsync(@"
UPDATE conversas
   SET estado = 'aberto'::estado_conversa_enum,
       status_atendimento = 'com_bot',
       id_agente_atribuido = NULL,
       motivo_fechamento = NULL,
       fechado_por_id = NULL,
       data_fechamento = NULL,
       data_atualizacao = NOW()
 WHERE id = @Id;",
                new { Id = alvo.Id }) > 0;
        }

        public async Task<bool> MarcarComoLidaAsync(Guid idConversa, Guid? idEstabelecimento = null)
        {
            await using var cx = new NpgsqlConnection(_connectionString);
            var alvo = idEstabelecimento.HasValue
                ? await ResolveForEstablishmentAsync(cx, idConversa, idEstabelecimento.Value)
                : await ExactSyncedAsync(cx, idConversa);

            if (alvo == null)
            {
                return false;
            }

            return await cx.ExecuteAsync(@"
UPDATE conversas
   SET qtd_nao_lidas = 0,
       data_ultima_leitura = NOW(),
       data_atualizacao = NOW()
 WHERE COALESCE(id_conversa_grupo, id) = @GroupId;",
                new { GroupId = alvo.IdConversaGrupo }) > 0;
        }

        public async Task<ConversationControlDto?> ObterControleConversaAsync(Guid idConversa, Guid? idEstabelecimento = null)
        {
            await using var cx = new NpgsqlConnection(_connectionString);
            var alvo = idEstabelecimento.HasValue
                ? await ResolveForEstablishmentAsync(cx, idConversa, idEstabelecimento.Value)
                : await ExactSyncedAsync(cx, idConversa);

            if (alvo == null)
            {
                return null;
            }

            return await BuildControlAsync(cx, alvo, idEstabelecimento ?? alvo.IdEstabelecimento);
        }

        public async Task RegistrarEventoAsync(Guid idConversa, ConversationEventDto evento, Guid? idEstabelecimento = null)
        {
            await using var cx = new NpgsqlConnection(_connectionString);
            var alvo = idEstabelecimento.HasValue
                ? await ResolveForEstablishmentAsync(cx, idConversa, idEstabelecimento.Value)
                : await ExactSyncedAsync(cx, idConversa);

            if (alvo == null)
            {
                return;
            }

            const string sql = @"
INSERT INTO conversa_eventos (
    id, id_conversa, id_conversa_grupo, tipo, origem, ator_usuario_id, ator_agente_id,
    ator_nome, status_anterior, status_novo, motivo, tipo_fechamento, dados, data_criacao
)
VALUES (
    @Id, @IdConversa, @IdConversaGrupo, @Tipo, @Origem, @AtorUsuarioId, @AtorAgenteId,
    @AtorNome, @StatusAnterior, @StatusNovo, @Motivo, @TipoFechamento, CAST(@Dados AS jsonb), @DataCriacao
);";

            await cx.ExecuteAsync(sql, new
            {
                Id = evento.Id == Guid.Empty ? Guid.NewGuid() : evento.Id,
                IdConversa = alvo.Id,
                IdConversaGrupo = alvo.IdConversaGrupo,
                Tipo = evento.Type,
                Origem = string.IsNullOrWhiteSpace(evento.Source) ? "api" : evento.Source,
                AtorUsuarioId = evento.ActorUserId,
                AtorAgenteId = evento.ActorAgentId,
                AtorNome = string.IsNullOrWhiteSpace(evento.ActorName) ? null : evento.ActorName,
                StatusAnterior = string.IsNullOrWhiteSpace(evento.FromStatus) ? null : NormalizeStatus(evento.FromStatus),
                StatusNovo = string.IsNullOrWhiteSpace(evento.ToStatus) ? null : NormalizeStatus(evento.ToStatus),
                Motivo = string.IsNullOrWhiteSpace(evento.Reason) ? null : evento.Reason,
                TipoFechamento = string.IsNullOrWhiteSpace(evento.CloseType) ? null : evento.CloseType,
                Dados = evento.Data == null || evento.Data.Count == 0 ? null : JsonSerializer.Serialize(evento.Data),
                DataCriacao = evento.At == default ? DateTime.UtcNow : evento.At
            });
        }

        public async Task<IReadOnlyList<ConversationEventDto>> ListarEventosConversaAsync(Guid idConversa, Guid? idEstabelecimento = null)
        {
            await using var cx = new NpgsqlConnection(_connectionString);
            var alvo = idEstabelecimento.HasValue
                ? await ResolveForEstablishmentAsync(cx, idConversa, idEstabelecimento.Value)
                : await ExactSyncedAsync(cx, idConversa);

            return alvo == null
                ? Array.Empty<ConversationEventDto>()
                : await ListarEventosInternalAsync(cx, alvo.IdConversaGrupo);
        }

        public async Task<IReadOnlyList<ConversationAgentDto>> ListarAgentesAsync(Guid? idEstabelecimento = null)
        {
            const string sql = @"SELECT a.id,
                                        a.usuarioid AS UsuarioId,
                                        COALESCE(u.nome, CONCAT('Agente ', a.id::text)) AS Nome
                                   FROM agentes a
                                   LEFT JOIN usuario u ON u.id = a.usuarioid
                               ORDER BY COALESCE(u.nome, CONCAT('Agente ', a.id::text));";
            await using var cx = new NpgsqlConnection(_connectionString);
            var rows = await cx.QueryAsync<ConversationAgentDto>(sql);
            return rows.AsList();
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

        public async Task LimparContextoCompletoAsync(Guid idConversa)
        {
            await using var cx = new NpgsqlConnection(_connectionString);
            await cx.ExecuteAsync("UPDATE conversas SET contexto_estado = NULL, data_atualizacao = NOW() WHERE id = @Id;", new { Id = idConversa });
        }

        public async Task LimparContextoGrupoCompletoAsync(Guid groupId)
        {
            await using var cx = new NpgsqlConnection(_connectionString);
            await cx.ExecuteAsync(
                "UPDATE conversas SET contexto_estado = NULL, data_atualizacao = NOW() WHERE COALESCE(id_conversa_grupo, id) = @GroupId;",
                new { GroupId = groupId });
        }

        private async Task<ConversationRow?> ResolveForEstablishmentAsync(NpgsqlConnection cx, Guid id, Guid idEstabelecimento)
        {
            var exact = await ExactSyncedAsync(cx, id);
            if (exact == null)
            {
                return null;
            }

            if (IsCentral(idEstabelecimento))
            {
                var root = await RootAsync(cx, exact);
                if (root == null || root.IdEstabelecimento != idEstabelecimento)
                {
                    return null;
                }

                return exact.Id == root.Id ? await OperationalAsync(cx, root.IdConversaGrupo) ?? root : exact;
            }

            return exact.IdEstabelecimento == idEstabelecimento ? exact : null;
        }

        private async Task<ConversationRow?> ResolveOperationTargetAsync(Guid idConversa, Guid? idEstabelecimento)
        {
            await using var cx = new NpgsqlConnection(_connectionString);
            return idEstabelecimento.HasValue
                ? await ResolveForEstablishmentAsync(cx, idConversa, idEstabelecimento.Value)
                : await ExactSyncedAsync(cx, idConversa);
        }

        private async Task<ConversationRow?> ExactSyncedAsync(NpgsqlConnection cx, Guid id)
        {
            var exact = await ExactAsync(cx, id);
            if (exact == null)
            {
                return null;
            }

            await ExpireExpiredConversationGroupAsync(cx, exact.IdConversaGrupo, exact.IdEstabelecimento);
            return await ExactAsync(cx, id);
        }

        private Task<ConversationRow?> ExactAsync(NpgsqlConnection cx, Guid id) => cx.QueryFirstOrDefaultAsync<ConversationRow>(SelectConversation + " WHERE c.id = @Id LIMIT 1;", new { Id = id });
        private Task<ConversationRow?> RootAsync(NpgsqlConnection cx, ConversationRow row) => cx.QueryFirstOrDefaultAsync<ConversationRow>(SelectConversation + " WHERE c.id = @Id LIMIT 1;", new { Id = row.IdConversaGrupo });
        private Task<ConversationRow?> OperationalAsync(NpgsqlConnection cx, Guid groupId) => cx.QueryFirstOrDefaultAsync<ConversationRow>(SelectConversation + " WHERE COALESCE(c.id_conversa_grupo, c.id) = @GroupId ORDER BY CASE WHEN c.id <> COALESCE(c.id_conversa_grupo, c.id) AND c.estado NOT IN ('fechado_automaticamente'::estado_conversa_enum, 'fechado_agente'::estado_conversa_enum, 'arquivada'::estado_conversa_enum) THEN 0 WHEN c.id <> COALESCE(c.id_conversa_grupo, c.id) THEN 1 ELSE 2 END, COALESCE(c.data_ultima_mensagem, c.data_atualizacao, c.data_criacao) DESC LIMIT 1;", new { GroupId = groupId });

        private Task ExpireExpiredConversationsForEstablishmentAsync(NpgsqlConnection cx, Guid? idEstabelecimento)
            => ExpireExpiredConversationsAsync(cx, idEstabelecimento, null);

        private Task ExpireExpiredConversationGroupAsync(NpgsqlConnection cx, Guid groupId, Guid? idEstabelecimento = null)
            => ExpireExpiredConversationsAsync(cx, idEstabelecimento, groupId);

        private async Task ExpireExpiredConversationsAsync(NpgsqlConnection cx, Guid? idEstabelecimento, Guid? groupId)
        {
            var candidates = (await cx.QueryAsync<ExpiredConversationCandidateRow>(@"
SELECT c.id AS Id,
       COALESCE(c.id_conversa_grupo, c.id) AS IdConversaGrupo,
       c.id_estabelecimento AS IdEstabelecimento,
       c.estado::text AS Estado,
       COALESCE(NULLIF(c.status_atendimento, ''), '') AS StatusAtendimento,
       c.id_agente_atribuido AS IdAgenteAtribuido
  FROM conversas c
 WHERE c.janela_24h_fim IS NOT NULL
   AND c.janela_24h_fim <= NOW()
   AND c.estado IN ('aberto'::estado_conversa_enum, 'em_atendimento'::estado_conversa_enum)
   AND (@IdEstabelecimento IS NULL OR c.id_estabelecimento = @IdEstabelecimento)
   AND (@GroupId IS NULL OR COALESCE(c.id_conversa_grupo, c.id) = @GroupId);",
                new { IdEstabelecimento = idEstabelecimento, GroupId = groupId })).ToList();

            if (candidates.Count == 0)
            {
                return;
            }

            var candidateIds = candidates.Select(candidate => candidate.Id).Distinct().ToArray();
            var updatedIds = (await cx.QueryAsync<Guid>(@"
UPDATE conversas
   SET estado = 'fechado_automaticamente'::estado_conversa_enum,
       status_atendimento = 'encerrada_inatividade',
       motivo_fechamento = @Motivo,
       fechado_por_id = NULL,
       data_fechamento = COALESCE(data_fechamento, NOW()),
       data_atualizacao = NOW()
 WHERE id = ANY(@Ids)
   AND estado IN ('aberto'::estado_conversa_enum, 'em_atendimento'::estado_conversa_enum)
RETURNING id;",
                new { Ids = candidateIds, Motivo = WindowExpiredReason })).ToHashSet();

            if (updatedIds.Count == 0)
            {
                return;
            }

            var now = DateTime.UtcNow;
            var eventos = candidates
                .Where(candidate => updatedIds.Contains(candidate.Id))
                .Select(candidate => new
                {
                    Id = Guid.NewGuid(),
                    IdConversa = candidate.Id,
                    IdConversaGrupo = candidate.IdConversaGrupo,
                    Tipo = "closed",
                    Origem = "system",
                    AtorUsuarioId = (int?)null,
                    AtorAgenteId = (int?)null,
                    AtorNome = (string?)null,
                    StatusAnterior = CanonicalStatus(
                        candidate.StatusAtendimento,
                        candidate.Estado,
                        candidate.IdAgenteAtribuido.HasValue ? ModoConversa.Humano : ModoConversa.Bot),
                    StatusNovo = "encerrada_inatividade",
                    Motivo = WindowExpiredReason,
                    TipoFechamento = "inatividade",
                    Dados = (string?)null,
                    DataCriacao = now
                })
                .ToArray();

            if (eventos.Length == 0)
            {
                return;
            }

            await cx.ExecuteAsync(@"
INSERT INTO conversa_eventos (
    id, id_conversa, id_conversa_grupo, tipo, origem, ator_usuario_id, ator_agente_id,
    ator_nome, status_anterior, status_novo, motivo, tipo_fechamento, dados, data_criacao
)
VALUES (
    @Id, @IdConversa, @IdConversaGrupo, @Tipo, @Origem, @AtorUsuarioId, @AtorAgenteId,
    @AtorNome, @StatusAnterior, @StatusNovo, @Motivo, @TipoFechamento, CAST(@Dados AS jsonb), @DataCriacao
);",
                eventos);
        }

        private async Task<int> FecharConversasAbertasPorTelefoneInternalAsync(
            NpgsqlConnection cx,
            NpgsqlTransaction? tx,
            string telefoneE164,
            int? idAgente,
            string? motivo,
            string closeType,
            Guid? preservarConversaId)
        {
            var legacyState = string.Equals(closeType, "inatividade", StringComparison.OrdinalIgnoreCase)
                ? "fechado_automaticamente"
                : "fechado_agente";
            var canonicalStatus = string.Equals(closeType, "inatividade", StringComparison.OrdinalIgnoreCase)
                ? "encerrada_inatividade"
                : "encerrada_manual";

            return await cx.ExecuteAsync(@"
UPDATE conversas c
   SET estado = @Estado::estado_conversa_enum,
       status_atendimento = @StatusAtendimento,
       motivo_fechamento = @Motivo,
       fechado_por_id = @FechadoPorId,
       data_fechamento = COALESCE(c.data_fechamento, NOW()),
       data_atualizacao = NOW()
  FROM clientes cl
 WHERE c.id_cliente = cl.id
   AND c.id_estabelecimento = cl.id_estabelecimento
   AND cl.telefone_e164 = @Telefone
   AND c.estado NOT IN ('fechado_automaticamente'::estado_conversa_enum, 'fechado_agente'::estado_conversa_enum, 'arquivada'::estado_conversa_enum)
   AND (@PreservarConversaId IS NULL OR c.id <> @PreservarConversaId);",
                new
                {
                    Telefone = telefoneE164,
                    Estado = legacyState,
                    StatusAtendimento = canonicalStatus,
                    Motivo = string.IsNullOrWhiteSpace(motivo) ? null : motivo.Trim(),
                    FechadoPorId = idAgente,
                    PreservarConversaId = preservarConversaId
                },
                tx);
        }

        private async Task<ConversationListItemDto> BuildStandardListItemAsync(NpgsqlConnection cx, ConversationRow row)
        {
            var last = await LastMessageAsync(cx, row.Id, false);
            return new ConversationListItemDto
            {
                Id = row.Id,
                IdCliente = row.IdCliente,
                IdConversaOperacional = row.Id,
                IdConversaGrupo = row.IdConversaGrupo,
                ClienteNome = row.ClienteNome,
                ClienteNumero = row.TelefoneCliente,
                Estado = CanonicalStatus(row.StatusAtendimento, row.Estado, row.IdAgenteAtribuido.HasValue ? ModoConversa.Humano : ModoConversa.Bot),
                IdAgenteAtribuido = row.IdAgenteAtribuido,
                AgenteNome = row.AgenteNome,
                QtdNaoLidas = row.QtdNaoLidas,
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
                IdConversaOperacional = op.Id,
                IdConversaGrupo = root.IdConversaGrupo,
                ClienteNome = root.ClienteNome,
                ClienteNumero = root.TelefoneCliente ?? op.TelefoneCliente,
                Estado = CanonicalStatus(op.StatusAtendimento, op.Estado, op.IdAgenteAtribuido.HasValue ? ModoConversa.Humano : ModoConversa.Bot),
                IdAgenteAtribuido = op.IdAgenteAtribuido,
                AgenteNome = op.AgenteNome,
                QtdNaoLidas = agg.UnreadCount,
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
                IdConversaOperacional = op.Id,
                IdConversaGrupo = root.IdConversaGrupo,
                ClienteNome = root.ClienteNome,
                ClienteNumero = root.TelefoneCliente ?? op.TelefoneCliente,
                Estado = CanonicalStatus(op.StatusAtendimento, op.Estado, op.IdAgenteAtribuido.HasValue ? ModoConversa.Humano : ModoConversa.Bot),
                IdAgenteAtribuido = op.IdAgenteAtribuido,
                AgenteNome = op.AgenteNome,
                QtdNaoLidas = agg.UnreadCount,
                DataPrimeiraMensagem = agg.FirstMessage ?? root.DataPrimeiraMensagem ?? root.DataCriacao,
                DataUltimaMensagem = agg.LastMessage ?? op.DataUltimaMensagem ?? root.DataAtualizacao,
                DataCriacao = root.DataCriacao,
                DataAtualizacao = agg.LastUpdate ?? op.DataAtualizacao,
                DataFechamento = op.DataFechamento,
                FechadoPorId = op.FechadoPorId,
                MotivoFechamento = op.MotivoFechamento
            };
        }

        private ConversationDetailsDto Details(ConversationRow row) => new()
        {
            Id = row.Id,
            IdCliente = row.IdCliente,
            IdConversaOperacional = row.Id,
            IdConversaGrupo = row.IdConversaGrupo,
            ClienteNome = row.ClienteNome,
            ClienteNumero = row.TelefoneCliente,
            Estado = CanonicalStatus(row.StatusAtendimento, row.Estado, row.IdAgenteAtribuido.HasValue ? ModoConversa.Humano : ModoConversa.Bot),
            IdAgenteAtribuido = row.IdAgenteAtribuido,
            AgenteNome = row.AgenteNome,
            QtdNaoLidas = row.QtdNaoLidas,
            DataPrimeiraMensagem = row.DataPrimeiraMensagem ?? row.DataCriacao,
            DataUltimaMensagem = row.DataUltimaMensagem ?? row.DataAtualizacao,
            DataCriacao = row.DataCriacao,
            DataAtualizacao = row.DataAtualizacao,
            DataFechamento = row.DataFechamento,
            FechadoPorId = row.FechadoPorId,
            MotivoFechamento = row.MotivoFechamento
        };

        private async Task<ConversationControlDto> BuildControlAsync(NpgsqlConnection cx, ConversationRow row, Guid idEstabelecimento)
        {
            var status = CanonicalStatus(row.StatusAtendimento, row.Estado, row.IdAgenteAtribuido.HasValue ? ModoConversa.Humano : ModoConversa.Bot);
            var windowExpired = IsWindowExpired(row.Janela24hFim);
            var closed = IsClosedStatus(status);
            var unreadCount = IsCentral(idEstabelecimento) ? await GroupUnreadCountAsync(cx, row.IdConversaGrupo) : row.QtdNaoLidas;
            return new ConversationControlDto
            {
                ConversationId = row.Id,
                ClientId = row.IdCliente,
                ConversationGroupId = row.IdConversaGrupo,
                Status = status,
                CanBotReply = string.Equals(status, "com_bot", StringComparison.OrdinalIgnoreCase) && !closed && !windowExpired,
                CanManualReply = IsHumanStatus(status) && !closed && !windowExpired,
                SendBlockReasonCode = ResolveSendBlockReasonCode(row, status),
                AssignedAgentId = row.IdAgenteAtribuido,
                AssignedAgentName = row.AgenteNome,
                LastInteractionAt = row.DataUltimaMensagem ?? row.DataAtualizacao,
                AutoCloseAt = row.Janela24hFim,
                UnreadCount = unreadCount
            };
        }

        private Task<GroupAggregate> GroupAggregateAsync(NpgsqlConnection cx, Guid groupId) => cx.QueryFirstAsync<GroupAggregate>("SELECT MIN(COALESCE(data_primeira_mensagem, data_criacao)) AS FirstMessage, MAX(COALESCE(data_ultima_mensagem, data_atualizacao, data_criacao)) AS LastMessage, MAX(data_atualizacao) AS LastUpdate, COALESCE(SUM(COALESCE(qtd_nao_lidas, 0)), 0)::int AS UnreadCount FROM conversas WHERE COALESCE(id_conversa_grupo, id) = @GroupId;", new { GroupId = groupId });
        private Task<int> GroupUnreadCountAsync(NpgsqlConnection cx, Guid groupId) => cx.ExecuteScalarAsync<int>("SELECT COALESCE(SUM(COALESCE(qtd_nao_lidas, 0)), 0)::int FROM conversas WHERE COALESCE(id_conversa_grupo, id) = @GroupId;", new { GroupId = groupId });
        private Task<MessagePreview?> LastMessageAsync(NpgsqlConnection cx, Guid id, bool byGroup) => cx.QueryFirstOrDefaultAsync<MessagePreview>(@"SELECT m.conteudo AS Conteudo, COALESCE(m.data_criacao, m.data_envio) AS DataCriacao, COALESCE(m.criada_por, '') AS CriadaPor FROM mensagens m JOIN conversas c ON c.id = m.id_conversa WHERE " + (byGroup ? "COALESCE(c.id_conversa_grupo, c.id) = @Id" : "m.id_conversa = @Id") + " ORDER BY COALESCE(m.data_criacao, m.data_envio) DESC LIMIT 1;", new { Id = id });
        private Task<IEnumerable<ConversationMessageItemDto>> ExactMessagesAsync(NpgsqlConnection cx, Guid id, int page, int pageSize) => cx.QueryAsync<ConversationMessageItemDto>(@"SELECT m.id AS Id, COALESCE(m.criada_por, '') AS CriadaPor, COALESCE(m.conteudo, '') AS Conteudo, COALESCE(m.tipo::text, 'texto') AS Tipo, COALESCE(m.status::text, '') AS Status, m.data_envio AS DataEnvio, COALESCE(m.data_criacao, m.data_envio, NOW()) AS DataCriacao FROM mensagens m WHERE m.id_conversa = @Id ORDER BY COALESCE(m.data_criacao, m.data_envio) ASC LIMIT @PageSize OFFSET @Offset;", new { Id = id, PageSize = pageSize, Offset = (page - 1) * pageSize });
        private Task<IEnumerable<ConversationMessageItemDto>> GroupMessagesAsync(NpgsqlConnection cx, Guid groupId, int page, int pageSize) => cx.QueryAsync<ConversationMessageItemDto>(@"SELECT m.id AS Id, COALESCE(m.criada_por, '') AS CriadaPor, COALESCE(m.conteudo, '') AS Conteudo, COALESCE(m.tipo::text, 'texto') AS Tipo, COALESCE(m.status::text, '') AS Status, m.data_envio AS DataEnvio, COALESCE(m.data_criacao, m.data_envio, NOW()) AS DataCriacao FROM mensagens m JOIN conversas c ON c.id = m.id_conversa WHERE COALESCE(c.id_conversa_grupo, c.id) = @GroupId ORDER BY COALESCE(m.data_criacao, m.data_envio) ASC LIMIT @PageSize OFFSET @Offset;", new { GroupId = groupId, PageSize = pageSize, Offset = (page - 1) * pageSize });
        private Task<IEnumerable<ConversationMessageItemDto>> ClientMessagesAsync(NpgsqlConnection cx, Guid idCliente, Guid idEstabelecimento, int page, int pageSize) => cx.QueryAsync<ConversationMessageItemDto>(@"SELECT m.id AS Id, COALESCE(m.criada_por, '') AS CriadaPor, COALESCE(m.conteudo, '') AS Conteudo, COALESCE(m.tipo::text, 'texto') AS Tipo, COALESCE(m.status::text, '') AS Status, m.data_envio AS DataEnvio, COALESCE(m.data_criacao, m.data_envio, NOW()) AS DataCriacao FROM mensagens m JOIN conversas c ON c.id = m.id_conversa WHERE c.id_cliente = @IdCliente AND c.id_estabelecimento = @IdEstabelecimento ORDER BY COALESCE(m.data_criacao, m.data_envio) ASC LIMIT @PageSize OFFSET @Offset;", new { IdCliente = idCliente, IdEstabelecimento = idEstabelecimento, PageSize = pageSize, Offset = (page - 1) * pageSize });

        private async Task<IReadOnlyList<ConversationEventDto>> ListarEventosInternalAsync(NpgsqlConnection cx, Guid groupId)
        {
            var persistedRows = (await cx.QueryAsync<ConversationEventRow>(@"SELECT id AS Id, tipo AS Type, data_criacao AS At, ator_nome AS ActorName, status_anterior AS FromStatus, status_novo AS ToStatus, motivo AS Reason, tipo_fechamento AS CloseType, origem AS Source, ator_usuario_id AS ActorUserId, ator_agente_id AS ActorAgentId, dados::text AS DataJson FROM conversa_eventos WHERE id_conversa_grupo = @GroupId ORDER BY data_criacao ASC;", new { GroupId = groupId })).ToList();
            var persisted = persistedRows.Select(ToEventDto).ToList();
            var seeds = (await cx.QueryAsync<BaseEventSeed>(@"SELECT c.id AS Id, COALESCE(c.id_conversa_grupo, c.id) AS IdConversaGrupo, c.data_primeira_mensagem AS DataPrimeiraMensagem, c.data_criacao AS DataCriacao, c.data_fechamento AS DataFechamento, c.motivo_fechamento AS MotivoFechamento, c.estado::text AS Estado, COALESCE(NULLIF(c.status_atendimento, ''), '') AS StatusAtendimento, CASE WHEN c.id_agente_atribuido IS NULL THEN NULL ELSE COALESCE(u.nome, CONCAT('Agente ', c.id_agente_atribuido::text)) END AS AgenteNome FROM conversas c LEFT JOIN agentes a ON a.id = c.id_agente_atribuido LEFT JOIN usuario u ON u.id = a.usuarioid WHERE COALESCE(c.id_conversa_grupo, c.id) = @GroupId ORDER BY c.data_criacao ASC;", new { GroupId = groupId })).ToList();

            var baseEvents = new List<ConversationEventDto>();
            foreach (var seed in seeds)
            {
                var startedAt = seed.DataPrimeiraMensagem ?? seed.DataCriacao;
                baseEvents.Add(new ConversationEventDto
                {
                    Id = DeterministicGuid($"started:{seed.Id}:{startedAt:O}"),
                    Type = "started",
                    At = startedAt,
                    ActorName = seed.AgenteNome,
                    ToStatus = CanonicalStatus(seed.StatusAtendimento, seed.Estado, seed.AgenteNome != null ? ModoConversa.Humano : ModoConversa.Bot),
                    Source = "derived"
                });
            }

            if (!persisted.Any(e => string.Equals(e.Type, "closed", StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var seed in seeds.Where(s => s.DataFechamento.HasValue))
                {
                    var status = CanonicalStatus(seed.StatusAtendimento, seed.Estado, seed.AgenteNome != null ? ModoConversa.Humano : ModoConversa.Bot);
                    baseEvents.Add(new ConversationEventDto
                    {
                        Id = DeterministicGuid($"closed:{seed.Id}:{seed.DataFechamento:O}"),
                        Type = "closed",
                        At = seed.DataFechamento!.Value,
                        ActorName = seed.AgenteNome,
                        ToStatus = status,
                        Reason = seed.MotivoFechamento,
                        CloseType = string.Equals(status, "encerrada_inatividade", StringComparison.OrdinalIgnoreCase) ? "inatividade" : "manual",
                        Source = "derived"
                    });
                }
            }

            return baseEvents.Concat(persisted).OrderBy(e => e.At).ThenBy(e => e.Type).ToList();
        }

        private async Task<IReadOnlyList<ConversationEventDto>> ListarEventosClienteAsync(NpgsqlConnection cx, Guid idCliente, Guid idEstabelecimento)
        {
            var persistedRows = (await cx.QueryAsync<ConversationEventRow>(@"SELECT e.id AS Id, e.tipo AS Type, e.data_criacao AS At, e.ator_nome AS ActorName, e.status_anterior AS FromStatus, e.status_novo AS ToStatus, e.motivo AS Reason, e.tipo_fechamento AS CloseType, e.origem AS Source, e.ator_usuario_id AS ActorUserId, e.ator_agente_id AS ActorAgentId, e.dados::text AS DataJson FROM conversa_eventos e JOIN conversas c ON c.id = e.id_conversa WHERE c.id_cliente = @IdCliente AND c.id_estabelecimento = @IdEstabelecimento ORDER BY e.data_criacao ASC;", new { IdCliente = idCliente, IdEstabelecimento = idEstabelecimento })).ToList();
            var persisted = persistedRows.Select(ToEventDto).ToList();
            var seeds = (await cx.QueryAsync<BaseEventSeed>(@"SELECT c.id AS Id, COALESCE(c.id_conversa_grupo, c.id) AS IdConversaGrupo, c.data_primeira_mensagem AS DataPrimeiraMensagem, c.data_criacao AS DataCriacao, c.data_fechamento AS DataFechamento, c.motivo_fechamento AS MotivoFechamento, c.estado::text AS Estado, COALESCE(NULLIF(c.status_atendimento, ''), '') AS StatusAtendimento, CASE WHEN c.id_agente_atribuido IS NULL THEN NULL ELSE COALESCE(u.nome, CONCAT('Agente ', c.id_agente_atribuido::text)) END AS AgenteNome FROM conversas c LEFT JOIN agentes a ON a.id = c.id_agente_atribuido LEFT JOIN usuario u ON u.id = a.usuarioid WHERE c.id_cliente = @IdCliente AND c.id_estabelecimento = @IdEstabelecimento ORDER BY c.data_criacao ASC;", new { IdCliente = idCliente, IdEstabelecimento = idEstabelecimento })).ToList();

            var baseEvents = new List<ConversationEventDto>();
            foreach (var seed in seeds)
            {
                var startedAt = seed.DataPrimeiraMensagem ?? seed.DataCriacao;
                baseEvents.Add(new ConversationEventDto
                {
                    Id = DeterministicGuid($"started:{seed.Id}:{startedAt:O}"),
                    Type = "started",
                    At = startedAt,
                    ActorName = seed.AgenteNome,
                    ToStatus = CanonicalStatus(seed.StatusAtendimento, seed.Estado, seed.AgenteNome != null ? ModoConversa.Humano : ModoConversa.Bot),
                    Source = "derived"
                });
            }

            if (!persisted.Any(e => string.Equals(e.Type, "closed", StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var seed in seeds.Where(s => s.DataFechamento.HasValue))
                {
                    var status = CanonicalStatus(seed.StatusAtendimento, seed.Estado, seed.AgenteNome != null ? ModoConversa.Humano : ModoConversa.Bot);
                    baseEvents.Add(new ConversationEventDto
                    {
                        Id = DeterministicGuid($"closed:{seed.Id}:{seed.DataFechamento:O}"),
                        Type = "closed",
                        At = seed.DataFechamento!.Value,
                        ActorName = seed.AgenteNome,
                        ToStatus = status,
                        Reason = seed.MotivoFechamento,
                        CloseType = string.Equals(status, "encerrada_inatividade", StringComparison.OrdinalIgnoreCase) ? "inatividade" : "manual",
                        Source = "derived"
                    });
                }
            }

            return baseEvents.Concat(persisted).OrderBy(e => e.At).ThenBy(e => e.Type).ToList();
        }

        private async Task<Guid> GarantirClienteTxAsync(NpgsqlConnection cx, NpgsqlTransaction? tx, string telefoneE164, Guid idEstabelecimento)
        {
            var existente = await cx.ExecuteScalarAsync<Guid?>(@"SELECT id FROM clientes WHERE id_estabelecimento = @IdEstabelecimento AND telefone_e164 = @Telefone LIMIT 1;", new { IdEstabelecimento = idEstabelecimento, Telefone = telefoneE164 }, tx);
            if (existente.HasValue && existente.Value != Guid.Empty)
            {
                return existente.Value;
            }

            var novoId = Guid.NewGuid();
            var agora = DateTime.UtcNow;
            await cx.ExecuteAsync(@"INSERT INTO clientes (id, id_estabelecimento, telefone_e164, data_criacao, data_atualizacao) SELECT @Id, @IdEstabelecimento, @Telefone, @CriadoEm, @AtualizadoEm WHERE NOT EXISTS (SELECT 1 FROM clientes WHERE id_estabelecimento = @IdEstabelecimento AND telefone_e164 = @Telefone);", new { Id = novoId, IdEstabelecimento = idEstabelecimento, Telefone = telefoneE164, CriadoEm = agora, AtualizadoEm = agora }, tx);
            return (await cx.ExecuteScalarAsync<Guid?>(@"SELECT id FROM clientes WHERE id_estabelecimento = @IdEstabelecimento AND telefone_e164 = @Telefone LIMIT 1;", new { IdEstabelecimento = idEstabelecimento, Telefone = telefoneE164 }, tx)) ?? novoId;
        }

        private Conversation ToConversation(ConversationRow row) => new()
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
            ContextoEstadoJson = row.ContextoEstadoJson,
            StatusAtendimento = CanonicalStatus(row.StatusAtendimento, row.Estado, row.IdAgenteAtribuido.HasValue ? ModoConversa.Humano : ModoConversa.Bot),
            DataUltimaLeitura = row.DataUltimaLeitura
        };

        private bool IsCentral(Guid idEstabelecimento) => _centralEstabelecimentoId.HasValue && _centralEstabelecimentoId.Value == idEstabelecimento;
        private static string EstadoDb(EstadoConversa estado) => estado switch { EstadoConversa.Aberto => "aberto", EstadoConversa.EmAtendimento => "em_atendimento", EstadoConversa.FechadoAutomaticamente => "fechado_automaticamente", EstadoConversa.FechadoAgente => "fechado_agente", EstadoConversa.Arquivada => "arquivada", _ => "aberto" };
        private static EstadoConversa EstadoModel(string? estado) => NormalizeLegacyState(estado) switch { "aberto" => EstadoConversa.Aberto, "em_atendimento" => EstadoConversa.EmAtendimento, "fechado_agente" => EstadoConversa.FechadoAgente, "fechado_automaticamente" => EstadoConversa.FechadoAutomaticamente, "arquivada" => EstadoConversa.Arquivada, _ => EstadoConversa.Aberto };
        private static string LegacyStatusToCanonical(string? legacyState) => NormalizeLegacyState(legacyState) switch { "em_atendimento" => "em_andamento", "fechado_agente" => "encerrada_manual", "fechado_automaticamente" => "encerrada_inatividade", "arquivada" => "encerrada_manual", _ => "com_bot" };
        private static string CanonicalStatus(string? statusAtendimento, string? legacyState, ModoConversa modo) { var normalized = NormalizeStatus(statusAtendimento); if (!string.IsNullOrWhiteSpace(normalized)) return normalized; if (modo == ModoConversa.Humano && string.Equals(NormalizeLegacyState(legacyState), "aberto", StringComparison.OrdinalIgnoreCase)) return "em_andamento"; return LegacyStatusToCanonical(legacyState); }
        private static string LegacyStateFromCanonicalStatus(string? status) => NormalizeStatus(status) switch { "em_andamento" => "em_atendimento", "aguardando_cliente" => "em_atendimento", "aguardando_interno" => "em_atendimento", "encerrada_manual" => "fechado_agente", "encerrada_inatividade" => "fechado_automaticamente", _ => "aberto" };
        private static string NormalizeLegacyState(string? estado) => (estado ?? string.Empty).Trim().ToLowerInvariant() switch { "aguardando_atendimento" => "em_atendimento", "fechado_bot" => "fechado_automaticamente", "arquivado" => "arquivada", _ => (estado ?? string.Empty).Trim().ToLowerInvariant() };
        private static string NormalizeStatus(string? status) => (status ?? string.Empty).Trim().ToLowerInvariant() switch { "aberto" => "com_bot", "novo_bot" => "com_bot", "pendente" => "com_bot", "em_atendimento" => "em_andamento", "em_atendimento_humano" => "em_andamento", "fechado_bot" => "encerrada_inatividade", "fechado_agente" => "encerrada_manual", "arquivada" => "encerrada_manual", _ => (status ?? string.Empty).Trim().ToLowerInvariant() };
        private static string NormalizeCloseType(string? closeType) => string.IsNullOrWhiteSpace(closeType) ? "manual" : closeType.Trim().ToLowerInvariant();
        private static bool IsHumanStatus(string status) => string.Equals(status, "em_andamento", StringComparison.OrdinalIgnoreCase) || string.Equals(status, "aguardando_cliente", StringComparison.OrdinalIgnoreCase) || string.Equals(status, "aguardando_interno", StringComparison.OrdinalIgnoreCase);
        private static bool IsClosedStatus(string status) => string.Equals(status, "encerrada_manual", StringComparison.OrdinalIgnoreCase) || string.Equals(status, "encerrada_inatividade", StringComparison.OrdinalIgnoreCase);
        private static bool IsWindowExpired(DateTime? expiresAt) => expiresAt.HasValue && expiresAt.Value <= DateTime.UtcNow;
        private static string? ResolveSendBlockReasonCode(ConversationRow row, string status)
            => string.Equals(status, "encerrada_inatividade", StringComparison.OrdinalIgnoreCase)
                && string.Equals(row.MotivoFechamento, WindowExpiredReason, StringComparison.OrdinalIgnoreCase)
                ? WindowExpiredBlockCode
                : null;
        private static ConversationContext? Deserialize(string? json) { try { return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<ConversationContext>(json); } catch { return null; } }
        private static ConversationEventDto ToEventDto(ConversationEventRow row) => new() { Id = row.Id, Type = row.Type ?? string.Empty, At = row.At, ActorName = row.ActorName, FromStatus = string.IsNullOrWhiteSpace(row.FromStatus) ? null : NormalizeStatus(row.FromStatus), ToStatus = string.IsNullOrWhiteSpace(row.ToStatus) ? null : NormalizeStatus(row.ToStatus), Reason = row.Reason, CloseType = row.CloseType, Source = row.Source, ActorUserId = row.ActorUserId, ActorAgentId = row.ActorAgentId, Data = DeserializeEventData(row.DataJson) };
        private static Dictionary<string, object?>? DeserializeEventData(string? json) { try { return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<Dictionary<string, object?>>(json); } catch { return null; } }
        private static Guid DeterministicGuid(string input) { using var md5 = MD5.Create(); return new Guid(md5.ComputeHash(Encoding.UTF8.GetBytes(input))); }

        private sealed class ConversationRow
        {
            public Guid Id { get; set; }
            public Guid IdConversaGrupo { get; set; }
            public Guid IdEstabelecimento { get; set; }
            public Guid IdCliente { get; set; }
            public string? Estado { get; set; }
            public string? StatusAtendimento { get; set; }
            public int? IdAgenteAtribuido { get; set; }
            public int QtdNaoLidas { get; set; }
            public DateTime? DataUltimaLeitura { get; set; }
            public DateTime? DataPrimeiraMensagem { get; set; }
            public DateTime? DataUltimaMensagem { get; set; }
            public DateTime? DataUltimaEntrada { get; set; }
            public DateTime? DataUltimaSaida { get; set; }
            public DateTime? Janela24hFim { get; set; }
            public string? MotivoFechamento { get; set; }
            public int? FechadoPorId { get; set; }
            public DateTime? DataFechamento { get; set; }
            public DateTime DataCriacao { get; set; }
            public DateTime DataAtualizacao { get; set; }
            public string? ContextoEstadoJson { get; set; }
            public string? TelefoneCliente { get; set; }
            public string? ClienteNome { get; set; }
            public string? AgenteNome { get; set; }
        }

        private sealed class ExpiredConversationCandidateRow
        {
            public Guid Id { get; set; }
            public Guid IdConversaGrupo { get; set; }
            public Guid IdEstabelecimento { get; set; }
            public string? Estado { get; set; }
            public string? StatusAtendimento { get; set; }
            public int? IdAgenteAtribuido { get; set; }
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
            public int UnreadCount { get; set; }
        }

        private sealed class ConversationEventRow
        {
            public Guid Id { get; set; }
            public string? Type { get; set; }
            public DateTime At { get; set; }
            public string? ActorName { get; set; }
            public string? FromStatus { get; set; }
            public string? ToStatus { get; set; }
            public string? Reason { get; set; }
            public string? CloseType { get; set; }
            public string? Source { get; set; }
            public int? ActorUserId { get; set; }
            public int? ActorAgentId { get; set; }
            public string? DataJson { get; set; }
        }

        private sealed class BaseEventSeed
        {
            public Guid Id { get; set; }
            public Guid IdConversaGrupo { get; set; }
            public DateTime? DataPrimeiraMensagem { get; set; }
            public DateTime DataCriacao { get; set; }
            public DateTime? DataFechamento { get; set; }
            public string? MotivoFechamento { get; set; }
            public string? Estado { get; set; }
            public string? StatusAtendimento { get; set; }
            public string? AgenteNome { get; set; }
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
