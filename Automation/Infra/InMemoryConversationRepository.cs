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
        private readonly ConcurrentDictionary<(Guid Estab, string Tel), Guid> _clientes = new();

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

        public Task DefinirModoAsync(Guid id, ModoConversa modo, int? agenteId)
        {
            _conversas.AddOrUpdate(
                id,
                _ => new Conversation
                {
                    IdConversa = id,
                    Modo = modo,
                    AgenteDesignado = agenteId?.ToString(),
                    CriadoEm = DateTime.UtcNow,
                    AtualizadoEm = DateTime.UtcNow,
                },
                (_, atual) =>
                {
                    atual.Modo = modo;
                    atual.AgenteDesignado = agenteId?.ToString();
                    atual.AtualizadoEm = DateTime.UtcNow;
                    return atual;
                });
            return Task.CompletedTask;
        }

        public Task AcrescentarMensagemAsync(Message mensagem, string? phoneNumberId, string idWa = null)
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

        public Task<Guid> GarantirClienteAsync(string telefoneE164, Guid idEstabelecimento)
        {
            if (idEstabelecimento == Guid.Empty) throw new ArgumentException("idEstabelecimento obrigatório", nameof(idEstabelecimento));
            telefoneE164 ??= string.Empty;
            var key = (idEstabelecimento, telefoneE164);
            var id = _clientes.GetOrAdd(key, _ => Guid.NewGuid());
            return Task.FromResult(id);
        }

        public Task<bool> ExisteIdMensagemPorProvedorWaAsync(string idMensagemWa)
        {
            var existe = _idsMensagemWa.ContainsKey(idMensagemWa);
            return Task.FromResult(existe);
        }

        public Task<Guid> ObterIdConversaPorClienteAsync(Guid idCliente, Guid idEstabelecimento)
        {
            throw new NotImplementedException();
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
