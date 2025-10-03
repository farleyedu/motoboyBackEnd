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

namespace APIBack.Automation.Services
{
    public class ConversationService
    {
        private readonly IConversationRepository _repositorio;
        private readonly ILogger<ConversationService> _logger;
        private readonly IQueueBus _queueBus;
        private readonly IClienteRepository _repositorioClientes;
        private readonly IWabaPhoneRepository _wabaPhoneRepository;
        private readonly IMessageService _mensagemService;
        private readonly IConfiguration _configuration;

        // mapeia waId -> conversationId (in-memory)
        private readonly ConcurrentDictionary<string, Guid> _waParaConversa = new(StringComparer.OrdinalIgnoreCase);

        // armazenamento in-memory de mensagens por conversa
        private readonly ConcurrentDictionary<Guid, ConcurrentQueue<Message>> _mensagens = new();

        public ConversationService(
            IConversationRepository repo,
            ILogger<ConversationService> logger,
            IQueueBus queueBus,
            IClienteRepository repositorioClientes,
            IWabaPhoneRepository wabaPhoneRepository,
            IConfiguration configuration,
            IMessageService mensagemService)
        {
            _repositorio = repo;
            _logger = logger;
            _queueBus = queueBus;
            _repositorioClientes = repositorioClientes;
            _wabaPhoneRepository = wabaPhoneRepository;
            _configuration = configuration;
            _mensagemService = mensagemService;
        }

        /// <summary>
        /// Adiciona uma mensagem de entrada (do cliente) e persiste no banco.
        /// </summary>
        /// <param name="idWa">WhatsApp ID do cliente (ex: 5534999887766)</param>
        /// <param name="idMensagemWa">ID único da mensagem do WhatsApp</param>
        /// <param name="conteudo">Texto da mensagem</param>
        /// <param name="displayPhoneNumber">Número de telefone visível do estabelecimento (ex: +5534999887766) - USADO PARA BUSCAR ESTABELECIMENTO</param>
        /// <param name="dataMensagemUtc">Data/hora UTC da mensagem</param>
        /// <param name="tipoOrigem">Tipo da mensagem (text, image, etc)</param>
        public async Task<Message?> AcrescentarEntradaAsync(
            string idWa,
            string idMensagemWa,
            string conteudo,
            string displayPhoneNumber,
            DateTime? dataMensagemUtc = null,
            string? tipoOrigem = null,
            string? telefoneContato = null)
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

            // ============== CORREÇÃO: SEMPRE USAR DISPLAY PHONE NUMBER ==============
            // Buscar estabelecimento pelo número visível (display_phone_number)
            Guid? idEstabelecimento = null;
            if (!string.IsNullOrWhiteSpace(displayPhoneNumber))
            {
                idEstabelecimento = await _wabaPhoneRepository
                    .ObterIdEstabelecimentoPorDisplayPhoneAsync(displayPhoneNumber);

                if (idEstabelecimento == null || idEstabelecimento == Guid.Empty)
                {
                    _logger.LogWarning(
                        "Estabelecimento não encontrado para display_phone_number={Display}",
                        displayPhoneNumber);
                }
                else
                {
                    _logger.LogDebug(
                        "Estabelecimento {IdEstabelecimento} encontrado para display={Display}",
                        idEstabelecimento,
                        displayPhoneNumber);
                }
            }
            // =========================================================================

            // Fallback para estabelecimento padrão se não encontrar
            if (idEstabelecimento == null || idEstabelecimento == Guid.Empty)
            {
                var fallbackEstabelecimentoId = _configuration.GetValue<string>("WhatsApp:FallbackEstabelecimentoId");
                if (!string.IsNullOrWhiteSpace(fallbackEstabelecimentoId) &&
                    Guid.TryParse(fallbackEstabelecimentoId, out var fallbackGuid))
                {
                    idEstabelecimento = fallbackGuid;
                    _logger.LogWarning(
                        "Usando estabelecimento fallback {IdEstabelecimento} para display_phone_number={Display}",
                        idEstabelecimento,
                        displayPhoneNumber);
                }
                else
                {
                    _logger.LogError(
                        "Não foi possível resolver id_estabelecimento para display_phone_number={Display} e não há fallback configurado",
                        displayPhoneNumber);
                    throw new InvalidOperationException(
                        $"Não foi possível resolver id_estabelecimento para display_phone_number={displayPhoneNumber}");
                }
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

            var idCliente = await _repositorioClientes.GarantirClienteAsync(telefoneE164, idEstabelecimento.Value);

            // Obter ou criar conversa
            var idConversa = await _repositorio.ObterIdConversaPorClienteAsync(idCliente, idEstabelecimento.Value);
            if (idConversa == Guid.Empty) idConversa = Guid.NewGuid();

            _waParaConversa[idWa] = idConversa; // cache auxiliar

            var existente = await _repositorio.ObterPorIdAsync(idConversa);
            var conversa = existente ?? new Conversation
            {
                IdConversa = idConversa,
                IdEstabelecimento = idEstabelecimento.Value,
                IdCliente = idCliente,
                IdWa = idWa,
                Modo = ModoConversa.Bot,
                CriadoEm = DateTime.UtcNow,
                AtualizadoEm = DateTime.UtcNow,
                MessageIdWhatsapp = idMensagemWa
            };

            // Garante WaId e Estabelecimento salvos
            conversa.IdWa = idWa;
            conversa.IdEstabelecimento = idEstabelecimento.Value;

            if (string.IsNullOrWhiteSpace(conversa.TelefoneCliente) && !string.IsNullOrWhiteSpace(telefoneE164))
            {
                conversa.TelefoneCliente = telefoneE164;
            }

            var conversaInserida = await _repositorio.InserirOuAtualizarAsync(conversa);
            if (!conversaInserida)
            {
                _logger.LogError("Falha ao inserir/atualizar conversa {IdConversa}", idConversa);
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

            return mensagem;
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

        public async Task<ConversationResponse?> ObterConversaRespostaAsync(Guid idConversa, int ultimasN = 20)
        {
            var conversa = await _repositorio.ObterPorIdAsync(idConversa);
            if (conversa == null) return null;

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
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
