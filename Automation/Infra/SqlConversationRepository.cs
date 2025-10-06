// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using APIBack.Automation.Dtos;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Models;
using Dapper;
using APIBack.Automation.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using APIBack.Automation.Services;

namespace APIBack.Automation.Infra
{
    public class SqlConversationRepository : IConversationRepository
    {
        private readonly string _connectionString;
        private readonly Microsoft.Extensions.Configuration.IConfiguration _configuration;
        private readonly ILogger<SqlConversationRepository> _logger;
        private readonly IWabaPhoneRepository? _wabaPhoneRepository;
        private static bool _indexesEnsured;
        private const string ConversationDetailsSql = @"SELECT
  c.id                    AS Id,
  c.id_cliente            AS IdCliente,
  COALESCE(cl.nome, cl.telefone_e164) AS ClienteNome,
  cl.telefone_e164        AS ClienteNumero,
  c.estado::text          AS Estado,
  c.id_agente_atribuido   AS IdAgenteAtribuido,
  c.data_primeira_mensagem AS DataPrimeiraMensagem,
  c.data_ultima_mensagem  AS DataUltimaMensagem,
  c.data_criacao          AS DataCriacao,
  c.data_atualizacao      AS DataAtualizacao,
  c.data_fechamento       AS DataFechamento,
  c.fechado_por_id        AS FechadoPorId,
  c.motivo_fechamento     AS MotivoFechamento
FROM conversas c
LEFT JOIN clientes cl ON c.id_cliente = cl.id
WHERE c.id = @Id
LIMIT 1;";

        public SqlConversationRepository(IConfiguration config, ILogger<SqlConversationRepository> logger)
        {
            _connectionString = config.GetConnectionString("DefaultConnection")
                                 ?? config["ConnectionStrings:DefaultConnection"]
                                 ?? throw new InvalidOperationException("Connection string 'DefaultConnection' nuo encontrada.");
            _configuration = config;
            _logger = logger;

            // Garante �ndice �nico de idempot�ncia (executa uma vez por processo)
            if (!_indexesEnsured)
            {
                try
                {
                    using var cx = new NpgsqlConnection(_connectionString);
                    cx.Execute("CREATE UNIQUE INDEX IF NOT EXISTS ux_mensagens_id_provedor ON mensagens (id_provedor);");
                    _indexesEnsured = true;
                }
                catch
                {
                    // N�o interrompe a aplica��o em caso de erro ao criar �ndice.
                }
            }
        }

        // Helpers de conversão para DateTime (UTC-safe)
        private static DateTime? ToUtc(DateTime? dt)
            => dt.HasValue ? DateTime.SpecifyKind(dt.Value, DateTimeKind.Utc) : (DateTime?)null;

        private static DateTime ToUtcNonNull(DateTime dt)
            => DateTime.SpecifyKind(dt, DateTimeKind.Utc);
        private static string MapEstadoToDatabase(EstadoConversa estado) => estado switch
        {
            EstadoConversa.Aberto => "aberto",
            EstadoConversa.FechadoAutomaticamente => "fechado_automaticamente",
            EstadoConversa.FechadoAgente => "fechado_agente",
            EstadoConversa.Arquivada => "arquivada",
            EstadoConversa.EmAtendimento => "em_atendimento",
            _ => "aberto"
        };

        private static EstadoConversa MapEstadoFromDatabase(string? valor)
        {
            if (string.IsNullOrWhiteSpace(valor))
            {
                return EstadoConversa.Aberto;
            }

            var normalized = valor.Trim().ToLowerInvariant();

            return normalized switch
            {
                "aberto" => EstadoConversa.Aberto,
                "aguardando_atendimento" or "aguardandoatendimento" => EstadoConversa.EmAtendimento,
                "em_atendimento" or "ematendimento" => EstadoConversa.EmAtendimento,
                "fechado_automaticamente" or "fechadoautomaticamente" => EstadoConversa.FechadoAutomaticamente,
                "fechado_bot" or "fechadobot" => EstadoConversa.FechadoAutomaticamente,
                "fechado_agente" or "fechadoagente" => EstadoConversa.FechadoAgente,
                "arquivado" or "arquivada" => EstadoConversa.Arquivada,
                _ => Enum.TryParse<EstadoConversa>(valor, true, out var parsed) ? parsed : EstadoConversa.Aberto
            };
        }

        private static string MapEstadoDbToApi(string? valor)
        {
            if (string.IsNullOrWhiteSpace(valor))
            {
                return "aberto";
            }

            return valor.Trim().ToLowerInvariant() switch
            {
                "fechado_automaticamente" => "fechado_bot",
                _ => valor.Trim().ToLowerInvariant()
            };
        }

        private static string NormalizeEstadoFiltro(string estado)
        {
            var normalized = estado.Trim().ToLowerInvariant();
            return normalized switch
            {
                "fechado_bot" => "fechado_automaticamente",
                "arquivado" => "arquivada",
                _ => normalized
            };
        }

        public async Task<Conversation?> ObterPorIdAsync(Guid id)
        {
            const string sql = @"
SELECT
  c.id                         AS Id,
  c.id_estabelecimento         AS IdEstabelecimento,
  c.id_cliente                 AS IdCliente,
  c.canal                      AS Canal,
  c.estado::text               AS Estado,
  c.id_agente_atribuido        AS IdAgenteAtribuido,
  c.data_primeira_mensagem     AS DataPrimeiraMensagem,
  c.data_ultima_mensagem       AS DataUltimaMensagem,
  c.data_ultima_entrada        AS DataUltimaEntrada,
  c.data_ultima_saida          AS DataUltimaSaida,
  c.janela_24h_inicio          AS Janela24hInicio,
  c.janela_24h_fim             AS Janela24hFim,
  c.qtd_nao_lidas              AS QtdNaoLidas,
  c.motivo_fechamento          AS MotivoFechamento,
  c.fechado_por_id             AS FechadoPorId,
  c.data_fechamento            AS DataFechamento,
  c.data_criacao               AS DataCriacao,
  c.data_atualizacao           AS DataAtualizacao,
  cl.telefone_e164             AS TelefoneCliente
FROM conversas c
LEFT JOIN clientes cl ON c.id_cliente = cl.id
WHERE c.id = @Id;";

            await using var cx = new NpgsqlConnection(_connectionString);
            var row = await cx.QueryFirstOrDefaultAsync<(Guid Id, Guid IdEstabelecimento, Guid IdCliente, string? Canal, string? Estado, int? IdAgenteAtribuido, DateTime? DataPrimeiraMensagem, DateTime? DataUltimaMensagem, DateTime? DataUltimaEntrada, DateTime? DataUltimaSaida, DateTime? Janela24hInicio, DateTime? Janela24hFim, int QtdNaoLidas, string? MotivoFechamento, int? FechadoPorId, DateTime? DataFechamento, DateTime DataCriacao, DateTime DataAtualizacao, string? TelefoneCliente)>(sql, new { Id = id });
            if (row.Equals(default((Guid, Guid, Guid, string?, string?, int?, DateTime?, DateTime?, DateTime?, DateTime?, DateTime?, DateTime?, int, string?, int?, DateTime?, DateTime, DateTime, string?)))) return null;

            var conv = new Conversation
            {
                IdConversa = row.Id,
                IdEstabelecimento = row.IdEstabelecimento,
                IdCliente = row.IdCliente,
                TelefoneCliente = row.TelefoneCliente,
                IdWa = string.Empty, // não existe no schema
                Modo = row.IdAgenteAtribuido == null ? ModoConversa.Bot : ModoConversa.Humano,
                AgenteDesignadoId = row.IdAgenteAtribuido,
                UltimoUsuarioEm = ToUtc(row.DataUltimaEntrada) ?? default,
                Janela24hExpiraEm = ToUtc(row.Janela24hFim),
                CriadoEm = ToUtcNonNull(row.DataCriacao),
                AtualizadoEm = ToUtcNonNull(row.DataAtualizacao),
                Estado = MapEstadoFromDatabase(row.Estado),
                MotivoFechamento = row.MotivoFechamento,
                FechadoPorId = row.FechadoPorId,
                DataFechamento = ToUtc(row.DataFechamento)
            };
            return conv;
        }

        public async Task<bool> InserirOuAtualizarAsync(Conversation conversa)
        {
            // Validação obrigatória do id_estabelecimento
            if (conversa.IdEstabelecimento == Guid.Empty)
            {
                throw new ArgumentException("id_estabelecimento é obrigatório para criar/atualizar conversa", nameof(conversa));
            }

            var criado = conversa.CriadoEm != default ? DateTime.SpecifyKind(conversa.CriadoEm, DateTimeKind.Utc) : DateTime.UtcNow;
            var atualizado = conversa.AtualizadoEm.HasValue && conversa.AtualizadoEm.Value != default
                                ? DateTime.SpecifyKind(conversa.AtualizadoEm.Value, DateTimeKind.Utc)
                                : criado;

            // Datas derivadas (nunca nulas), todas em UTC
            var dataPrimeiraMensagem = criado;
            var dataUltimaMensagem = criado; // na criação, igual à primeira
            var dataUltimaEntrada = criado; // inicializa com criado
            var dataUltimaSaida = dataUltimaMensagem; // inicialmente igual à última mensagem
            var janela24hInicio = dataPrimeiraMensagem; // na criação, igual à primeira
            var janela24hFim = janela24hInicio.AddHours(24); // sempre 24h após o início

            const string sqlConversas = @"
INSERT INTO conversas
  (id, id_estabelecimento, id_cliente, canal, estado, id_agente_atribuido, data_primeira_mensagem, data_ultima_mensagem, data_ultima_entrada, data_ultima_saida, janela_24h_inicio, janela_24h_fim, qtd_nao_lidas, motivo_fechamento, fechado_por_id, data_fechamento, data_criacao, data_atualizacao)
VALUES
  (@Id, @IdEstabelecimento, @IdCliente, @Canal::canal_chat_enum, @Estado::estado_conversa_enum, @IdAgenteAtribuido, @DataPrimeiraMensagem, @DataUltimaMensagem, @DataUltimaEntrada, @DataUltimaSaida, @Janela24hInicio, @Janela24hFim, @QtdNaoLidas, @MotivoFechamento, @FechadoPorId, @DataFechamento, @DataCriacao, @DataAtualizacao)
ON CONFLICT (id) DO UPDATE SET
  data_ultima_mensagem = GREATEST(conversas.data_ultima_mensagem, EXCLUDED.data_ultima_mensagem),
  data_atualizacao     = EXCLUDED.data_atualizacao,
  id_agente_atribuido  = COALESCE(EXCLUDED.id_agente_atribuido, conversas.id_agente_atribuido);";

            try
            {
                await using var cx = new NpgsqlConnection(_connectionString);
                var rowsAffected = await cx.ExecuteAsync(sqlConversas, new
                {
                    Id = conversa.IdConversa,
                    IdEstabelecimento = conversa.IdEstabelecimento,
                    IdCliente = conversa.IdCliente,
                    Canal = conversa.Canal,
                    Estado = MapEstadoToDatabase(conversa.Estado),
                    IdAgenteAtribuido = conversa.Modo == ModoConversa.Humano
                        ? conversa.AgenteDesignadoId
                        : null,
                    DataPrimeiraMensagem = dataPrimeiraMensagem,
                    DataUltimaMensagem = dataUltimaMensagem,
                    DataUltimaEntrada = dataUltimaEntrada,
                    DataUltimaSaida = dataUltimaSaida,
                    Janela24hInicio = janela24hInicio,
                    Janela24hFim = janela24hFim,
                    QtdNaoLidas = 0,
                    MotivoFechamento = conversa.MotivoFechamento,
                    FechadoPorId = conversa.FechadoPorId,
                    DataFechamento = conversa.DataFechamento.HasValue ? DateTime.SpecifyKind(conversa.DataFechamento.Value, DateTimeKind.Utc) : (DateTime?)null,
                    DataCriacao = criado,
                    DataAtualizacao = atualizado
                });
                return rowsAffected > 0;
            }
            catch (Exception ex)
            {
                // Log error but don't break webhook flow
                Console.WriteLine($"Erro ao inserir/atualizar conversa {conversa.IdConversa}: {ex.Message}");
                return false;
            }
        }

        public async Task<Guid> GarantirClienteAsync(string telefoneE164, Guid idEstabelecimento)
        {
            if (string.IsNullOrWhiteSpace(telefoneE164))
                throw new ArgumentException("telefoneE164 obrigatório", nameof(telefoneE164));
            if (idEstabelecimento == Guid.Empty)
                throw new ArgumentException("idEstabelecimento obrigatório", nameof(idEstabelecimento));

            const string sqlSel = @"SELECT id FROM clientes
                                     WHERE id_estabelecimento = @IdEstabelecimento
                                       AND telefone_e164      = @Telefone
                                     LIMIT 1;";

            await using var cx = new NpgsqlConnection(_connectionString);
            var existente = await cx.ExecuteScalarAsync<Guid?>(sqlSel, new { IdEstabelecimento = idEstabelecimento, Telefone = telefoneE164 });
            if (existente.HasValue && existente.Value != Guid.Empty)
                return existente.Value;

            var novoId = Guid.NewGuid();
            var agora = DateTime.UtcNow;

            const string sqlIns = @"INSERT INTO clientes (id, id_estabelecimento, telefone_e164, data_criacao, data_atualizacao)
                                     SELECT @Id, @IdEstabelecimento, @Telefone, @CriadoEm, @AtualizadoEm
                                     WHERE NOT EXISTS (
                                         SELECT 1 FROM clientes WHERE id_estabelecimento = @IdEstabelecimento AND telefone_e164 = @Telefone
                                     );";

            await cx.ExecuteAsync(sqlIns, new
            {
                Id = novoId,
                IdEstabelecimento = idEstabelecimento,
                Telefone = telefoneE164,
                CriadoEm = agora,
                AtualizadoEm = agora
            });

            var idFinal = await cx.ExecuteScalarAsync<Guid?>(sqlSel, new { IdEstabelecimento = idEstabelecimento, Telefone = telefoneE164 });
            return idFinal ?? novoId;
        }


        public async Task DefinirModoAsync(Guid id, ModoConversa modo, int? agenteId)
        {
            const string sqlHumano = @"
                      UPDATE conversas
                         SET id_agente_atribuido = @AgenteId,
                             estado               = @Estado::estado_conversa_enum,
                             data_atualizacao     = NOW()
                       WHERE id = @Id;";

            const string sqlBot = @"
                      UPDATE conversas
                         SET id_agente_atribuido = NULL,
                             estado               = @Estado::estado_conversa_enum,
                             data_atualizacao     = NOW()
                       WHERE id = @Id;";

            object parametros = modo == ModoConversa.Humano
                ? new { Id = id, AgenteId = (object?)agenteId, Estado = MapEstadoToDatabase(EstadoConversa.EmAtendimento) }
                : new { Id = id, Estado = MapEstadoToDatabase(EstadoConversa.Aberto) };

            var sql = modo == ModoConversa.Humano ? sqlHumano : sqlBot;

            await using var cx = new NpgsqlConnection(_connectionString);
            await cx.ExecuteAsync(sql, parametros);
        }

        public async Task AcrescentarMensagemAsync(Message mensagem, string? phoneNumberId, string? idWa = null)
        {
            // Normaliza timestamp para UTC corretamente
            DateTime quandoUtc;
            if (mensagem.DataHora == default)
                quandoUtc = DateTime.UtcNow;
            else if (mensagem.DataHora.Kind == DateTimeKind.Utc)
                quandoUtc = mensagem.DataHora;
            else if (mensagem.DataHora.Kind == DateTimeKind.Local)
                quandoUtc = mensagem.DataHora.ToUniversalTime();
            else
                // Unspecified: trate como UTC para não aplicar offset errado
                quandoUtc = DateTime.SpecifyKind(mensagem.DataHora, DateTimeKind.Utc);

            var idMsg = mensagem.Id != Guid.Empty ? mensagem.Id : Guid.NewGuid();
            var direcao = mensagem.Direcao == DirecaoMensagem.Entrada ? "entrada" : "saida";
            var tipoOrigemWa = string.IsNullOrWhiteSpace(mensagem.TipoOriginal) ? mensagem.Tipo : mensagem.TipoOriginal;
            var criadaPor = string.IsNullOrWhiteSpace(mensagem.CriadaPor)
                            ? (mensagem.Direcao == DirecaoMensagem.Entrada ? "bot" : "agente:1")
                            : mensagem.CriadaPor!;
            var tipoMapeado = MessageTypeMapper.MapType(tipoOrigemWa, mensagem.Direcao, criadaPor);
            var tipo = string.IsNullOrWhiteSpace(tipoMapeado) ? "texto" : tipoMapeado;
            mensagem.Tipo = tipo;
            mensagem.TipoOriginal ??= tipoOrigemWa;
            mensagem.CriadaPor = criadaPor;
            var tipoOriginalLog = string.IsNullOrWhiteSpace(tipoOrigemWa) ? "(indefinido)" : tipoOrigemWa.Trim();
            _logger.LogInformation("[Conversa={Conversa}] [Mensagem={Mensagem}] Tipo WA={TipoWa} -> Tipo Banco={TipoBanco}", mensagem.IdConversa, idMsg, tipoOriginalLog, tipo);
            var status = string.IsNullOrWhiteSpace(mensagem.Status)
                          ? (mensagem.Direcao == DirecaoMensagem.Entrada ? "entregue" : "fila")
                          : mensagem.Status!;
            var idProv = !string.IsNullOrWhiteSpace(mensagem.IdProvedor) ? mensagem.IdProvedor : mensagem.IdMensagemWa;

            // ===== timestamps por regra =====
            DateTime? dataEnvio = null, dataEntrega = null, dataLeitura = null;
            var st = status.Trim().ToLowerInvariant();

            if (direcao == "entrada")
            {
                // Entrada "chega" já enviada/entregue ao bot
                dataEnvio = quandoUtc;
                dataEntrega = quandoUtc;
                dataLeitura = null;
            }
            else
            {
                switch (st)
                {
                    case "fila":
                    case "enviado":
                    case "enviada":
                        dataEnvio = quandoUtc;
                        break;
                    case "entregue":
                    case "entregada":
                        dataEnvio = quandoUtc;
                        dataEntrega = quandoUtc;
                        break;
                    case "lido":
                    case "lida":
                        dataEnvio = quandoUtc;
                        dataEntrega = quandoUtc;
                        dataLeitura = quandoUtc;
                        break;
                    default:
                        // mantém só data_criacao
                        break;
                }
            }

            const string insertMsg = @"
INSERT INTO mensagens
  (id, id_conversa, direcao, tipo, status, id_provedor, codigo_erro, mensagem_erro, tentativas, criada_por,
   data_envio, data_entrega, data_leitura, data_criacao, conteudo)
VALUES
  (@Id, @IdConversa,
   @Direcao::direcao_mensagem_enum,
   @Tipo::tipo_mensagem_enum,          
   @Status::status_mensagem_enum,         
   @IdProvedor, @CodigoErro, @MensagemErro, @Tentativas, @CriadaPor,
   @DataEnvio, @DataEntrega, @DataLeitura, @DataCriacao, @Conteudo)
ON CONFLICT DO NOTHING;";


            // Janela 24h: estende de forma rolante a cada nova ENTRADA
            const string updConv = @"
UPDATE conversas
   SET data_primeira_mensagem = COALESCE(data_primeira_mensagem, @Quando),
       data_ultima_mensagem   = GREATEST(COALESCE(data_ultima_mensagem, @Quando), @Quando),
       data_ultima_entrada    = CASE WHEN @Direcao = 'entrada' THEN @Quando ELSE data_ultima_entrada END,
       data_ultima_saida      = CASE WHEN @Direcao = 'saida'   THEN @Quando ELSE data_ultima_saida   END,
       janela_24h_inicio      = CASE
                                  WHEN @Direcao = 'entrada' AND (janela_24h_inicio IS NULL) THEN @Quando
                                  ELSE janela_24h_inicio
                                END,
       janela_24h_fim         = CASE
                                  WHEN @Direcao = 'entrada' THEN GREATEST(COALESCE(janela_24h_fim, @Quando + interval '24 hour'),
                                                                           @Quando + interval '24 hour')
                                  ELSE janela_24h_fim
                                END,
       qtd_nao_lidas          = CASE WHEN @Direcao = 'entrada' THEN COALESCE(qtd_nao_lidas,0) + 1 ELSE qtd_nao_lidas END,
       data_atualizacao       = NOW()
 WHERE id = @IdConversa;";

            await using var cx = new NpgsqlConnection(_connectionString);
            await cx.OpenAsync();
            await using var tx = await cx.BeginTransactionAsync();

            try
            {
                // Garante que a conversa existe antes de inserir a mensagem (mesma transação) SEM usar Guid.Empty
                if (mensagem.IdConversa == Guid.Empty)
                    throw new ArgumentException("IdConversa obrigatório", nameof(mensagem));

                // Extrai WABA phone e telefone do cliente de MetadadosMidia (JSON), caso disponíveis
                // Verifica/Cria conversa apenas se ainda não existir
                const string checkConv = "SELECT 1 FROM conversas WHERE id = @Id LIMIT 1;";
                var convExists = await cx.ExecuteScalarAsync<int?>(checkConv, new { Id = mensagem.IdConversa }, transaction: tx);
                if (!convExists.HasValue)
                {
                    // Conversa não existe -> garantir criação
                    string? wabaPhoneNumberId = phoneNumberId;
                    string? telefoneClienteRaw = idWa;

                    // Resolve IdEstabelecimento via waba_phone
                    const string sqlWaba = "SELECT id_estabelecimento FROM waba_phone WHERE display_phone_number  = @PhoneNumberId LIMIT 1;";
                    var idEstabelecimento = await cx.ExecuteScalarAsync<Guid?>(sqlWaba, new { PhoneNumberId = wabaPhoneNumberId }, transaction: tx);
                    if (!idEstabelecimento.HasValue || idEstabelecimento.Value == Guid.Empty)
                        throw new InvalidOperationException("Estabelecimento não encontrado para este WABA");

                    // Resolve IdCliente por telefone normalizado (cria se não existir)
                    var telefoneE164 = APIBack.Automation.Helpers.TelefoneHelper.ToE164(telefoneClienteRaw ?? string.Empty);
                    const string sqlCliSel = "SELECT id FROM clientes WHERE telefone_e164 = @Tel AND id_estabelecimento = @IdEstabelecimento LIMIT 1;";
                    var idCliente = await cx.ExecuteScalarAsync<Guid?>(sqlCliSel, new { Tel = telefoneE164, IdEstabelecimento = idEstabelecimento }, transaction: tx);

                    if (!idCliente.HasValue || idCliente.Value == Guid.Empty)
                    {
                        var novoClienteId = Guid.NewGuid();
                        var agora = DateTime.UtcNow;
                        const string sqlCliIns = @"
        INSERT INTO clientes (id, id_estabelecimento, telefone_e164, data_criacao, data_atualizacao)
        SELECT @Id, @IdEstabelecimento, @Telefone, @CriadoEm, @AtualizadoEm
        WHERE NOT EXISTS (
          SELECT 1 FROM clientes
          WHERE id_estabelecimento = @IdEstabelecimento
            AND telefone_e164   = @Telefone
        )
        RETURNING id;";

                        var insertedId = await cx.ExecuteScalarAsync<Guid?>(
                            sqlCliIns,
                            new
                            {
                                Id = novoClienteId,
                                IdEstabelecimento = idEstabelecimento,
                                Telefone = telefoneE164,
                                CriadoEm = agora,
                                AtualizadoEm = agora
                            },
                            transaction: tx
                        );

                        if (insertedId.HasValue) idCliente = insertedId;
                        else
                        {
                            idCliente = await cx.ExecuteScalarAsync<Guid?>(sqlCliSel, new { Tel = telefoneE164, IdEstabelecimento = idEstabelecimento }, transaction: tx);
                        }

                        if (!idCliente.HasValue || idCliente.Value == Guid.Empty)
                            throw new InvalidOperationException("Falha ao criar cliente");
                    }

                    // Cria conversa com IDs válidos
                    var agoraUtc = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Utc);
                    await InserirOuAtualizarAsync(new Conversation
                    {
                        IdConversa = mensagem.IdConversa,
                        IdEstabelecimento = idEstabelecimento.Value,
                        IdCliente = idCliente.Value,
                        CriadoEm = agoraUtc,
                        AtualizadoEm = agoraUtc
                    });
                }

                // Insere a mensagem (idempotente via índice único em id_provedor) e atualiza a conversa
                await cx.ExecuteAsync(insertMsg, new
                {
                    Id = idMsg,
                    IdConversa = mensagem.IdConversa,
                    Direcao = direcao,
                    Tipo = tipo,
                    Status = status,
                    IdProvedor = (object?)idProv,
                    Conteudo = mensagem.Conteudo,
                    CodigoErro = (string?)mensagem.CodigoErro,
                    MensagemErro = (string?)mensagem.MensagemErro,
                    Tentativas = mensagem.Tentativas,
                    CriadaPor = criadaPor,
                    DataEnvio = dataEnvio,
                    DataEntrega = dataEntrega,
                    DataLeitura = dataLeitura,
                    DataCriacao = quandoUtc
                }, transaction: tx);

                await cx.ExecuteAsync(updConv, new
                {
                    IdConversa = mensagem.IdConversa,
                    Quando = quandoUtc,
                    Direcao = direcao
                }, transaction: tx);

                await tx.CommitAsync();
            }
            catch
            {
                await tx.RollbackAsync();
                throw;
            }
        }


        public async Task<bool> ExisteIdMensagemPorProvedorWaAsync(string idMensagemWa)
        {
            const string sql = "SELECT 1 FROM mensagens WHERE id_provedor = @IdMensagemWa LIMIT 1;";
            await using var cx = new NpgsqlConnection(_connectionString);
            var existe = await cx.ExecuteScalarAsync<int?>(sql, new { IdMensagemWa = idMensagemWa });
            return existe.HasValue;
        }

        public async Task<Guid> ObterIdConversaPorClienteAsync(Guid idCliente, Guid idEstabelecimento)
        {
            const string sql = @"SELECT id
                                   FROM conversas
                                  WHERE id_cliente = @IdCliente
                                    AND id_estabelecimento = @IdEstabelecimento
                                    AND motivo_fechamento IS NULL -- apenas conversas abertas
                                    AND estado <> 'fechado_automaticamente'::estado_conversa_enum
                                    AND estado <> 'fechado_agente'::estado_conversa_enum
                                    AND estado <> 'arquivada'::estado_conversa_enum
                                  ORDER BY data_criacao DESC
                                  LIMIT 1;";
            await using var cx = new NpgsqlConnection(_connectionString);
            var found = await cx.ExecuteScalarAsync<Guid?>(sql, new { IdCliente = idCliente, IdEstabelecimento = idEstabelecimento });
            return found ?? Guid.Empty;
        }

        public async Task<IReadOnlyList<ConversationListItemDto>> ListarConversasAsync(string? estado, int? idAgente, bool incluirArquivadas)
        {
            var sb = new StringBuilder();
            sb.AppendLine("SELECT");
            sb.AppendLine("  c.id                   AS Id,");
            sb.AppendLine("  c.id_cliente           AS IdCliente,");
            sb.AppendLine("  COALESCE(cl.nome, cl.telefone_e164) AS ClienteNome,");
            sb.AppendLine("  cl.telefone_e164       AS ClienteNumero,");
            sb.AppendLine("  c.estado::text         AS Estado,");
            sb.AppendLine("  c.id_agente_atribuido  AS IdAgenteAtribuido,");
            sb.AppendLine("  c.data_primeira_mensagem AS DataPrimeiraMensagem,");
            sb.AppendLine("  c.data_ultima_mensagem AS DataUltimaMensagem,");
            sb.AppendLine("  c.data_criacao         AS DataCriacao,");
            sb.AppendLine("  c.data_atualizacao     AS DataAtualizacao,");
            sb.AppendLine("  c.data_fechamento      AS DataFechamento,");
            sb.AppendLine("  lm.conteudo            AS UltimaMensagemConteudo,");
            sb.AppendLine("  lm.data_criacao        AS UltimaMensagemData,");
            sb.AppendLine("  lm.criada_por          AS UltimaMensagemCriadaPor");
            sb.AppendLine("FROM conversas c");
            sb.AppendLine("LEFT JOIN clientes cl ON c.id_cliente = cl.id");
            sb.AppendLine("LEFT JOIN LATERAL (");
            sb.AppendLine("    SELECT m.conteudo, m.data_criacao, m.criada_por");
            sb.AppendLine("    FROM mensagens m");
            sb.AppendLine("    WHERE m.id_conversa = c.id");
            sb.AppendLine("    ORDER BY m.data_criacao DESC");
            sb.AppendLine("    LIMIT 1");
            sb.AppendLine(") lm ON TRUE");
            sb.AppendLine("WHERE 1=1");

            var parameters = new DynamicParameters();
            string? estadoNormalizado = null;

            if (!string.IsNullOrWhiteSpace(estado))
            {
                estadoNormalizado = NormalizeEstadoFiltro(estado);
                parameters.Add("Estado", estadoNormalizado);
                sb.AppendLine("  AND c.estado = @Estado::estado_conversa_enum");
            }

            if (idAgente.HasValue)
            {
                parameters.Add("Responsavel", idAgente);
                sb.AppendLine("  AND c.id_agente_atribuido = @Responsavel");
            }

            if (!incluirArquivadas)
            {
                if (!string.Equals(estadoNormalizado, "arquivada", StringComparison.OrdinalIgnoreCase))
                {
                    sb.AppendLine("  AND c.estado <> 'arquivada'::estado_conversa_enum");
                }
            }

            sb.AppendLine("ORDER BY c.data_ultima_mensagem DESC NULLS LAST, c.data_criacao DESC;");

            await using var cx = new NpgsqlConnection(_connectionString);
            var registros = (await cx.QueryAsync<ConversationListItemDto>(sb.ToString(), parameters)).ToList();
            foreach (var item in registros)
            {
                item.Estado = MapEstadoDbToApi(item.Estado);
            }

            return registros;
        }

        public async Task<ConversationHistoryDto?> ObterHistoricoConversaAsync(Guid idConversa, int page, int pageSize)
        {
            if (page < 1) page = 1;
            if (pageSize <= 0) pageSize = 50;

            await using var cx = new NpgsqlConnection(_connectionString);
            var detalhes = await cx.QueryFirstOrDefaultAsync<ConversationDetailsDto>(ConversationDetailsSql, new { Id = idConversa });
            if (detalhes == null)
            {
                return null;
            }

            detalhes.Estado = MapEstadoDbToApi(detalhes.Estado);
            
            // Log para debug
            _logger.LogInformation("ClienteNumero: {ClienteNumero}, ClienteNome: {ClienteNome}", detalhes.ClienteNumero, detalhes.ClienteNome);
            _logger.LogInformation("Query SQL: {Sql}", ConversationDetailsSql);

            var parametrosMensagens = new
            {
                Id = idConversa,
                PageSize = pageSize,
                Offset = (page - 1) * pageSize
            };

            var mensagens = (await cx.QueryAsync<ConversationMessageItemDto>(@"
SELECT
  m.id          AS Id,
  COALESCE(m.criada_por, '') AS CriadaPor,
  COALESCE(m.conteudo, '')   AS Conteudo,
  m.data_envio  AS DataEnvio,
  m.data_criacao AS DataCriacao
FROM mensagens m
WHERE m.id_conversa = @Id
ORDER BY m.data_criacao ASC, m.data_envio ASC
LIMIT @PageSize OFFSET @Offset;", parametrosMensagens)).ToList();

            var total = await cx.ExecuteScalarAsync<int>("SELECT COUNT(1) FROM mensagens WHERE id_conversa = @Id;", new { Id = idConversa });

            return new ConversationHistoryDto
            {
                Conversa = detalhes,
                Mensagens = mensagens,
                Page = page,
                PageSize = pageSize,
                Total = total
            };
        }

        public async Task<bool> AtribuirConversaAsync(Guid idConversa, int idAgente)
        {
            const string sql = @"
UPDATE conversas
   SET id_agente_atribuido = @IdAgente,
       estado = 'em_atendimento'::estado_conversa_enum,
       data_atualizacao = NOW()
 WHERE id = @Id;";

            await using var cx = new NpgsqlConnection(_connectionString);
            var linhas = await cx.ExecuteAsync(sql, new { Id = idConversa, IdAgente = idAgente });
            return linhas > 0;
        }

        public async Task<bool> FecharConversaAsync(Guid idConversa, int? idAgente, string? motivo)
        {
            var estado = idAgente.HasValue ? "fechado_agente" : "fechado_automaticamente";
            const string sql = @"
UPDATE conversas
   SET estado = @Estado::estado_conversa_enum,
       motivo_fechamento = @Motivo,
       fechado_por_id = @FechadoPorId,
       data_fechamento = NOW(),
       data_atualizacao = NOW()
 WHERE id = @Id;";

            await using var cx = new NpgsqlConnection(_connectionString);
            var linhas = await cx.ExecuteAsync(sql, new
            {
                Id = idConversa,
                Estado = estado,
                Motivo = string.IsNullOrWhiteSpace(motivo) ? null : motivo.Trim(),
                FechadoPorId = idAgente
            });

            return linhas > 0;
        }

        public async Task<ConversationDetailsDto?> ArquivarConversaAsync(Guid idConversa)
        {
            const string sql = @"
UPDATE conversas
   SET estado = 'arquivada'::estado_conversa_enum,
       data_atualizacao = NOW()
 WHERE id = @Id;";

            await using var cx = new NpgsqlConnection(_connectionString);
            var linhas = await cx.ExecuteAsync(sql, new { Id = idConversa });
            if (linhas == 0)
            {
                return null;
            }

            var detalhes = await cx.QueryFirstOrDefaultAsync<ConversationDetailsDto>(ConversationDetailsSql, new { Id = idConversa });
            if (detalhes == null)
            {
                return null;
            }

            detalhes.Estado = MapEstadoDbToApi(detalhes.Estado);
            return detalhes;
        }

        public async Task<ConversationDetailsDto?> ObterDetalhesConversaAsync(Guid idConversa)
        {
            await using var cx = new NpgsqlConnection(_connectionString);
            var detalhes = await cx.QueryFirstOrDefaultAsync<ConversationDetailsDto>(ConversationDetailsSql, new { Id = idConversa });
            if (detalhes == null)
            {
                return null;
            }

            detalhes.Estado = MapEstadoDbToApi(detalhes.Estado);
            return detalhes;
        }
        public async Task AtualizarEstadoAsync(Guid idConversa, EstadoConversa novoEstado)
        {
            const string sql = @"
                UPDATE conversas
                   SET estado = @NovoEstado::estado_conversa_enum,
                       data_atualizacao = NOW()
                 WHERE id = @IdConversa
                   AND NOT (
                       estado = ANY ('{fechado_automaticamente,fechado_agente,arquivada}'::estado_conversa_enum[])
                       AND @NovoEstado::estado_conversa_enum = ANY ('{aberto,em_atendimento}'::estado_conversa_enum[])
                   );";

            await using var cx = new NpgsqlConnection(_connectionString);
            await cx.ExecuteAsync(sql, new { IdConversa = idConversa, NovoEstado = MapEstadoToDatabase(novoEstado) });
        }

        public async Task SalvarContextoAsync(Guid idConversa, ConversationContext contexto)
        {
            const string sql = @"
                UPDATE conversas
                SET contexto_estado = @ContextoJson::jsonb,
                    data_atualizacao = NOW()
                WHERE id = @IdConversa;";

            var json = System.Text.Json.JsonSerializer.Serialize(contexto);

            await using var cx = new NpgsqlConnection(_connectionString);
            await cx.ExecuteAsync(sql, new { IdConversa = idConversa, ContextoJson = json });
        }

        public async Task<ConversationContext?> ObterContextoAsync(Guid idConversa)
        {
            const string sql = @"
                SELECT contexto_estado
                FROM conversas
                WHERE id = @IdConversa;";

            await using var cx = new NpgsqlConnection(_connectionString);
            var json = await cx.ExecuteScalarAsync<string>(sql, new { IdConversa = idConversa });

            if (string.IsNullOrWhiteSpace(json))
                return null;

            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<ConversationContext>(json);
            }
            catch
            {
                return null;
            }
        }

        public async Task LimparContextoAsync(Guid idConversa)
        {
            const string sql = @"
                UPDATE conversas
                SET contexto_estado = NULL,
                    data_atualizacao = NOW()
                WHERE id = @IdConversa;";

            await using var cx = new NpgsqlConnection(_connectionString);
            await cx.ExecuteAsync(sql, new { IdConversa = idConversa });
        }

    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) =================























