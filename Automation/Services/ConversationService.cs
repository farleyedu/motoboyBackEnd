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
            IQueueBus queueBus,           IClienteRepository repositorioClientes,
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

        public async Task<Message?> AcrescentarEntradaAsync(string idWa, string idMensagemWa, string conteudo, string phoneNumberId, DateTime? dataMensagemUtc = null)
        {
            if (string.IsNullOrWhiteSpace(idMensagemWa))
            {
                _logger.LogWarning("Mensagem de entrada sem IdMensagemWa para IdWa={WaId}", idWa);
                return null;
            }


            ///voltar depois
            //if (await _repositorio.ExisteIdMensagemPorProvedorWaAsync(idMensagemWa))
            //{
            //    _logger.LogInformation("Ignorando duplicata de entrada IdMensagemWa={WaMessageId}", idMensagemWa);
            //    return null;
            //}

            //////////////////////////////////////////////////
            ///
            // Em desenvolvimento, não bloqueia duplicatas para facilitar testes.
            var ambiente = _configuration.GetValue<string>("ASPNETCORE_ENVIRONMENT");
            var isDev = string.Equals(ambiente, "Development", StringComparison.OrdinalIgnoreCase);

            // Verifica duplicidade apenas uma vez
            var duplicata = await _repositorio.ExisteIdMensagemPorProvedorWaAsync(idMensagemWa);
            if (duplicata)
            {
                if (isDev)
                {
                    _logger.LogWarning("DEV: Duplicata detectada IdMensagemWa={WaMessageId}, processamento continuará para testes.", idMensagemWa);
                    // segue o fluxo em DEV
                }
                else
                {
                    _logger.LogInformation("Ignorando duplicata de entrada IdMensagemWa={WaMessageId}", idMensagemWa);
                    return null; // bloqueia fora de DEV
                }
            }
            //////////////////////////////////////////////////////////////////////////////
            ///


            // Resolve o id_estabelecimento usando o phone_number_id
            Guid? idEstabelecimento = null;
            if (!string.IsNullOrWhiteSpace(phoneNumberId))
            {
                idEstabelecimento = await _wabaPhoneRepository.ObterIdEstabelecimentoPorPhoneNumberIdAsync(phoneNumberId);
            }

            // Fallback para estabelecimento padrão se não encontrar
            if (idEstabelecimento == null || idEstabelecimento == Guid.Empty)
            {
                var fallbackEstabelecimentoId = _configuration.GetValue<string>("WhatsApp:FallbackEstabelecimentoId");
                if (!string.IsNullOrWhiteSpace(fallbackEstabelecimentoId) && Guid.TryParse(fallbackEstabelecimentoId, out var fallbackGuid))
                {
                    idEstabelecimento = fallbackGuid;
                    _logger.LogWarning("Usando estabelecimento fallback {IdEstabelecimento} para phone_number_id {PhoneNumberId}", idEstabelecimento, phoneNumberId);
                }
                else
                {
                    _logger.LogError("Não foi possível resolver id_estabelecimento para phone_number_id {PhoneNumberId} e não há fallback configurado", phoneNumberId);
                    throw new InvalidOperationException($"Não foi possível resolver id_estabelecimento para phone_number_id {phoneNumberId}");
                }
            }

            var telefoneE164 = APIBack.Automation.Helpers.TelefoneHelper.ToE164(idWa);
            var idCliente = await _repositorioClientes.GarantirClienteAsync(telefoneE164, idEstabelecimento.Value);
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

            // Garante WaId salvo
            conversa.IdWa = idWa;
            var conversaInserida = await _repositorio.InserirOuAtualizarAsync(conversa);
            if (!conversaInserida)
            {
                _logger.LogError("Falha ao inserir/atualizar conversa {IdConversa}", idConversa);
            }
            if (existente == null)
            {
                _logger.LogInformation("[Automation] Nova conversa criada: {ConversationId} para WaId={WaId}", idConversa, idWa);
            }

            var mensagem = new Message
            {
                IdConversa = idConversa,
                IdMensagemWa = idMensagemWa,
                Direcao = DirecaoMensagem.Entrada,
                Conteudo = conteudo,
                DataHora = dataMensagemUtc.HasValue
                    ? DateTime.SpecifyKind(dataMensagemUtc.Value, DateTimeKind.Utc)
                    : DateTime.UtcNow,
            };

            EnfileirarMensagem(mensagem);
            await _mensagemService.AdicionarMensagemAsync(mensagem, phoneNumberId, idWa);

            return mensagem;
        }


        public async Task<Message> AcrescentarSaidaAsync(Guid idConversa, string idWa, string conteudo)
        {
            var mensagem = new Message
            {
                IdConversa = idConversa,
                IdMensagemWa = $"local-{Guid.NewGuid():N}",
                Direcao = DirecaoMensagem.Saida,
                Conteudo = conteudo,
                DataHora = DateTime.UtcNow,
            };

            EnfileirarMensagem(mensagem);
            await _mensagemService.AdicionarMensagemAsync(mensagem, phoneNumberId: null, idWa);
            return mensagem;
        }

        public async Task DefinirModoBotAsync(Guid idConversa, string? mensagemTransicao = null)
        {
            await _repositorio.DefinirModoAsync(idConversa, ModoConversa.Bot, agenteDesignado: null);
            if (!string.IsNullOrWhiteSpace(mensagemTransicao))
            {
                var msg = new Message
                {
                    IdConversa = idConversa,
                    IdMensagemWa = $"local-{Guid.NewGuid():N}",
                    Direcao = DirecaoMensagem.Saida,
                    Conteudo = mensagemTransicao,
                    DataHora = DateTime.UtcNow,
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
                AgenteDesignado = conversa.AgenteDesignado,
                UltimoUsuarioEm = conversa.UltimoUsuarioEm ?? default(DateTime),
                Janela24hExpiraEm = conversa.Janela24hExpiraEm,
                CriadoEm = conversa.CriadoEm,
                AtualizadoEm = conversa.AtualizadoEm ?? default(DateTime),
                Mensagens = ultimas.Select(m => new ConversationMessageView
                {
                    IdMensagemWa = m.IdMensagemWa,
                    Direcao = m.Direcao.ToString(),
                    Conteudo = m.Conteudo,
                    MetadadosMidia = m.MetadadosMidia,
                    DataHora = m.DataHora
                }).ToList()
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




