// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Threading.Tasks;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Models;
using Dapper;
using APIBack.Automation.Helpers;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace APIBack.Automation.Infra
{
    public class SqlConversationRepository : IConversationRepository
    {
        private readonly string _connectionString;
        private readonly Microsoft.Extensions.Configuration.IConfiguration _configuration;
        private readonly IWabaPhoneRepository? _wabaPhoneRepository;
        private static bool _indexesEnsured;

        public SqlConversationRepository(IConfiguration config)
        {
            _connectionString = config.GetConnectionString("DefaultConnection")
                                 ?? config["ConnectionStrings:DefaultConnection"]
                                 ?? throw new InvalidOperationException("Connection string 'DefaultConnection' não encontrada.");

            // Garante índice único de idempotência (executa uma vez por processo)
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
                    // Não interrompe a aplicação em caso de erro ao criar índice.
                }
            }
        }

        // Helpers de conversão para DateTime (UTC-safe)
        private static DateTime? ToUtc(DateTime? dt)
            => dt.HasValue ? DateTime.SpecifyKind(dt.Value, DateTimeKind.Utc) : (DateTime?)null;

        private static DateTime ToUtcNonNull(DateTime dt)
            => DateTime.SpecifyKind(dt, DateTimeKind.Utc);

        public async Task<Conversation?> ObterPorIdAsync(Guid id)
        {
            const string sql = @"
SELECT
  id                         AS Id,
  id_estabelecimento         AS IdEstabelecimento,
  id_cliente                 AS IdCliente,
  canal                      AS Canal,
  estado                     AS Estado,
  id_agente_atribuido        AS IdAgenteAtribuido,
  data_primeira_mensagem     AS DataPrimeiraMensagem,
  data_ultima_mensagem       AS DataUltimaMensagem,
  data_ultima_entrada        AS DataUltimaEntrada,
  data_ultima_saida          AS DataUltimaSaida,
  janela_24h_inicio          AS Janela24hInicio,
  janela_24h_fim             AS Janela24hFim,
  qtd_nao_lidas              AS QtdNaoLidas,
  motivo_fechamento          AS MotivoFechamento,
  data_criacao               AS DataCriacao,
  data_atualizacao           AS DataAtualizacao
FROM conversas
WHERE id = @Id;";

            await using var cx = new NpgsqlConnection(_connectionString);
            var row = await cx.QueryFirstOrDefaultAsync<(Guid Id, Guid IdEstabelecimento, Guid IdCliente, string? Canal, string? Estado, int? IdAgenteAtribuido, DateTime? DataPrimeiraMensagem, DateTime? DataUltimaMensagem, DateTime? DataUltimaEntrada, DateTime? DataUltimaSaida, DateTime? Janela24hInicio, DateTime? Janela24hFim, int QtdNaoLidas, string? MotivoFechamento, DateTime DataCriacao, DateTime DataAtualizacao)>(sql, new { Id = id });
            if (row.Equals(default((Guid, Guid, Guid, string?, string?, Guid?, DateTime?, DateTime?, DateTime?, DateTime?, DateTime?, DateTime?, int, string?, DateTime, DateTime)))) return null;

            var conv = new Conversation
            {
                IdConversa = row.Id,
                IdEstabelecimento = row.IdEstabelecimento,
                IdCliente = row.IdCliente,
                IdWa = string.Empty, // não existe no schema
                Modo = row.IdAgenteAtribuido == null ? ModoConversa.Bot : ModoConversa.Humano,
                AgenteDesignado = row.IdAgenteAtribuido?.ToString(),
                UltimoUsuarioEm = ToUtc(row.DataUltimaEntrada) ?? default,
                Janela24hExpiraEm = ToUtc(row.Janela24hFim),
                CriadoEm = ToUtcNonNull(row.DataCriacao),
                AtualizadoEm = ToUtcNonNull(row.DataAtualizacao)
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
  (id, id_estabelecimento, id_cliente, canal, estado, id_agente_atribuido, data_primeira_mensagem, data_ultima_mensagem, data_ultima_entrada, data_ultima_saida, janela_24h_inicio, janela_24h_fim, qtd_nao_lidas, motivo_fechamento, data_criacao, data_atualizacao)
VALUES
  (@Id, @IdEstabelecimento, @IdCliente, @Canal::canal_chat_enum, @Estado::estado_conversa_enum, @IdAgenteAtribuido, @DataPrimeiraMensagem, @DataUltimaMensagem, @DataUltimaEntrada, @DataUltimaSaida, @Janela24hInicio, @Janela24hFim, @QtdNaoLidas, @MotivoFechamento, @DataCriacao, @DataAtualizacao)
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
                    Estado = conversa.Estado,
                    IdAgenteAtribuido = conversa.Modo == ModoConversa.Humano
                    && int.TryParse(conversa.AgenteDesignado, out var agenteInt)
                        ? (int?)agenteInt
                        : null,
                    DataPrimeiraMensagem = dataPrimeiraMensagem,
                    DataUltimaMensagem = dataUltimaMensagem,
                    DataUltimaEntrada = dataUltimaEntrada,
                    DataUltimaSaida = dataUltimaSaida,
                    Janela24hInicio = janela24hInicio,
                    Janela24hFim = janela24hFim,
                    QtdNaoLidas = 0,
                    MotivoFechamento = (string?)null,
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
            string sql;
            object param;

            if (modo == ModoConversa.Humano)
            {
                sql = @"
                      UPDATE conversas
                         SET id_agente_atribuido = @AgenteId,
                             estado               = 'agente'::estado_conversa_enum,
                             data_atualizacao     = NOW()
                       WHERE id = @Id;";
                param = new { Id = id, AgenteId = (object?)agenteId };
            }
            else
            {
                sql = @"
                      UPDATE conversas
                         SET id_agente_atribuido = NULL,
                             estado               = 'aberta'::estado_conversa_enum,
                             data_atualizacao     = NOW()
                       WHERE id = @Id;";
                param = new { Id = id };
            }

            await using var cx = new NpgsqlConnection(_connectionString);
            await cx.ExecuteAsync(sql, param);
        }

        public async Task AcrescentarMensagemAsync(Message mensagem, string? phoneNumberId, string idWa = null)
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
            var tipo = string.IsNullOrWhiteSpace(mensagem.Tipo) ? "texto" : mensagem.Tipo!;
            var status = string.IsNullOrWhiteSpace(mensagem.Status)
                          ? (mensagem.Direcao == DirecaoMensagem.Entrada ? "entregue" : "fila")
                          : mensagem.Status!;
            var idProv = !string.IsNullOrWhiteSpace(mensagem.IdProvedor) ? mensagem.IdProvedor : mensagem.IdMensagemWa;
            var criadaPor = string.IsNullOrWhiteSpace(mensagem.CriadaPor)
                            ? (mensagem.Direcao == DirecaoMensagem.Entrada ? "bot" : "agente:1")
                            : mensagem.CriadaPor!;

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
                    const string sqlWaba = "SELECT id_estabelecimento FROM waba_phone WHERE phone_number_id = @PhoneNumberId LIMIT 1;";
                    var idEstabelecimento = await cx.ExecuteScalarAsync<Guid?>(sqlWaba, new { PhoneNumberId = wabaPhoneNumberId }, transaction: tx);
                    if (!idEstabelecimento.HasValue || idEstabelecimento.Value == Guid.Empty)
                        throw new InvalidOperationException("Estabelecimento não encontrado para este WABA");

                    // Resolve IdCliente por telefone normalizado (cria se não existir)
                    var telefoneE164 = APIBack.Automation.Helpers.TelefoneHelper.ToE164(telefoneClienteRaw ?? string.Empty);
                    const string sqlCliSel = "SELECT id FROM clientes WHERE telefone_e164 = @Tel LIMIT 1;";
                    var idCliente = await cx.ExecuteScalarAsync<Guid?>(sqlCliSel, new { Tel = telefoneE164 }, transaction: tx);

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
                            idCliente = await cx.ExecuteScalarAsync<Guid?>(sqlCliSel, new { Tel = telefoneE164 }, transaction: tx);
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
                                  ORDER BY data_criacao DESC
                                  LIMIT 1;";
            await using var cx = new NpgsqlConnection(_connectionString);
            var found = await cx.ExecuteScalarAsync<Guid?>(sql, new { IdCliente = idCliente, IdEstabelecimento = idEstabelecimento });
            return found ?? Guid.Empty;
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) =================




