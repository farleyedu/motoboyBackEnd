// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using APIBack.Automation.Dtos;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Models;

namespace APIBack.Automation.Infra
{
    public class InMemoryConversationRepository : IConversationRepository
    {
        private readonly ConcurrentDictionary<Guid, Conversation> _conversas = new();
        private readonly ConcurrentDictionary<string, byte> _idsMensagemWa = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<(Guid Estab, string Tel), Guid> _clientes = new();
        private readonly ConcurrentDictionary<Guid, ConcurrentQueue<Message>> _mensagens = new();

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
                    AgenteDesignadoId = agenteId,
                    CriadoEm = DateTime.UtcNow,
                    AtualizadoEm = DateTime.UtcNow,
                },
                (_, atual) =>
                {
                    atual.Modo = modo;
                    atual.AgenteDesignadoId = agenteId;
                    atual.AtualizadoEm = DateTime.UtcNow;
                    return atual;
                });
            return Task.CompletedTask;
        }

        public Task AcrescentarMensagemAsync(Message mensagem, string? phoneNumberId, string? idWa = null)
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

            var fila = _mensagens.GetOrAdd(mensagem.IdConversa, _ => new ConcurrentQueue<Message>());
            fila.Enqueue(mensagem);

            return Task.CompletedTask;
        }

        public Task<Guid> GarantirClienteAsync(string telefoneE164, Guid idEstabelecimento)
        {
            if (idEstabelecimento == Guid.Empty) throw new ArgumentException("idEstabelecimento obrigatÃƒÂ³rio", nameof(idEstabelecimento));
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

        public Task AtualizarEstadoAsync(Guid idConversa, EstadoConversa novoEstado)
        {
            _conversas.AddOrUpdate(
                idConversa,
                _ => new Conversation
                {
                    IdConversa = idConversa,
                    Estado = novoEstado,
                    CriadoEm = DateTime.UtcNow,
                    AtualizadoEm = DateTime.UtcNow
                },
                (_, atual) =>
                {
                    var estadoFechado = atual.Estado == EstadoConversa.FechadoAutomaticamente
                        || atual.Estado == EstadoConversa.FechadoAgente
                        || atual.Estado == EstadoConversa.Arquivada;
                    var reabertura = novoEstado == EstadoConversa.Aberto
                        || novoEstado == EstadoConversa.EmAtendimento;

                    if (estadoFechado && reabertura)
                    {
                        return atual;
                    }

                    atual.Estado = novoEstado;
                    atual.AtualizadoEm = DateTime.UtcNow;

                    if (novoEstado == EstadoConversa.FechadoAgente || novoEstado == EstadoConversa.FechadoAutomaticamente)
                    {
                        atual.DataFechamento = DateTime.UtcNow;
                    }
                    else if (reabertura)
                    {
                        atual.DataFechamento = null;
                        atual.FechadoPorId = null;
                        atual.MotivoFechamento = null;
                    }

                    return atual;
                });

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ConversationListItemDto>> ListarConversasAsync(string? estado, int? idAgente, bool incluirArquivadas)
        {
            IEnumerable<Conversation> query = _conversas.Values;

            if (!string.IsNullOrWhiteSpace(estado) && TryMapEstado(estado, out var estadoFiltro))
            {
                query = query.Where(c => c.Estado == estadoFiltro);
            }

            if (idAgente.HasValue)
            {
                query = query.Where(c => c.AgenteDesignadoId == idAgente);
            }

            if (!incluirArquivadas)
            {
                query = query.Where(c => c.Estado != EstadoConversa.Arquivada);
            }

            var lista = query
                .OrderByDescending(c => c.AtualizadoEm ?? c.CriadoEm)
                .Select(c =>
                {
                    _mensagens.TryGetValue(c.IdConversa, out var fila);
                    var ultima = fila?.ToArray().LastOrDefault();
                    return new ConversationListItemDto
                    {
                        Id = c.IdConversa,
                        IdCliente = c.IdCliente,
                        ClienteNome = null,
                        Estado = MapEstadoToString(c.Estado),
                        IdAgenteAtribuido = c.AgenteDesignadoId,
                        DataPrimeiraMensagem = c.CriadoEm,
                        DataUltimaMensagem = c.AtualizadoEm,
                        DataCriacao = c.CriadoEm,
                        DataAtualizacao = c.AtualizadoEm ?? c.CriadoEm,
                        DataFechamento = c.DataFechamento,
                        UltimaMensagemConteudo = ultima?.Conteudo,
                        UltimaMensagemData = ultima?.DataCriacao ?? ultima?.DataEnvio,
                        UltimaMensagemCriadaPor = ultima?.CriadaPor
                    };
                })
                .ToList();

            return Task.FromResult<IReadOnlyList<ConversationListItemDto>>(lista);
        }

        public Task<ConversationHistoryDto?> ObterHistoricoConversaAsync(Guid idConversa, int page, int pageSize)
        {
            if (!_conversas.TryGetValue(idConversa, out var conversa))
            {
                return Task.FromResult<ConversationHistoryDto?>(null);
            }

            var detalhes = ToDetails(conversa);
            var fila = _mensagens.GetOrAdd(idConversa, _ => new ConcurrentQueue<Message>());
            var todas = fila.ToArray().OrderBy(m => m.DataCriacao ?? m.DataEnvio ?? m.DataHora).ToList();
            var skip = Math.Max(0, (page - 1) * pageSize);
            var mensagens = todas.Skip(skip).Take(pageSize).Select(m => new ConversationMessageItemDto
            {
                Id = m.Id,
                CriadaPor = m.CriadaPor ?? string.Empty,
                Conteudo = m.Conteudo,
                DataEnvio = m.DataEnvio,
                DataCriacao = m.DataCriacao ?? m.DataHora
            }).ToList();

            var resposta = new ConversationHistoryDto
            {
                Conversa = detalhes,
                Mensagens = mensagens,
                Page = page,
                PageSize = pageSize,
                Total = todas.Count
            };

            return Task.FromResult<ConversationHistoryDto?>(resposta);
        }

        public Task<bool> AtribuirConversaAsync(Guid idConversa, int idAgente)
        {
            var atualizado = false;
            _conversas.AddOrUpdate(
                idConversa,
                _ => new Conversation
                {
                    IdConversa = idConversa,
                    Estado = EstadoConversa.EmAtendimento,
                    AgenteDesignadoId = idAgente,
                    Modo = ModoConversa.Humano,
                    CriadoEm = DateTime.UtcNow,
                    AtualizadoEm = DateTime.UtcNow
                },
                (_, atual) =>
                {
                    atual.AgenteDesignadoId = idAgente;
                    atual.Modo = ModoConversa.Humano;
                    atual.Estado = EstadoConversa.EmAtendimento;
                    atual.AtualizadoEm = DateTime.UtcNow;
                    atualizado = true;
                    return atual;
                });

            return Task.FromResult(atualizado);
        }

        public Task<bool> FecharConversaAsync(Guid idConversa, int? idAgente, string? motivo)
        {
            if (!_conversas.TryGetValue(idConversa, out var conversa))
            {
                return Task.FromResult(false);
            }

            conversa.Estado = idAgente.HasValue ? EstadoConversa.FechadoAgente : EstadoConversa.FechadoAutomaticamente;
            conversa.Modo = ModoConversa.Bot;
            conversa.MotivoFechamento = motivo;
            conversa.FechadoPorId = idAgente;
            conversa.DataFechamento = DateTime.UtcNow;
            conversa.AtualizadoEm = DateTime.UtcNow;
            return Task.FromResult(true);
        }

        public Task<ConversationDetailsDto?> ArquivarConversaAsync(Guid idConversa)
        {
            if (!_conversas.TryGetValue(idConversa, out var conversa))
            {
                return Task.FromResult<ConversationDetailsDto?>(null);
            }

            conversa.Estado = EstadoConversa.Arquivada;
            conversa.AtualizadoEm = DateTime.UtcNow;
            return Task.FromResult<ConversationDetailsDto?>(ToDetails(conversa));
        }

        public Task<ConversationDetailsDto?> ObterDetalhesConversaAsync(Guid idConversa)
        {
            if (!_conversas.TryGetValue(idConversa, out var conversa))
            {
                return Task.FromResult<ConversationDetailsDto?>(null);
            }

            return Task.FromResult<ConversationDetailsDto?>(ToDetails(conversa));
        }

        public Task SalvarContextoAsync(Guid idConversa, ConversationContext contexto)
        {
            if (_conversas.TryGetValue(idConversa, out var conversa))
            {
                conversa.ContextoEstadoJson = System.Text.Json.JsonSerializer.Serialize(contexto);
                conversa.AtualizadoEm = DateTime.UtcNow;
            }
            return Task.CompletedTask;
        }

        public Task<ConversationContext?> ObterContextoAsync(Guid idConversa)
        {
            if (_conversas.TryGetValue(idConversa, out var conversa) && !string.IsNullOrWhiteSpace(conversa.ContextoEstadoJson))
            {
                try
                {
                    var contexto = System.Text.Json.JsonSerializer.Deserialize<ConversationContext>(conversa.ContextoEstadoJson);
                    return Task.FromResult<ConversationContext?>(contexto);
                }
                catch
                {
                    return Task.FromResult<ConversationContext?>(null);
                }
            }
            return Task.FromResult<ConversationContext?>(null);
        }

        public Task LimparContextoAsync(Guid idConversa)
        {
            if (_conversas.TryGetValue(idConversa, out var conversa))
            {
                conversa.ContextoEstadoJson = null;
                conversa.AtualizadoEm = DateTime.UtcNow;
            }
            return Task.CompletedTask;
        }

        private static bool TryMapEstado(string estado, out EstadoConversa resultado)
        {
            var normalized = estado.Trim().ToLowerInvariant();
            switch (normalized)
            {
                case "aberto":
                    resultado = EstadoConversa.Aberto;
                    return true;
                case "aguardando_atendimento":
                case "em_atendimento":
                    resultado = EstadoConversa.EmAtendimento;
                    return true;
                case "fechado_agente":
                    resultado = EstadoConversa.FechadoAgente;
                    return true;
                case "fechado_bot":
                case "fechado_automaticamente":
                    resultado = EstadoConversa.FechadoAutomaticamente;
                    return true;
                case "arquivado":
                case "arquivada":
                    resultado = EstadoConversa.Arquivada;
                    return true;
                default:
                    return Enum.TryParse(normalized, true, out resultado);
            }
        }

        private static string MapEstadoToString(EstadoConversa estado)
        {
            return estado switch
            {
                EstadoConversa.Aberto => "aberto",
                EstadoConversa.EmAtendimento => "em_atendimento",
                EstadoConversa.FechadoAgente => "fechado_agente",
                EstadoConversa.FechadoAutomaticamente => "fechado_bot",
                EstadoConversa.Arquivada => "arquivada",
                _ => estado.ToString().ToLowerInvariant()
            };
        }

        private static ConversationDetailsDto ToDetails(Conversation conversa)
        {
            return new ConversationDetailsDto
            {
                Id = conversa.IdConversa,
                IdCliente = conversa.IdCliente,
                ClienteNome = null,
                Estado = MapEstadoToString(conversa.Estado),
                IdAgenteAtribuido = conversa.AgenteDesignadoId,
                DataPrimeiraMensagem = conversa.CriadoEm,
                DataUltimaMensagem = conversa.AtualizadoEm,
                DataCriacao = conversa.CriadoEm,
                DataAtualizacao = conversa.AtualizadoEm ?? conversa.CriadoEm,
                DataFechamento = conversa.DataFechamento,
                FechadoPorId = conversa.FechadoPorId,
                MotivoFechamento = conversa.MotivoFechamento
            };
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================











