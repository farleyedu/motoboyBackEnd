// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using APIBack.Automation.Dtos;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Models;
using APIBack.Automation.Services;

namespace APIBack.Automation.Infra
{
    public class InMemoryConversationRepository : IConversationRepository
    {
        private readonly ConcurrentDictionary<Guid, Conversation> _conversas = new();
        private readonly ConcurrentDictionary<Guid, ConcurrentQueue<Message>> _mensagens = new();
        private readonly ConcurrentDictionary<Guid, ConcurrentQueue<ConversationEventDto>> _eventos = new();
        private readonly ConcurrentDictionary<string, byte> _idsMensagemWa = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<(Guid Estab, string Tel), Guid> _clientes = new();
        private readonly Guid? _centralEstabelecimentoId;

        public InMemoryConversationRepository(Guid? centralEstabelecimentoId = null)
        {
            _centralEstabelecimentoId = centralEstabelecimentoId;
        }

        public Task<Conversation?> ObterPorIdAsync(Guid id, Guid? idEstabelecimento = null)
        {
            if (!_conversas.TryGetValue(id, out var conversa))
            {
                return Task.FromResult<Conversation?>(null);
            }

            if (!idEstabelecimento.HasValue)
            {
                return Task.FromResult<Conversation?>(Clone(conversa));
            }

            var alvo = ResolveTarget(id, idEstabelecimento.Value);
            return Task.FromResult<Conversation?>(alvo == null ? null : Clone(alvo));
        }

        public Task<bool> InserirOuAtualizarAsync(Conversation conversa)
        {
            var clone = Clone(conversa);
            clone.IdConversaGrupo = GroupId(clone);
            clone.StatusAtendimento = CanonicalStatus(clone);
            clone.AtualizadoEm ??= DateTime.UtcNow;
            _conversas[clone.IdConversa] = clone;
            return Task.FromResult(true);
        }

        public Task DefinirModoAsync(Guid id, ModoConversa modo, int? agenteId)
        {
            _conversas.AddOrUpdate(
                id,
                _ => new Conversation
                {
                    IdConversa = id,
                    IdConversaGrupo = id,
                    Modo = modo,
                    AgenteDesignadoId = agenteId,
                    Estado = modo == ModoConversa.Humano ? EstadoConversa.EmAtendimento : EstadoConversa.Aberto,
                    StatusAtendimento = modo == ModoConversa.Humano ? "em_andamento" : "com_bot",
                    CriadoEm = DateTime.UtcNow,
                    AtualizadoEm = DateTime.UtcNow
                },
                (_, c) =>
                {
                    c.Modo = modo;
                    c.AgenteDesignadoId = agenteId;
                    c.Estado = modo == ModoConversa.Humano ? EstadoConversa.EmAtendimento : EstadoConversa.Aberto;
                    c.StatusAtendimento = modo == ModoConversa.Humano ? "em_andamento" : "com_bot";
                    c.AtualizadoEm = DateTime.UtcNow;
                    return c;
                });
            return Task.CompletedTask;
        }

        public Task AcrescentarMensagemAsync(Message mensagem, string? phoneNumberId, string? idWa = null)
        {
            if (!string.IsNullOrWhiteSpace(mensagem.IdMensagemWa))
            {
                _idsMensagemWa.TryAdd(mensagem.IdMensagemWa, 1);
            }

            var quando = MsgDate(mensagem);
            _conversas.AddOrUpdate(
                mensagem.IdConversa,
                _ => new Conversation
                {
                    IdConversa = mensagem.IdConversa,
                    IdConversaGrupo = mensagem.IdConversa,
                    IdWa = idWa ?? string.Empty,
                    TelefoneCliente = idWa,
                    UltimoUsuarioEm = mensagem.Direcao == DirecaoMensagem.Entrada ? quando : null,
                    Janela24hExpiraEm = mensagem.Direcao == DirecaoMensagem.Entrada ? quando.AddHours(24) : null,
                    CriadoEm = quando,
                    AtualizadoEm = quando,
                    StatusAtendimento = "com_bot"
                },
                (_, c) =>
                {
                    c.IdConversaGrupo = GroupId(c);
                    if (!string.IsNullOrWhiteSpace(idWa))
                    {
                        c.IdWa = idWa;
                        c.TelefoneCliente ??= idWa;
                    }

                    if (mensagem.Direcao == DirecaoMensagem.Entrada)
                    {
                        c.UltimoUsuarioEm = quando;
                        c.Janela24hExpiraEm = quando.AddHours(24);
                    }

                    c.AtualizadoEm = quando;
                    return c;
                });

            var conversa = _conversas[mensagem.IdConversa];
            if (mensagem.Direcao == DirecaoMensagem.Entrada)
            {
                conversa.DataUltimaLeitura ??= null;
            }

            _mensagens.GetOrAdd(mensagem.IdConversa, _ => new ConcurrentQueue<Message>()).Enqueue(Clone(mensagem));
            return Task.CompletedTask;
        }

        public Task<bool> ExisteIdMensagemPorProvedorWaAsync(string idMensagemWa)
            => Task.FromResult(_idsMensagemWa.ContainsKey(idMensagemWa));

        public Task<Guid> GarantirClienteAsync(string telefoneE164, Guid idEstabelecimento)
            => Task.FromResult(_clientes.GetOrAdd((idEstabelecimento, telefoneE164 ?? string.Empty), _ => Guid.NewGuid()));

        public Task<Guid> ObterIdConversaPorClienteAsync(Guid idCliente, Guid idEstabelecimento)
            => Task.FromResult(_conversas.Values.Where(c => c.IdCliente == idCliente && c.IdEstabelecimento == idEstabelecimento && IsOpen(c)).OrderByDescending(ConvDate).Select(c => c.IdConversa).FirstOrDefault());

        public Task<Guid> ObterIdConversaAbertaPorGrupoAsync(Guid idConversaGrupo, Guid idEstabelecimento)
            => Task.FromResult(_conversas.Values.Where(c => GroupId(c) == idConversaGrupo && c.IdEstabelecimento == idEstabelecimento && !c.EhRaizDoGrupo && IsOpen(c)).OrderByDescending(ConvDate).Select(c => c.IdConversa).FirstOrDefault());

        public Task AtualizarEstadoAsync(Guid idConversa, EstadoConversa novoEstado)
        {
            if (_conversas.TryGetValue(idConversa, out var conversa))
            {
                conversa.Estado = novoEstado;
                conversa.StatusAtendimento = LegacyToCanonical(novoEstado);
                conversa.AtualizadoEm = DateTime.UtcNow;
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ConversationListItemDto>> ListarConversasAsync(string? estado, int? idAgente, bool incluirArquivadas, Guid? idEstabelecimento = null)
        {
            IEnumerable<Conversation> conversas = _conversas.Values;
            if (idEstabelecimento.HasValue)
            {
                conversas = IsCentral(idEstabelecimento.Value)
                    ? conversas.Where(c => c.EhRaizDoGrupo && c.IdEstabelecimento == idEstabelecimento.Value)
                    : conversas.Where(c => c.IdEstabelecimento == idEstabelecimento.Value);
            }

            var itens = conversas.Select(c => IsCentral(idEstabelecimento ?? Guid.Empty) && c.EhRaizDoGrupo ? ToCentralListItem(c) : ToListItem(c)).ToList();
            if (!string.IsNullOrWhiteSpace(estado))
            {
                itens = itens.Where(i => string.Equals(i.Estado, NormalizeStatus(estado), StringComparison.OrdinalIgnoreCase)).ToList();
            }

            if (idAgente.HasValue)
            {
                itens = itens.Where(i => i.IdAgenteAtribuido == idAgente.Value).ToList();
            }

            if (!incluirArquivadas)
            {
                itens = itens.Where(i => !string.Equals(i.Estado, "arquivada", StringComparison.OrdinalIgnoreCase)).ToList();
            }

            return Task.FromResult<IReadOnlyList<ConversationListItemDto>>(itens.OrderByDescending(i => i.UltimaMensagemData ?? i.DataUltimaMensagem ?? i.DataAtualizacao).ToList());
        }

        public Task<ConversationHistoryDto?> ObterHistoricoConversaAsync(Guid idConversa, int page, int pageSize, Guid? idEstabelecimento = null)
        {
            var alvo = idEstabelecimento.HasValue ? ResolveTarget(idConversa, idEstabelecimento.Value) : ResolveExact(idConversa);
            if (alvo == null)
            {
                return Task.FromResult<ConversationHistoryDto?>(null);
            }

            page = Math.Max(1, page);
            pageSize = pageSize <= 0 ? 50 : pageSize;
            var mensagens = (IsCentral(idEstabelecimento ?? Guid.Empty) ? GroupMessages(alvo.IdConversaGrupo) : Messages(alvo.IdConversa))
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(ToItem)
                .ToList();

            var allMessages = IsCentral(idEstabelecimento ?? Guid.Empty) ? GroupMessages(alvo.IdConversaGrupo) : Messages(alvo.IdConversa);
            var detalhes = IsCentral(idEstabelecimento ?? Guid.Empty) ? ToCentralDetails(GetRoot(alvo) ?? alvo) : ToDetails(alvo);
            var controle = BuildControl(alvo, idEstabelecimento ?? alvo.IdEstabelecimento);

            return Task.FromResult<ConversationHistoryDto?>(new ConversationHistoryDto
            {
                Conversa = detalhes,
                Controle = controle,
                Eventos = BuildEvents(alvo.IdConversaGrupo),
                Mensagens = mensagens,
                Page = page,
                PageSize = pageSize,
                Total = allMessages.Count
            });
        }

        public Task<bool> AtribuirConversaAsync(Guid idConversa, int idAgente, Guid? idEstabelecimento = null)
        {
            var alvo = ResolveTarget(idConversa, idEstabelecimento);
            if (alvo == null)
            {
                return Task.FromResult(false);
            }

            alvo.AgenteDesignadoId = idAgente;
            alvo.Modo = ModoConversa.Humano;
            alvo.Estado = EstadoConversa.EmAtendimento;
            alvo.StatusAtendimento = "em_andamento";
            alvo.AtualizadoEm = DateTime.UtcNow;
            return Task.FromResult(true);
        }

        public Task<bool> FecharConversaAsync(Guid idConversa, int? idAgente, string? motivo, Guid? idEstabelecimento = null, string? tipoFechamento = null)
        {
            var alvo = ResolveTarget(idConversa, idEstabelecimento);
            if (alvo == null)
            {
                return Task.FromResult(false);
            }

            var closeType = string.Equals(tipoFechamento, "inatividade", StringComparison.OrdinalIgnoreCase) ? "inatividade" : "manual";
            alvo.Estado = closeType == "inatividade" ? EstadoConversa.FechadoAutomaticamente : EstadoConversa.FechadoAgente;
            alvo.StatusAtendimento = closeType == "inatividade" ? "encerrada_inatividade" : "encerrada_manual";
            alvo.MotivoFechamento = string.IsNullOrWhiteSpace(motivo) ? null : motivo.Trim();
            alvo.FechadoPorId = idAgente;
            alvo.DataFechamento = DateTime.UtcNow;
            alvo.AtualizadoEm = DateTime.UtcNow;
            return Task.FromResult(true);
        }

        public Task<ConversationDetailsDto?> ArquivarConversaAsync(Guid idConversa, Guid? idEstabelecimento = null)
        {
            var alvo = ResolveTarget(idConversa, idEstabelecimento);
            if (alvo == null)
            {
                return Task.FromResult<ConversationDetailsDto?>(null);
            }

            alvo.Estado = EstadoConversa.Arquivada;
            alvo.StatusAtendimento = "encerrada_manual";
            alvo.AtualizadoEm = DateTime.UtcNow;
            return Task.FromResult<ConversationDetailsDto?>(ToDetails(alvo));
        }

        public Task<ConversationDetailsDto?> ObterDetalhesConversaAsync(Guid idConversa, Guid? idEstabelecimento = null)
        {
            var alvo = idEstabelecimento.HasValue ? ResolveTarget(idConversa, idEstabelecimento.Value) : ResolveExact(idConversa);
            if (alvo == null)
            {
                return Task.FromResult<ConversationDetailsDto?>(null);
            }

            return Task.FromResult<ConversationDetailsDto?>(IsCentral(idEstabelecimento ?? Guid.Empty) ? ToCentralDetails(GetRoot(alvo) ?? alvo) : ToDetails(alvo));
        }

        public Task<bool> AtualizarStatusAtendimentoAsync(Guid idConversa, string status, int? idAgente, string? agenteNome, Guid? idEstabelecimento = null)
        {
            var alvo = ResolveTarget(idConversa, idEstabelecimento);
            if (alvo == null)
            {
                return Task.FromResult(false);
            }

            var normalized = NormalizeStatus(status);
            alvo.StatusAtendimento = normalized;
            alvo.Estado = normalized switch
            {
                "encerrada_manual" => EstadoConversa.FechadoAgente,
                "encerrada_inatividade" => EstadoConversa.FechadoAutomaticamente,
                "com_bot" => EstadoConversa.Aberto,
                _ => EstadoConversa.EmAtendimento
            };
            alvo.Modo = normalized == "com_bot" ? ModoConversa.Bot : ModoConversa.Humano;
            alvo.AgenteDesignadoId = normalized == "com_bot" ? null : (idAgente ?? alvo.AgenteDesignadoId);
            alvo.AtualizadoEm = DateTime.UtcNow;
            return Task.FromResult(true);
        }

        public Task<bool> VoltarParaBotAsync(Guid idConversa, Guid? idEstabelecimento = null)
        {
            var alvo = ResolveTarget(idConversa, idEstabelecimento);
            if (alvo == null)
            {
                return Task.FromResult(false);
            }

            alvo.Modo = ModoConversa.Bot;
            alvo.AgenteDesignadoId = null;
            alvo.Estado = EstadoConversa.Aberto;
            alvo.StatusAtendimento = "com_bot";
            alvo.DataFechamento = null;
            alvo.MotivoFechamento = null;
            alvo.FechadoPorId = null;
            alvo.AtualizadoEm = DateTime.UtcNow;
            return Task.FromResult(true);
        }

        public Task<bool> ReabrirConversaAsync(Guid idConversa, Guid? idEstabelecimento = null)
            => VoltarParaBotAsync(idConversa, idEstabelecimento);

        public Task<bool> MarcarComoLidaAsync(Guid idConversa, Guid? idEstabelecimento = null)
        {
            var alvo = idEstabelecimento.HasValue ? ResolveTarget(idConversa, idEstabelecimento.Value) : ResolveExact(idConversa);
            if (alvo == null)
            {
                return Task.FromResult(false);
            }

            foreach (var conversa in _conversas.Values.Where(c => GroupId(c) == alvo.IdConversaGrupo))
            {
                conversa.DataUltimaLeitura = DateTime.UtcNow;
            }

            return Task.FromResult(true);
        }

        public Task<ConversationControlDto?> ObterControleConversaAsync(Guid idConversa, Guid? idEstabelecimento = null)
        {
            var alvo = idEstabelecimento.HasValue ? ResolveTarget(idConversa, idEstabelecimento.Value) : ResolveExact(idConversa);
            return Task.FromResult<ConversationControlDto?>(alvo == null ? null : BuildControl(alvo, idEstabelecimento ?? alvo.IdEstabelecimento));
        }

        public Task RegistrarEventoAsync(Guid idConversa, ConversationEventDto evento, Guid? idEstabelecimento = null)
        {
            var alvo = idEstabelecimento.HasValue ? ResolveTarget(idConversa, idEstabelecimento.Value) : ResolveExact(idConversa);
            if (alvo != null)
            {
                _eventos.GetOrAdd(alvo.IdConversaGrupo, _ => new ConcurrentQueue<ConversationEventDto>()).Enqueue(Clone(evento));
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ConversationEventDto>> ListarEventosConversaAsync(Guid idConversa, Guid? idEstabelecimento = null)
        {
            var alvo = idEstabelecimento.HasValue ? ResolveTarget(idConversa, idEstabelecimento.Value) : ResolveExact(idConversa);
            return Task.FromResult<IReadOnlyList<ConversationEventDto>>(alvo == null ? Array.Empty<ConversationEventDto>() : BuildEvents(alvo.IdConversaGrupo));
        }

        public Task<IReadOnlyList<ConversationAgentDto>> ListarAgentesAsync(Guid? idEstabelecimento = null)
            => Task.FromResult<IReadOnlyList<ConversationAgentDto>>(Array.Empty<ConversationAgentDto>());

        public Task SalvarContextoAsync(Guid idConversa, ConversationContext contexto)
        {
            if (_conversas.TryGetValue(idConversa, out var conversa))
            {
                var atual = Deserialize(conversa.ContextoEstadoJson);
                conversa.ContextoEstadoJson = JsonSerializer.Serialize(CentralRoutingService.MergeCentralSelection(atual, contexto));
                conversa.AtualizadoEm = DateTime.UtcNow;
            }
            return Task.CompletedTask;
        }

        public Task<ConversationContext?> ObterContextoAsync(Guid idConversa)
            => Task.FromResult(_conversas.TryGetValue(idConversa, out var conversa) ? Deserialize(conversa.ContextoEstadoJson) : null);

        public Task LimparContextoAsync(Guid idConversa)
        {
            if (_conversas.TryGetValue(idConversa, out var conversa))
            {
                var preservado = CentralRoutingService.BuildPreservedSelectionContext(Deserialize(conversa.ContextoEstadoJson));
                conversa.ContextoEstadoJson = preservado == null ? null : JsonSerializer.Serialize(preservado);
                conversa.AtualizadoEm = DateTime.UtcNow;
            }

            return Task.CompletedTask;
        }

        private Conversation? ResolveExact(Guid id)
            => _conversas.TryGetValue(id, out var conversa) ? conversa : null;

        private Conversation? ResolveTarget(Guid id, Guid? idEstabelecimento)
        {
            var exata = ResolveExact(id);
            if (exata == null)
            {
                return null;
            }

            if (!idEstabelecimento.HasValue)
            {
                return exata;
            }

            if (IsCentral(idEstabelecimento.Value))
            {
                var raiz = GetRoot(exata);
                if (raiz == null || raiz.IdEstabelecimento != idEstabelecimento.Value)
                {
                    return null;
                }

                return exata.EhRaizDoGrupo ? ResolveOperational(raiz.IdConversa) ?? exata : exata;
            }

            return exata.IdEstabelecimento == idEstabelecimento.Value ? exata : null;
        }

        private Conversation? GetRoot(Conversation conversa)
            => _conversas.TryGetValue(GroupId(conversa), out var root) ? root : null;

        private Conversation? ResolveOperational(Guid groupId)
            => _conversas.Values
                .Where(c => GroupId(c) == groupId)
                .OrderBy(c => c.EhRaizDoGrupo ? 1 : 0)
                .ThenBy(c => IsOpen(c) ? 0 : 1)
                .ThenByDescending(ConvDate)
                .FirstOrDefault();

        private List<Message> Messages(Guid idConversa)
            => _mensagens.TryGetValue(idConversa, out var q) ? q.ToArray().Select(Clone).OrderBy(MsgDate).ToList() : new List<Message>();

        private List<Message> GroupMessages(Guid groupId)
            => _conversas.Values.Where(c => GroupId(c) == groupId).SelectMany(c => Messages(c.IdConversa)).OrderBy(MsgDate).ToList();

        private IReadOnlyList<ConversationEventDto> BuildEvents(Guid groupId)
        {
            var baseEvents = _conversas.Values
                .Where(c => GroupId(c) == groupId)
                .OrderBy(c => c.CriadoEm)
                .Select(c => new ConversationEventDto
                {
                    Id = DeterministicGuid($"started:{c.IdConversa}:{c.CriadoEm:O}"),
                    Type = "started",
                    At = c.CriadoEm,
                    ToStatus = CanonicalStatus(c),
                    Source = "derived"
                })
                .ToList();

            if (_eventos.TryGetValue(groupId, out var queue))
            {
                baseEvents.AddRange(queue.ToArray().Select(Clone));
            }

            return baseEvents.OrderBy(e => e.At).ToList();
        }

        private ConversationListItemDto ToListItem(Conversation c)
        {
            var ultima = Messages(c.IdConversa).LastOrDefault();
            return new ConversationListItemDto
            {
                Id = c.IdConversa,
                IdCliente = c.IdCliente,
                IdConversaOperacional = c.IdConversa,
                IdConversaGrupo = GroupId(c),
                ClienteNome = c.TelefoneCliente,
                ClienteNumero = c.TelefoneCliente,
                Estado = CanonicalStatus(c),
                IdAgenteAtribuido = c.AgenteDesignadoId,
                QtdNaoLidas = 0,
                DataPrimeiraMensagem = c.CriadoEm,
                DataUltimaMensagem = c.AtualizadoEm,
                DataCriacao = c.CriadoEm,
                DataAtualizacao = c.AtualizadoEm ?? c.CriadoEm,
                DataFechamento = c.DataFechamento,
                UltimaMensagemConteudo = ultima?.Conteudo,
                UltimaMensagemData = ultima == null ? null : MsgDate(ultima),
                UltimaMensagemCriadaPor = ultima?.CriadaPor
            };
        }

        private ConversationListItemDto ToCentralListItem(Conversation root)
        {
            var op = ResolveOperational(root.IdConversa) ?? root;
            var msgs = GroupMessages(root.IdConversa);
            var ultima = msgs.LastOrDefault();
            return new ConversationListItemDto
            {
                Id = root.IdConversa,
                IdCliente = root.IdCliente,
                IdConversaOperacional = op.IdConversa,
                IdConversaGrupo = GroupId(root),
                ClienteNome = root.TelefoneCliente ?? op.TelefoneCliente,
                ClienteNumero = root.TelefoneCliente ?? op.TelefoneCliente,
                Estado = CanonicalStatus(op),
                IdAgenteAtribuido = op.AgenteDesignadoId,
                QtdNaoLidas = 0,
                DataPrimeiraMensagem = msgs.FirstOrDefault() == null ? root.CriadoEm : MsgDate(msgs.First()),
                DataUltimaMensagem = msgs.LastOrDefault() == null ? root.AtualizadoEm : MsgDate(msgs.Last()),
                DataCriacao = root.CriadoEm,
                DataAtualizacao = op.AtualizadoEm ?? op.CriadoEm,
                DataFechamento = op.DataFechamento,
                UltimaMensagemConteudo = ultima?.Conteudo,
                UltimaMensagemData = ultima == null ? null : MsgDate(ultima),
                UltimaMensagemCriadaPor = ultima?.CriadaPor
            };
        }

        private ConversationDetailsDto ToDetails(Conversation c)
            => new()
            {
                Id = c.IdConversa,
                IdCliente = c.IdCliente,
                IdConversaOperacional = c.IdConversa,
                IdConversaGrupo = GroupId(c),
                ClienteNome = c.TelefoneCliente,
                ClienteNumero = c.TelefoneCliente,
                Estado = CanonicalStatus(c),
                IdAgenteAtribuido = c.AgenteDesignadoId,
                QtdNaoLidas = 0,
                DataPrimeiraMensagem = c.CriadoEm,
                DataUltimaMensagem = c.AtualizadoEm,
                DataCriacao = c.CriadoEm,
                DataAtualizacao = c.AtualizadoEm ?? c.CriadoEm,
                DataFechamento = c.DataFechamento,
                FechadoPorId = c.FechadoPorId,
                MotivoFechamento = c.MotivoFechamento
            };

        private ConversationDetailsDto ToCentralDetails(Conversation root)
        {
            var op = ResolveOperational(root.IdConversa) ?? root;
            var detalhes = ToDetails(root);
            detalhes.IdConversaOperacional = op.IdConversa;
            detalhes.Estado = CanonicalStatus(op);
            detalhes.IdAgenteAtribuido = op.AgenteDesignadoId;
            detalhes.DataFechamento = op.DataFechamento;
            detalhes.FechadoPorId = op.FechadoPorId;
            detalhes.MotivoFechamento = op.MotivoFechamento;
            return detalhes;
        }

        private ConversationControlDto BuildControl(Conversation c, Guid idEstabelecimento)
            => new()
            {
                ConversationId = c.IdConversa,
                ClientId = c.IdCliente,
                ConversationGroupId = GroupId(c),
                Status = CanonicalStatus(c),
                CanBotReply = CanonicalStatus(c) == "com_bot",
                AssignedAgentId = c.AgenteDesignadoId,
                LastInteractionAt = c.AtualizadoEm,
                AutoCloseAt = c.Janela24hExpiraEm,
                UnreadCount = 0
            };

        private static ConversationMessageItemDto ToItem(Message m)
            => new()
            {
                Id = m.Id,
                CriadaPor = m.CriadaPor ?? string.Empty,
                Conteudo = m.Conteudo ?? string.Empty,
                Tipo = string.IsNullOrWhiteSpace(m.Tipo) ? "texto" : m.Tipo!,
                Status = m.Status ?? string.Empty,
                DataEnvio = m.DataEnvio,
                DataCriacao = MsgDate(m)
            };

        private bool IsCentral(Guid idEstabelecimento)
            => _centralEstabelecimentoId.HasValue && _centralEstabelecimentoId.Value == idEstabelecimento;

        private static bool IsOpen(Conversation c)
            => c.Estado != EstadoConversa.FechadoAutomaticamente && c.Estado != EstadoConversa.FechadoAgente && c.Estado != EstadoConversa.Arquivada;

        private static Guid GroupId(Conversation c)
            => c.IdConversaGrupo == Guid.Empty ? c.IdConversa : c.IdConversaGrupo;

        private static DateTime ConvDate(Conversation c)
            => c.AtualizadoEm ?? c.CriadoEm;

        private static DateTime MsgDate(Message m)
            => m.DataCriacao ?? m.DataEnvio ?? m.DataHora;

        private static string CanonicalStatus(Conversation c)
            => NormalizeStatus(c.StatusAtendimento) switch
            {
                "" => LegacyToCanonical(c.Estado),
                var status => status
            };

        private static string LegacyToCanonical(EstadoConversa estado)
            => estado switch
            {
                EstadoConversa.EmAtendimento => "em_andamento",
                EstadoConversa.FechadoAgente => "encerrada_manual",
                EstadoConversa.FechadoAutomaticamente => "encerrada_inatividade",
                EstadoConversa.Arquivada => "encerrada_manual",
                _ => "com_bot"
            };

        private static string NormalizeStatus(string? status)
            => (status ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "aberto" => "com_bot",
                "novo_bot" => "com_bot",
                "pendente" => "com_bot",
                "em_atendimento" => "em_andamento",
                "em_atendimento_humano" => "em_andamento",
                "fechado_bot" => "encerrada_inatividade",
                "fechado_agente" => "encerrada_manual",
                _ => (status ?? string.Empty).Trim().ToLowerInvariant()
            };

        private static ConversationContext? Deserialize(string? json)
        {
            try
            {
                return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<ConversationContext>(json);
            }
            catch
            {
                return null;
            }
        }

        private static Guid DeterministicGuid(string seed)
        {
            using var sha = System.Security.Cryptography.MD5.Create();
            return new Guid(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(seed)));
        }

        private static Conversation Clone(Conversation c)
            => new()
            {
                IdConversa = c.IdConversa,
                IdConversaGrupo = GroupId(c),
                IdEstabelecimento = c.IdEstabelecimento,
                IdCliente = c.IdCliente,
                TelefoneCliente = c.TelefoneCliente,
                IdWa = c.IdWa,
                Modo = c.Modo,
                AgenteDesignadoId = c.AgenteDesignadoId,
                UltimoUsuarioEm = c.UltimoUsuarioEm,
                Janela24hExpiraEm = c.Janela24hExpiraEm,
                CriadoEm = c.CriadoEm,
                AtualizadoEm = c.AtualizadoEm,
                MessageIdWhatsapp = c.MessageIdWhatsapp,
                Estado = c.Estado,
                MotivoFechamento = c.MotivoFechamento,
                FechadoPorId = c.FechadoPorId,
                DataFechamento = c.DataFechamento,
                ContextoEstadoJson = c.ContextoEstadoJson,
                StatusAtendimento = c.StatusAtendimento,
                DataUltimaLeitura = c.DataUltimaLeitura
            };

        private static Message Clone(Message m)
            => new()
            {
                Id = m.Id,
                IdConversa = m.IdConversa,
                IdMensagemWa = m.IdMensagemWa,
                Direcao = m.Direcao,
                Tipo = m.Tipo,
                TipoOriginal = m.TipoOriginal,
                Status = m.Status,
                IdProvedor = m.IdProvedor,
                CodigoErro = m.CodigoErro,
                MensagemErro = m.MensagemErro,
                Tentativas = m.Tentativas,
                CriadaPor = m.CriadaPor,
                DataHora = m.DataHora,
                DataEnvio = m.DataEnvio,
                DataEntrega = m.DataEntrega,
                DataLeitura = m.DataLeitura,
                DataCriacao = m.DataCriacao,
                Conteudo = m.Conteudo
            };

        private static ConversationEventDto Clone(ConversationEventDto e)
            => new()
            {
                Id = e.Id,
                Type = e.Type,
                At = e.At,
                ActorName = e.ActorName,
                FromStatus = e.FromStatus,
                ToStatus = e.ToStatus,
                Reason = e.Reason,
                CloseType = e.CloseType,
                Source = e.Source,
                ActorUserId = e.ActorUserId,
                ActorAgentId = e.ActorAgentId,
                Data = e.Data == null ? null : new Dictionary<string, object?>(e.Data)
            };
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
