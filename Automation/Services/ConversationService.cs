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
        private readonly IWabaPhoneRepository _wabaPhoneRepository;
        private readonly IConfiguration _configuration;

        // mapeia waId -> conversationId (in-memory)
        private readonly ConcurrentDictionary<string, Guid> _waParaConversa = new(StringComparer.OrdinalIgnoreCase);

        // armazenamento in-memory de mensagens por conversa
        private readonly ConcurrentDictionary<Guid, ConcurrentQueue<Message>> _mensagens = new();

        public ConversationService(
            IConversationRepository repo, 
            ILogger<ConversationService> logger,
            IQueueBus queueBus,
            IWabaPhoneRepository wabaPhoneRepository,
            IConfiguration configuration)
        {
            _repositorio = repo;
            _logger = logger;
            _queueBus = queueBus;
            _wabaPhoneRepository = wabaPhoneRepository;
            _configuration = configuration;
        }

        public async Task<Message?> AcrescentarEntradaAsync(string idWa, string idMensagemWa, string conteudo, string phoneNumberCliente, DateTime? dataMensagemUtc = null)
        {
            if (string.IsNullOrWhiteSpace(idMensagemWa))
            {
                _logger.LogWarning("Mensagem de entrada sem IdMensagemWa para IdWa={WaId}", idWa);
                return null;
            }

            if (await _repositorio.ExisteIdMensagemPorProvedorWaAsync(idMensagemWa))
            {
                _logger.LogInformation("Ignorando duplicata de entrada IdMensagemWa={WaMessageId}", idMensagemWa);
                return null;
            }

            // Resolve o id_estabelecimento usando o phone_number_id
            Guid? idEstabelecimento = null;
            if (!string.IsNullOrWhiteSpace(idWa))
            {
                idEstabelecimento = await _wabaPhoneRepository.ObterIdEstabelecimentoPorPhoneNumberIdAsync(phoneNumberCliente);
            }

            // Fallback para estabelecimento padrão se não encontrar
            if (idEstabelecimento == null || idEstabelecimento == Guid.Empty)
            {
                var fallbackEstabelecimentoId = _configuration.GetValue<string>("WhatsApp:FallbackEstabelecimentoId");
                if (!string.IsNullOrWhiteSpace(fallbackEstabelecimentoId) && Guid.TryParse(fallbackEstabelecimentoId, out var fallbackGuid))
                {
                    idEstabelecimento = fallbackGuid;
                    _logger.LogWarning("Usando estabelecimento fallback {IdEstabelecimento} para phone_number_id {PhoneNumberId}", idEstabelecimento, idWa);
                }
                else
                {
                    _logger.LogError("Não foi possível resolver id_estabelecimento para phone_number_id {PhoneNumberId} e não há fallback configurado", idWa);
                    throw new InvalidOperationException($"Não foi possível resolver id_estabelecimento para phone_number_id {idWa}");
                }
            }

            var idConversa = _waParaConversa.GetOrAdd(idWa, _ => Guid.NewGuid());
            var existente = await _repositorio.ObterPorIdAsync(idConversa);
            var conversa = existente ?? new Conversation
            {
                IdConversa = idConversa,
                IdEstabelecimento = idEstabelecimento.Value,
                IdCliente = Guid.NewGuid(), // TODO: Implementar resolução de cliente
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
            await _repositorio.AcrescentarMensagemAsync(mensagem, phoneNumberCliente, idWa);

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
            await _repositorio.AcrescentarMensagemAsync(mensagem, phoneNumberId: null, idWa);
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
                await _repositorio.AcrescentarMensagemAsync(msg, phoneNumberId: null);
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
