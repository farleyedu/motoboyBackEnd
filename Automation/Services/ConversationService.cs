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

namespace APIBack.Automation.Services
{
    public class ConversationService
    {
        private readonly IConversationRepository _repositorio;
        private readonly ILogger<ConversationService> _logger;

        // mapeia waId -> conversationId (in-memory)
        private readonly ConcurrentDictionary<string, Guid> _waParaConversa = new(StringComparer.OrdinalIgnoreCase);

        // armazenamento in-memory de mensagens por conversa
        private readonly ConcurrentDictionary<Guid, ConcurrentQueue<Message>> _mensagens = new();

        public ConversationService(IConversationRepository repo, ILogger<ConversationService> logger)
        {
            _repositorio = repo;
            _logger = logger;
        }

        public async Task<Message?> AcrescentarEntradaAsync(string idWa, string idMensagemWa, string conteudo)
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

            var idConversa = _waParaConversa.GetOrAdd(idWa, _ => Guid.NewGuid());
            var existente = await _repositorio.ObterPorIdAsync(idConversa);
            var conversa = existente ?? new Conversation
            {
                IdConversa = idConversa,
                IdWa = idWa,
                Modo = ModoConversa.Bot,
                CriadoEm = DateTime.UtcNow,
                AtualizadoEm = DateTime.UtcNow,
            };

            // Garante WaId salvo
            conversa.IdWa = idWa;
            await _repositorio.InserirOuAtualizarAsync(conversa);
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
                DataHora = DateTime.UtcNow,
            };

            EnfileirarMensagem(mensagem);
            await _repositorio.AcrescentarMensagemAsync(mensagem);

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
            await _repositorio.AcrescentarMensagemAsync(mensagem);
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
                await _repositorio.AcrescentarMensagemAsync(msg);
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
