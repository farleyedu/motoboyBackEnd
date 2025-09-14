// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Models;

namespace APIBack.Automation.Infra
{
    public class InMemoryConversationRepository : IConversationRepository
    {
        private readonly ConcurrentDictionary<Guid, Conversation> _conversas = new();
        private readonly ConcurrentDictionary<string, byte> _idsMensagemWa = new(StringComparer.OrdinalIgnoreCase);

        public Task<Conversation?> ObterPorIdAsync(Guid id)
        {
            _conversas.TryGetValue(id, out var conversa);
            return Task.FromResult(conversa);
        }

        public Task<bool> InserirOuAtualizarAsync(Conversation conversa)
        {
            conversa.AtualizadoEm = DateTime.UtcNow;
            _conversas.AddOrUpdate(conversa.IdConversa, conversa, (_, __) => conversa);
            return Task.FromResult(true);
        }

        public Task DefinirModoAsync(Guid id, ModoConversa modo, string? agenteDesignado)
        {
            _conversas.AddOrUpdate(
                id,
                _ => new Conversation
                {
                    IdConversa = id,
                    Modo = modo,
                    AgenteDesignado = agenteDesignado,
                    CriadoEm = DateTime.UtcNow,
                    AtualizadoEm = DateTime.UtcNow,
                },
                (_, atual) =>
                {
                    atual.Modo = modo;
                    atual.AgenteDesignado = agenteDesignado;
                    atual.AtualizadoEm = DateTime.UtcNow;
                    return atual;
                });
            return Task.CompletedTask;
        }

        public Task AcrescentarMensagemAsync(Message mensagem)
        {
            // Idempotencia por IdMensagemWa
            if (!string.IsNullOrWhiteSpace(mensagem.IdMensagemWa))
            {
                _idsMensagemWa.TryAdd(mensagem.IdMensagemWa, 1);
            }

            // Atualiza estado da conversa conforme direcao
            _conversas.AddOrUpdate(
                mensagem.IdConversa,
                _ => new Conversation
                {
                    IdConversa = mensagem.IdConversa,
                    IdWa = string.Empty,
                    UltimoUsuarioEm = mensagem.Direcao == DirecaoMensagem.Entrada ? mensagem.DataHora : default,
                    Janela24hExpiraEm = mensagem.Direcao == DirecaoMensagem.Entrada ? mensagem.DataHora.AddHours(24) : null,
                    CriadoEm = DateTime.UtcNow,
                    AtualizadoEm = DateTime.UtcNow,
                },
                (_, atual) =>
                {
                    if (mensagem.Direcao == DirecaoMensagem.Entrada)
                    {
                        atual.UltimoUsuarioEm = mensagem.DataHora;
                        atual.Janela24hExpiraEm = mensagem.DataHora.AddHours(24);
                    }
                    atual.AtualizadoEm = DateTime.UtcNow;
                    return atual;
                });

            return Task.CompletedTask;
        }

        public Task<bool> ExisteIdMensagemPorProvedorWaAsync(string idMensagemWa)
        {
            var existe = _idsMensagemWa.ContainsKey(idMensagemWa);
            return Task.FromResult(existe);
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
