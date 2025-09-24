// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Threading.Tasks;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Models;
using Dapper;
using Npgsql;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using APIBack.Automation.Services;

namespace APIBack.Automation.Infra
{
    public class SqlMessageRepository : IMessageRepository
    {
        private readonly string _connectionString;
        private readonly ILogger<SqlMessageRepository> _logger;
        private static bool _indexesEnsured;

        public SqlMessageRepository(Microsoft.Extensions.Configuration.IConfiguration config, ILogger<SqlMessageRepository> logger)
        {
            _connectionString = config.GetConnectionString("DefaultConnection")
                                 ?? config["ConnectionStrings:DefaultConnection"]
                                 ?? throw new InvalidOperationException("Connection string 'DefaultConnection' nuo encontrada.");
            _logger = logger;

            // Garante �ndice �nico de id_provedor
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
                }
            }
        }

        public async Task<bool> ExistsByProviderIdAsync(string providerMessageId)
        {
            const string sql = "SELECT 1 FROM mensagens WHERE id_provedor = @Id LIMIT 1;";
            await using var cx = new NpgsqlConnection(_connectionString);
            var r = await cx.ExecuteScalarAsync<int?>(sql, new { Id = providerMessageId });
            return r.HasValue;
        }

        public async Task AddMessageAsync(Message mensagem, string? phoneNumberId, string? idWa)
        {
            // Timestamp base (UTC)
            DateTime quandoUtc;
            if (mensagem.DataHora == default)
                quandoUtc = DateTime.UtcNow;
            else if (mensagem.DataHora.Kind == DateTimeKind.Utc)
                quandoUtc = mensagem.DataHora;
            else if (mensagem.DataHora.Kind == DateTimeKind.Local)
                quandoUtc = mensagem.DataHora.ToUniversalTime();
            else
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

            // timestamps derivadas
            DateTime? dataEnvio = null, dataEntrega = null, dataLeitura = null;
            var st = status.Trim().ToLowerInvariant();
            if (direcao == "entrada")
            {
                dataEnvio = quandoUtc;
                dataEntrega = quandoUtc;
            }
            else
            {
                switch (st)
                {
                    case "fila":
                    case "enviado":
                    case "enviada":
                        dataEnvio = quandoUtc; break;
                    case "entregue":
                    case "entregada":
                        dataEnvio = quandoUtc; dataEntrega = quandoUtc; break;
                    case "lido":
                    case "lida":
                        dataEnvio = quandoUtc; dataEntrega = quandoUtc; dataLeitura = quandoUtc; break;
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

            // Garante que a conversa existe (id fornecido deve ser não vazio)
            if (mensagem.IdConversa == Guid.Empty)
                throw new ArgumentException("IdConversa obrigatório", nameof(mensagem));

            const string checkConv = "SELECT 1 FROM conversas WHERE id = @Id LIMIT 1;";
            var convExists = await cx.ExecuteScalarAsync<int?>(checkConv, new { Id = mensagem.IdConversa }, transaction: tx);
            if (!convExists.HasValue)
            {
                // Descobre id_estabelecimento pelo phone_number_id
                const string sqlWaba = "SELECT id_estabelecimento FROM waba_phone WHERE phone_number_id = @PhoneNumberId LIMIT 1;";
                var idEstabelecimento = await cx.ExecuteScalarAsync<Guid?>(sqlWaba, new { PhoneNumberId = phoneNumberId }, transaction: tx);
                if (!idEstabelecimento.HasValue || idEstabelecimento.Value == Guid.Empty)
                    throw new InvalidOperationException("Estabelecimento não encontrado para este WABA");

                // Resolve cliente por telefone E.164
                var telefoneE164 = APIBack.Automation.Helpers.TelefoneHelper.ToE164(idWa ?? string.Empty);
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
  SELECT 1 FROM clientes WHERE id_estabelecimento = @IdEstabelecimento AND telefone_e164 = @Telefone
)
RETURNING id;";
                    var insertedId = await cx.ExecuteScalarAsync<Guid?>(sqlCliIns, new
                    {
                        Id = novoClienteId,
                        IdEstabelecimento = idEstabelecimento,
                        Telefone = telefoneE164,
                        CriadoEm = agora,
                        AtualizadoEm = agora
                    }, transaction: tx);
                    if (insertedId.HasValue) idCliente = insertedId;
                    else idCliente = await cx.ExecuteScalarAsync<Guid?>(sqlCliSel, new { Tel = telefoneE164 }, transaction: tx);
                    if (!idCliente.HasValue || idCliente.Value == Guid.Empty)
                        throw new InvalidOperationException("Falha ao criar cliente");
                }

                // Cria conversa
                var agoraUtc = DateTime.UtcNow;
                const string sqlConv = @"
INSERT INTO conversas
  (id, id_estabelecimento, id_cliente, canal, estado, id_agente_atribuido, data_primeira_mensagem, data_ultima_mensagem, data_ultima_entrada, data_ultima_saida, janela_24h_inicio, janela_24h_fim, qtd_nao_lidas, motivo_fechamento, data_criacao, data_atualizacao)
VALUES
  (@Id, @IdEstabelecimento, @IdCliente, 'whatsapp', 'aberto'::estado_conversa_enum, NULL, @Quando, @Quando, @Quando, NULL, @Quando, @Quando + interval '24 hour', 1, NULL, @Quando, @Quando)
ON CONFLICT (id) DO UPDATE SET
  data_ultima_mensagem = EXCLUDED.data_ultima_mensagem,
  data_atualizacao     = EXCLUDED.data_atualizacao;";

                await cx.ExecuteAsync(sqlConv, new
                {
                    Id = mensagem.IdConversa,
                    IdEstabelecimento = idEstabelecimento.Value,
                    IdCliente = idCliente.Value,
                    Quando = quandoUtc
                }, transaction: tx);
            }

            // Insere mensagem e atualiza conversa
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

        public async Task<IReadOnlyList<Message>> GetByConversationAsync(Guid idConversa, int limit = 200)
        {
            const string sql = @"SELECT 
    m.id,
    m.id_conversa,
    m.direcao,
    m.tipo,
    m.status,
    m.id_provedor,
    m.codigo_erro,
    m.mensagem_erro,
    m.tentativas,
    m.criada_por,
    m.data_envio,
    m.data_entrega,
    m.data_leitura,
    m.data_criacao,
    m.conteudo
FROM mensagens m
JOIN conversas c ON c.id = m.id_conversa
WHERE m.id_conversa = @IdConversa
  AND c.estado NOT IN (
        'fechado_automaticamente'::estado_conversa_enum,
        'fechado_agente'::estado_conversa_enum,
        'arquivada'::estado_conversa_enum
  )
ORDER BY m.data_criacao ASC
LIMIT @Limit;
";

            await using var cx = new NpgsqlConnection(_connectionString);
            var list = await cx.QueryAsync<Message>(sql, new
            {
                IdConversa = idConversa,
                Limit = limit
            });
            return list.AsList();
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================




