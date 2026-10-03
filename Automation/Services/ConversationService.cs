// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using APIBack.Automation.Dtos;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Dapper;
using Npgsql;

namespace APIBack.Automation.Services
{
    public class ConversationService : APIBack.Atendimento.IIngressoDeConversa
    {
        private readonly IConversationRepository _repositorio;
        private readonly ILogger<ConversationService> _logger;
        private readonly IClienteRepository _repositorioClientes;
        private readonly IWabaPhoneRepository _wabaPhoneRepository;
        private readonly IMessageService _mensagemService;
        private readonly IConfiguration _configuration;
        private readonly string _connectionString;

        // mapeia waId -> conversationId (in-memory)
        private readonly ConcurrentDictionary<string, Guid> _waParaConversa = new(StringComparer.OrdinalIgnoreCase);

        // armazenamento in-memory de mensagens por conversa
        private readonly ConcurrentDictionary<Guid, ConcurrentQueue<Message>> _mensagens = new();

        public ConversationService(
            IConversationRepository repo,
            ILogger<ConversationService> logger,
            IClienteRepository repositorioClientes,
            IWabaPhoneRepository wabaPhoneRepository,
            IConfiguration configuration,
            IMessageService mensagemService)
        {
            _repositorio = repo;
            _logger = logger;
            _repositorioClientes = repositorioClientes;
            _wabaPhoneRepository = wabaPhoneRepository;
            _configuration = configuration;
            _mensagemService = mensagemService;
            _connectionString = configuration.GetConnectionString("DefaultConnection")
                                ?? configuration["ConnectionStrings:DefaultConnection"]
                                ?? throw new InvalidOperationException("Connection string 'DefaultConnection' nao encontrada.");
        }

        /// <summary>
        /// Adiciona uma mensagem de entrada (do cliente) e persiste no banco.
        /// </summary>
        /// <param name="idWa">WhatsApp ID do cliente (ex: 5534999887766)</param>
        /// <param name="idMensagemWa">ID único da mensagem do WhatsApp</param>
        /// <param name="conteudo">Texto da mensagem</param>
        /// <param name="displayPhoneNumber">Número de telefone visível do estabelecimento (ex: +5534999887766) - USADO PARA BUSCAR ESTABELECIMENTO</param>
        /// <param name="phoneNumberId">Identificador do número WhatsApp no Meta; usado como fallback para resolver estabelecimento</param>
        /// <param name="dataMensagemUtc">Data/hora UTC da mensagem</param>
        /// <param name="tipoOrigem">Tipo da mensagem (text, image, etc)</param>
        public async Task<ConversationIngressResult?> AcrescentarEntradaAsync(
            string idWa,
            string idMensagemWa,
            string conteudo,
            string displayPhoneNumber,
            string? phoneNumberId = null,
            DateTime? dataMensagemUtc = null,
            string? tipoOrigem = null,
            string? telefoneContato = null,
            Guid? idEstabelecimentoDoCanal = null,
            Guid? idCanal = null)
        {
            if (string.IsNullOrWhiteSpace(idMensagemWa))
            {
                _logger.LogWarning("Mensagem de entrada sem IdMensagemWa para IdWa={WaId}", idWa);
                return null;
            }

            // Verificar duplicidade (com exceção em DEV)
            var ambiente = _configuration.GetValue<string>("ASPNETCORE_ENVIRONMENT");
            var isDev = string.Equals(ambiente, "Development", StringComparison.OrdinalIgnoreCase);

            var duplicata = await _repositorio.ExisteIdMensagemPorProvedorWaAsync(idMensagemWa);
            if (duplicata)
            {
                if (isDev)
                {
                    _logger.LogWarning(
                        "DEV: Duplicata detectada IdMensagemWa={WaMessageId}, processamento continuará para testes.",
                        idMensagemWa);
                }
                else
                {
                    _logger.LogInformation("Ignorando duplicata de entrada IdMensagemWa={WaMessageId}", idMensagemWa);
                    return null;
                }
            }

            // A loja vem do canal (numero de WhatsApp) que recebeu a mensagem, resolvido pelo WaEventoProcessor pelo
            // phone_number_id. Nao ha loja padrao nem busca por telefone de exibicao: numero desconhecido nunca chega aqui.
            if (!idEstabelecimentoDoCanal.HasValue || idEstabelecimentoDoCanal.Value == Guid.Empty)
            {
                _logger.LogError(
                    "[atend] ev=sem_loja motivo=canal_nao_informado wa={Wa} phone_number_id={PhoneNumberId} display={Display}",
                    idMensagemWa, phoneNumberId ?? "(null)", displayPhoneNumber);
                throw new InvalidOperationException("Mensagem sem canal resolvido: nao ha como saber a qual loja ela pertence.");
            }

            Guid? idEstabelecimento = idEstabelecimentoDoCanal;

            var estadoOperacional = await ObterEstadoOperacionalAsync(idEstabelecimento.Value);
            if (!estadoOperacional.EmpresaAtiva)
            {
                await RegistrarWebhookIgnoradoAsync(
                    estadoOperacional.EmpresaId,
                    idEstabelecimento.Value,
                    idMensagemWa,
                    idWa,
                    displayPhoneNumber,
                    phoneNumberId,
                    conteudo,
                    "empresa_desativada");
                _logger.LogInformation(
                    "[Automation] Entrada ignorada por empresa desativada. Empresa={EmpresaId} Estabelecimento={EstabelecimentoId} WaId={WaId}",
                    estadoOperacional.EmpresaId,
                    idEstabelecimento.Value,
                    idWa);
                return null;
            }

            // Garantir cliente existe
            var telefonePreferencialBruto = !string.IsNullOrWhiteSpace(telefoneContato) ? telefoneContato : idWa;
            string telefoneE164;

            try
            {
                telefoneE164 = APIBack.Automation.Helpers.TelefoneHelper.ToE164(telefonePreferencialBruto);

                if (telefoneE164.Length < 13 || telefoneE164 == "+55")
                {
                    _logger.LogWarning("Telefone incompleto após normalização: {Telefone}. Tentando fallback para idWa.", telefoneE164);

                    if (!string.Equals(telefonePreferencialBruto, idWa, StringComparison.Ordinal))
                    {
                        try
                        {
                            telefoneE164 = APIBack.Automation.Helpers.TelefoneHelper.ToE164(idWa);

                            if (telefoneE164.Length < 13)
                            {
                                _logger.LogError("Impossível obter telefone válido. idWa: {IdWa}, telefoneContato: {TelefoneContato}", idWa, telefoneContato);
                            }
                        }
                        catch (Exception exFallback)
                        {
                            _logger.LogError(exFallback, "Falha ao normalizar idWa como telefone: {IdWa}", idWa);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao normalizar telefone. Usando idWa como fallback: {IdWa}", idWa);
                telefoneE164 = APIBack.Automation.Helpers.TelefoneHelper.ToE164(idWa);
            }

            Guid idCliente;
            Guid idConversa;
            Guid idConversaGrupo;
            Guid idEstabelecimentoEfetivo;
            Conversation? existente;
            var conversaResolvidaPorTelefone = await ResolverConversaAbertaPorTelefoneAsync(telefoneE164, idEstabelecimento.Value);
            var conversaAbertaPorTelefone = conversaResolvidaPorTelefone.Conversa;

            if (conversaAbertaPorTelefone != null)
            {
                idCliente = conversaAbertaPorTelefone.IdCliente;
                idConversa = conversaAbertaPorTelefone.IdConversa;
                idConversaGrupo = conversaAbertaPorTelefone.IdConversaGrupo == Guid.Empty
                    ? conversaAbertaPorTelefone.IdConversa
                    : conversaAbertaPorTelefone.IdConversaGrupo;
                idEstabelecimentoEfetivo = conversaAbertaPorTelefone.IdEstabelecimento;
                existente = conversaAbertaPorTelefone;
            }
            else
            {
                idCliente = await _repositorioClientes.GarantirClienteAsync(telefoneE164, idEstabelecimento.Value);
                idConversa = Guid.NewGuid();
                existente = null;
                idConversaGrupo = idConversa;
                idEstabelecimentoEfetivo = idEstabelecimento.Value;
            }

            _waParaConversa[idWa] = idConversa; // cache auxiliar

            var conversa = existente ?? new Conversation
            {
                IdConversa = idConversa,
                IdConversaGrupo = idConversaGrupo,
                IdEstabelecimento = idEstabelecimentoEfetivo,
                IdCliente = idCliente,
                IdWa = idWa,
                Modo = ModoConversa.Bot,
                CriadoEm = DateTime.UtcNow,
                AtualizadoEm = DateTime.UtcNow,
                MessageIdWhatsapp = idMensagemWa
            };

            if (estadoOperacional.EmpresaPausada)
            {
                conversa.Modo = ModoConversa.Bot;
                conversa.AgenteDesignadoId = null;
                conversa.StatusAtendimento = "empresa_pausada";
                conversa.MotivoFechamento = "Empresa pausada temporariamente";
            }

            // O numero por onde o cliente chegou: a resposta (bot ou atendente) sai por ele.
            if (idCanal.HasValue) conversa.IdCanal = idCanal;

            // Garante WaId e Estabelecimento salvos
            conversa.IdWa = idWa;
            conversa.IdConversaGrupo = conversa.IdConversaGrupo == Guid.Empty ? idConversaGrupo : conversa.IdConversaGrupo;
            conversa.IdEstabelecimento = idEstabelecimentoEfetivo;
            conversa.IdCliente = idCliente;

            if (string.IsNullOrWhiteSpace(conversa.TelefoneCliente) && !string.IsNullOrWhiteSpace(telefoneE164))
            {
                conversa.TelefoneCliente = telefoneE164;
            }

            var conversaInserida = await _repositorio.InserirOuAtualizarAsync(conversa);
            if (!conversaInserida)
            {
                _logger.LogError("Falha ao inserir/atualizar conversa {IdConversa} — abortando processamento da mensagem", idConversa);
                throw new InvalidOperationException($"Não foi possível persistir a conversa {idConversa} antes de salvar a mensagem.");
            }

            if (existente == null)
            {
                _logger.LogInformation(
                    "[Automation] Nova conversa criada: {ConversationId} para WaId={WaId}",
                    idConversa,
                    idWa);
            }

            // Criar mensagem
            const string criador = "cliente";
            var dataMensagem = dataMensagemUtc.HasValue
                ? DateTime.SpecifyKind(dataMensagemUtc.Value, DateTimeKind.Utc)
                : DateTime.UtcNow;
            var tipoMapeado = MessageTypeMapper.MapType(tipoOrigem, DirecaoMensagem.Entrada, criador);

            var mensagem = new Message
            {
                IdConversa = idConversa,
                IdMensagemWa = idMensagemWa,
                Direcao = DirecaoMensagem.Entrada,
                Conteudo = conteudo,
                DataHora = dataMensagem,
                DataCriacao = dataMensagem,
                CriadaPor = criador,
                TipoOriginal = tipoOrigem,
                Tipo = tipoMapeado,
            };

            // Persistir
            EnfileirarMensagem(mensagem);
            await _mensagemService.AdicionarMensagemAsync(mensagem, displayPhoneNumber, idWa);

            return new ConversationIngressResult(
                mensagem,
                conversaResolvidaPorTelefone.ReiniciadaPorExpiracao,
                conversaResolvidaPorTelefone.DataFechamentoAnteriorManual,
                estadoOperacional.EmpresaPausada,
                NovaConversa: existente == null);
        }

        public async Task<Message> AcrescentarSaidaAsync(Guid idConversa, string idWa, string conteudo)
        {
            const string criador = "sistema";
            const string tipoOriginal = "text";
            var dataMensagem = DateTime.UtcNow;
            var tipoMapeado = MessageTypeMapper.MapType(tipoOriginal, DirecaoMensagem.Saida, criador);

            var mensagem = new Message
            {
                IdConversa = idConversa,
                IdMensagemWa = $"local-{Guid.NewGuid():N}",
                Direcao = DirecaoMensagem.Saida,
                Conteudo = conteudo,
                DataHora = dataMensagem,
                DataCriacao = dataMensagem,
                DataEnvio = dataMensagem,
                CriadaPor = criador,
                TipoOriginal = tipoOriginal,
                Tipo = tipoMapeado,
            };

            EnfileirarMensagem(mensagem);
            await _mensagemService.AdicionarMensagemAsync(mensagem, phoneNumberId: null, idWa);
            return mensagem;
        }

        public async Task DefinirModoBotAsync(Guid idConversa, string? mensagemTransicao = null)
        {
            await _repositorio.DefinirModoAsync(idConversa, ModoConversa.Bot, agenteId: null);
            if (!string.IsNullOrWhiteSpace(mensagemTransicao))
            {
                const string criador = "sistema";
                const string tipoOriginal = "text";
                var dataMensagem = DateTime.UtcNow;
                var tipoMapeado = MessageTypeMapper.MapType(tipoOriginal, DirecaoMensagem.Saida, criador);

                var msg = new Message
                {
                    IdConversa = idConversa,
                    IdMensagemWa = $"local-{Guid.NewGuid():N}",
                    Direcao = DirecaoMensagem.Saida,
                    Conteudo = mensagemTransicao,
                    DataHora = dataMensagem,
                    DataCriacao = dataMensagem,
                    DataEnvio = dataMensagem,
                    CriadaPor = criador,
                    TipoOriginal = tipoOriginal,
                    Tipo = tipoMapeado,
                };
                EnfileirarMensagem(msg);
                await _mensagemService.AdicionarMensagemAsync(msg, phoneNumberId: null, idWa: null);
            }
        }

        public async Task<ConversationResponse?> ObterConversaRespostaAsync(Guid idConversa, Guid? idEstabelecimento = null, int ultimasN = 20)
        {
            var conversa = await _repositorio.ObterPorIdAsync(idConversa, idEstabelecimento);
            if (conversa == null) return null;

            if (idEstabelecimento.HasValue)
            {
                var historico = await _repositorio.ObterHistoricoConversaAsync(idConversa, null, ultimasN, idEstabelecimento.Value);
                if (historico != null)
                {
                    return new ConversationResponse
                    {
                        IdConversa = historico.Conversa.Id,
                        IdWa = conversa.IdWa,
                        Modo = conversa.Modo,
                        AgenteDesignadoId = conversa.AgenteDesignadoId,
                        UltimoUsuarioEm = conversa.UltimoUsuarioEm ?? default(DateTime),
                        Janela24hExpiraEm = conversa.Janela24hExpiraEm,
                        CriadoEm = historico.Conversa.DataCriacao,
                        AtualizadoEm = historico.Conversa.DataAtualizacao,
                        Mensagens = historico.Mensagens
                            .Select(item => new ConversationMessageView
                            {
                                Id = item.Id.ToString(),
                                SenderId = string.Equals(item.CriadaPor, "cliente", StringComparison.OrdinalIgnoreCase) ? "in" : "out",
                                Msg = item.Conteudo ?? string.Empty,
                                Type = "text",
                                CreatedAt = item.DataCriacao
                            })
                            .ToList()
                    };
                }
            }

            var lista = _mensagens.GetOrAdd(idConversa, _ => new ConcurrentQueue<Message>()).ToArray();
            var ultimas = lista.Skip(Math.Max(0, lista.Length - ultimasN)).ToList();

            return new ConversationResponse
            {
                IdConversa = conversa.IdConversa,
                IdWa = conversa.IdWa,
                Modo = conversa.Modo,
                AgenteDesignadoId = conversa.AgenteDesignadoId,
                UltimoUsuarioEm = conversa.UltimoUsuarioEm ?? default(DateTime),
                Janela24hExpiraEm = conversa.Janela24hExpiraEm,
                CriadoEm = conversa.CriadoEm,
                AtualizadoEm = conversa.AtualizadoEm ?? default(DateTime),
                Mensagens = ultimas.Select(ConversationMessageView.FromMessage).ToList()
            };
        }

        private void EnfileirarMensagem(Message mensagem)
        {
            var fila = _mensagens.GetOrAdd(mensagem.IdConversa, _ => new ConcurrentQueue<Message>());
            fila.Enqueue(mensagem);
        }

        private async Task<ConversationLookupResult> ResolverConversaAbertaPorTelefoneAsync(string telefoneE164, Guid idEstabelecimentoEntrada)
        {
            if (string.IsNullOrWhiteSpace(telefoneE164))
            {
                return new ConversationLookupResult(null, false);
            }

            var abertas = (await _repositorio.ListarConversasAbertasPorTelefoneAsync(telefoneE164)).ToList();
            if (abertas.Count == 0)
            {
                return new ConversationLookupResult(null, false);
            }

            var preservada = abertas
                .Where(c => c.IdEstabelecimento == idEstabelecimentoEntrada)
                .OrderByDescending(ConversationTimestamp)
                .FirstOrDefault();

            if (preservada == null)
            {
                await _repositorio.FecharConversasAbertasPorTelefoneAsync(
                    telefoneE164,
                    idAgente: null,
                    motivo: "Troca de atendimento por telefone",
                    tipoFechamento: "manual");
                return new ConversationLookupResult(null, false);
            }

            var classificacao = await ClassificarConversaParaEntradaAsync(preservada);
            if (classificacao.ReiniciadaPorExpiracao)
            {
                return classificacao;
            }

            if (classificacao.Conversa == null)
            {
                return new ConversationLookupResult(null, false, classificacao.DataFechamentoAnteriorManual);
            }

            await _repositorio.FecharConversasAbertasPorTelefoneAsync(
                telefoneE164,
                idAgente: null,
                motivo: "Reconciliacao de conversa unica por telefone",
                tipoFechamento: "manual",
                preservarConversaId: classificacao.Conversa.IdConversa);

            _logger.LogInformation(
                "[Automation] Conversa preservada por telefone {Telefone}: {Conversa}",
                telefoneE164,
                classificacao.Conversa.IdConversa);

            return new ConversationLookupResult(
                await _repositorio.ObterPorIdAsync(classificacao.Conversa.IdConversa) ?? classificacao.Conversa,
                false);
        }

        private async Task<ConversationLookupResult> ClassificarConversaParaEntradaAsync(Conversation candidata)
        {
            var controle = await _repositorio.ObterControleConversaAsync(candidata.IdConversa);
            if (controle == null)
            {
                return new ConversationLookupResult(null, false);
            }

            var atualizada = await _repositorio.ObterPorIdAsync(candidata.IdConversa) ?? candidata;
            if (controle.CanBotReply || controle.CanManualReply || IsHumanActiveStatus(controle.Status))
            {
                return new ConversationLookupResult(atualizada, false);
            }

            if (string.Equals(controle.Status, "encerrada_manual", StringComparison.OrdinalIgnoreCase))
            {
                var dataFechamento = atualizada.DataFechamento ?? atualizada.AtualizadoEm ?? DateTime.UtcNow;
                return new ConversationLookupResult(null, false, dataFechamento);
            }

            return new ConversationLookupResult(null, false);
        }

        private static DateTime ConversationTimestamp(Conversation conversa)
            => conversa.AtualizadoEm ?? conversa.CriadoEm;

        private static bool IsHumanActiveStatus(string? status)
            => string.Equals(status, "em_andamento", StringComparison.OrdinalIgnoreCase)
               || string.Equals(status, "aguardando_cliente", StringComparison.OrdinalIgnoreCase)
               || string.Equals(status, "aguardando_interno", StringComparison.OrdinalIgnoreCase);

        private async Task<Guid?> ResolverEstabelecimentoAsync(string? displayPhoneNumber, string? phoneNumberId)
        {
            if (!string.IsNullOrWhiteSpace(displayPhoneNumber))
            {
                var idPorDisplay = await _wabaPhoneRepository.ObterIdEstabelecimentoPorDisplayPhoneAsync(displayPhoneNumber);
                if (idPorDisplay.HasValue && idPorDisplay.Value != Guid.Empty)
                {
                    _logger.LogDebug(
                        "Estabelecimento {IdEstabelecimento} encontrado para display={Display}",
                        idPorDisplay.Value,
                        displayPhoneNumber);
                    return idPorDisplay.Value;
                }

                _logger.LogWarning(
                    "Estabelecimento não encontrado para display_phone_number={Display}",
                    displayPhoneNumber);
            }

            if (!string.IsNullOrWhiteSpace(phoneNumberId))
            {
                var idPorPhoneNumberId = await _wabaPhoneRepository.ObterIdEstabelecimentoPorPhoneNumberIdAsync(phoneNumberId);
                if (idPorPhoneNumberId.HasValue && idPorPhoneNumberId.Value != Guid.Empty)
                {
                    _logger.LogInformation(
                        "Estabelecimento {IdEstabelecimento} resolvido via phone_number_id={PhoneNumberId} após falha no display_phone_number={Display}",
                        idPorPhoneNumberId.Value,
                        phoneNumberId,
                        displayPhoneNumber ?? "(null)");
                    return idPorPhoneNumberId.Value;
                }

                _logger.LogWarning(
                    "Estabelecimento não encontrado para phone_number_id={PhoneNumberId}",
                    phoneNumberId);
            }

            return null;
        }

        private sealed record ConversationLookupResult(Conversation? Conversa, bool ReiniciadaPorExpiracao, DateTime? DataFechamentoAnteriorManual = null);
        private sealed class EmpresaOperacionalState
        {
            public Guid EmpresaId { get; set; }
            public bool EmpresaAtiva { get; set; } = true;
            public bool EmpresaPausada { get; set; }
        }

        private async Task<EmpresaOperacionalState> ObterEstadoOperacionalAsync(Guid idEstabelecimento)
        {
            const string sql = @"
SELECT emp.id AS EmpresaId,
       COALESCE(emp.ativo, TRUE) AS EmpresaAtiva,
       COALESCE(emp.pausada, FALSE) AS EmpresaPausada
  FROM estabelecimentos e
  JOIN empresas emp ON emp.id = e.id_empresa
 WHERE e.id = @IdEstabelecimento
 LIMIT 1;";

            await using var connection = new NpgsqlConnection(_connectionString);
            var state = await connection.QueryFirstOrDefaultAsync<EmpresaOperacionalState>(sql, new { IdEstabelecimento = idEstabelecimento });
            return state ?? new EmpresaOperacionalState { EmpresaId = Guid.Empty, EmpresaAtiva = true, EmpresaPausada = false };
        }

        private async Task RegistrarWebhookIgnoradoAsync(
            Guid empresaId,
            Guid estabelecimentoId,
            string idMensagemWa,
            string idWa,
            string? displayPhoneNumber,
            string? phoneNumberId,
            string? conteudo,
            string motivo)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.ExecuteAsync(@"
INSERT INTO empresa_webhook_auditoria (
    id, id_empresa, id_estabelecimento, id_mensagem_wa, telefone_cliente,
    display_phone_number, phone_number_id, motivo, conteudo_preview, data_criacao
)
VALUES (
    @Id, @EmpresaId, @EstabelecimentoId, @IdMensagemWa, @TelefoneCliente,
    @DisplayPhoneNumber, @PhoneNumberId, @Motivo, @ConteudoPreview, NOW()
);",
                    new
                    {
                        Id = Guid.NewGuid(),
                        EmpresaId = empresaId == Guid.Empty ? (Guid?)null : empresaId,
                        EstabelecimentoId = estabelecimentoId == Guid.Empty ? (Guid?)null : estabelecimentoId,
                        IdMensagemWa = idMensagemWa,
                        TelefoneCliente = idWa,
                        DisplayPhoneNumber = displayPhoneNumber,
                        PhoneNumberId = phoneNumberId,
                        Motivo = motivo,
                        ConteudoPreview = string.IsNullOrWhiteSpace(conteudo)
                            ? null
                            : conteudo.Length > 500 ? conteudo[..500] : conteudo
                    });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao registrar auditoria de webhook ignorado para Empresa={EmpresaId}", empresaId);
            }
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================

