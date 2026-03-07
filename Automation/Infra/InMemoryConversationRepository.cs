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
        private readonly ConcurrentDictionary<string, byte> _idsMensagemWa = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<(Guid Estab, string Tel), Guid> _clientes = new();
        private readonly Guid? _centralEstabelecimentoId;

        public InMemoryConversationRepository(Guid? centralEstabelecimentoId = null) => _centralEstabelecimentoId = centralEstabelecimentoId;

        public Task<Conversation?> ObterPorIdAsync(Guid id, Guid? idEstabelecimento = null)
        {
            if (!_conversas.TryGetValue(id, out var conversa)) return Task.FromResult<Conversation?>(null);
            if (!idEstabelecimento.HasValue) return Task.FromResult<Conversation?>(Clone(conversa));
            if (IsCentral(idEstabelecimento.Value))
            {
                var raiz = GetRoot(conversa);
                if (raiz == null || raiz.IdEstabelecimento != idEstabelecimento.Value) return Task.FromResult<Conversation?>(null);
                return Task.FromResult<Conversation?>(Clone(conversa.EhRaizDoGrupo ? ResolveOperational(raiz.IdConversa) ?? raiz : conversa));
            }
            return Task.FromResult<Conversation?>(conversa.IdEstabelecimento == idEstabelecimento.Value ? Clone(conversa) : null);
        }

        public Task<bool> InserirOuAtualizarAsync(Conversation conversa)
        {
            var clone = Clone(conversa);
            clone.IdConversaGrupo = GroupId(clone);
            clone.AtualizadoEm ??= DateTime.UtcNow;
            _conversas[clone.IdConversa] = clone;
            return Task.FromResult(true);
        }

        public Task DefinirModoAsync(Guid id, ModoConversa modo, int? agenteId)
        {
            _conversas.AddOrUpdate(id,
                _ => new Conversation { IdConversa = id, IdConversaGrupo = id, Modo = modo, AgenteDesignadoId = agenteId, Estado = modo == ModoConversa.Humano ? EstadoConversa.EmAtendimento : EstadoConversa.Aberto, CriadoEm = DateTime.UtcNow, AtualizadoEm = DateTime.UtcNow },
                (_, c) => { c.Modo = modo; c.AgenteDesignadoId = agenteId; c.Estado = modo == ModoConversa.Humano ? EstadoConversa.EmAtendimento : EstadoConversa.Aberto; c.AtualizadoEm = DateTime.UtcNow; return c; });
            return Task.CompletedTask;
        }

        public Task AcrescentarMensagemAsync(Message mensagem, string? phoneNumberId, string? idWa = null)
        {
            if (!string.IsNullOrWhiteSpace(mensagem.IdMensagemWa)) _idsMensagemWa.TryAdd(mensagem.IdMensagemWa, 1);
            var quando = MsgDate(mensagem);
            _conversas.AddOrUpdate(mensagem.IdConversa,
                _ => new Conversation { IdConversa = mensagem.IdConversa, IdConversaGrupo = mensagem.IdConversa, IdWa = idWa ?? string.Empty, UltimoUsuarioEm = mensagem.Direcao == DirecaoMensagem.Entrada ? quando : default, Janela24hExpiraEm = mensagem.Direcao == DirecaoMensagem.Entrada ? quando.AddHours(24) : null, CriadoEm = DateTime.UtcNow, AtualizadoEm = DateTime.UtcNow },
                (_, c) => { c.IdConversaGrupo = GroupId(c); if (!string.IsNullOrWhiteSpace(idWa)) c.IdWa = idWa; if (mensagem.Direcao == DirecaoMensagem.Entrada) { c.UltimoUsuarioEm = quando; c.Janela24hExpiraEm = quando.AddHours(24); } c.AtualizadoEm = DateTime.UtcNow; return c; });
            _mensagens.GetOrAdd(mensagem.IdConversa, _ => new ConcurrentQueue<Message>()).Enqueue(Clone(mensagem));
            return Task.CompletedTask;
        }

        public Task<bool> ExisteIdMensagemPorProvedorWaAsync(string idMensagemWa) => Task.FromResult(_idsMensagemWa.ContainsKey(idMensagemWa));

        public Task<Guid> GarantirClienteAsync(string telefoneE164, Guid idEstabelecimento)
        {
            if (idEstabelecimento == Guid.Empty) throw new ArgumentException("idEstabelecimento obrigatorio", nameof(idEstabelecimento));
            return Task.FromResult(_clientes.GetOrAdd((idEstabelecimento, telefoneE164 ?? string.Empty), _ => Guid.NewGuid()));
        }

        public Task<Guid> ObterIdConversaPorClienteAsync(Guid idCliente, Guid idEstabelecimento)
            => Task.FromResult(_conversas.Values.Where(c => c.IdCliente == idCliente && c.IdEstabelecimento == idEstabelecimento && IsOpen(c)).OrderByDescending(ConvDate).Select(c => c.IdConversa).FirstOrDefault());

        public Task<Guid> ObterIdConversaAbertaPorGrupoAsync(Guid idConversaGrupo, Guid idEstabelecimento)
            => Task.FromResult(_conversas.Values.Where(c => GroupId(c) == idConversaGrupo && c.IdEstabelecimento == idEstabelecimento && !c.EhRaizDoGrupo && IsOpen(c)).OrderByDescending(ConvDate).Select(c => c.IdConversa).FirstOrDefault());

        public Task AtualizarEstadoAsync(Guid idConversa, EstadoConversa novoEstado)
        {
            _conversas.AddOrUpdate(idConversa,
                _ => new Conversation { IdConversa = idConversa, IdConversaGrupo = idConversa, Estado = novoEstado, CriadoEm = DateTime.UtcNow, AtualizadoEm = DateTime.UtcNow },
                (_, c) => { var fechada = !IsOpen(c); var reabrindo = novoEstado == EstadoConversa.Aberto || novoEstado == EstadoConversa.EmAtendimento; if (fechada && reabrindo) return c; c.Estado = novoEstado; c.AtualizadoEm = DateTime.UtcNow; if (novoEstado == EstadoConversa.FechadoAgente || novoEstado == EstadoConversa.FechadoAutomaticamente) c.DataFechamento = DateTime.UtcNow; else if (reabrindo) { c.DataFechamento = null; c.FechadoPorId = null; c.MotivoFechamento = null; } return c; });
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ConversationListItemDto>> ListarConversasAsync(string? estado, int? idAgente, bool incluirArquivadas, Guid? idEstabelecimento = null)
        {
            var lista = idEstabelecimento.HasValue && IsCentral(idEstabelecimento.Value)
                ? _conversas.Values.Where(c => c.EhRaizDoGrupo && c.IdEstabelecimento == idEstabelecimento.Value).Select(CentralListItem).Where(i => i != null).Cast<ConversationListItemDto>()
                : _conversas.Values.Where(c => !idEstabelecimento.HasValue || c.IdEstabelecimento == idEstabelecimento.Value).Select(StandardListItem);
            if (!string.IsNullOrWhiteSpace(estado)) { var filtro = NormalizeEstado(estado); lista = lista.Where(i => NormalizeEstado(i.Estado) == filtro); }
            if (idAgente.HasValue) lista = lista.Where(i => i.IdAgenteAtribuido == idAgente.Value);
            if (!incluirArquivadas) lista = lista.Where(i => !string.Equals(i.Estado, "arquivada", StringComparison.OrdinalIgnoreCase));
            return Task.FromResult<IReadOnlyList<ConversationListItemDto>>(lista.OrderByDescending(i => i.UltimaMensagemData ?? i.DataUltimaMensagem ?? i.DataAtualizacao).ToList());
        }

        public Task<ConversationHistoryDto?> ObterHistoricoConversaAsync(Guid idConversa, int page, int pageSize, Guid? idEstabelecimento = null)
        {
            if (!_conversas.TryGetValue(idConversa, out var conversa)) return Task.FromResult<ConversationHistoryDto?>(null);
            page = Math.Max(1, page); pageSize = pageSize <= 0 ? 50 : pageSize;
            if (idEstabelecimento.HasValue && IsCentral(idEstabelecimento.Value))
            {
                var raiz = GetRoot(conversa);
                if (raiz == null || raiz.IdEstabelecimento != idEstabelecimento.Value) return Task.FromResult<ConversationHistoryDto?>(null);
                var grupo = GroupMessages(raiz.IdConversa); var skip = (page - 1) * pageSize;
                return Task.FromResult<ConversationHistoryDto?>(new ConversationHistoryDto { Conversa = CentralDetails(raiz), Mensagens = grupo.Skip(skip).Take(pageSize).Select(ToItem).ToList(), Page = page, PageSize = pageSize, Total = grupo.Count });
            }
            if (idEstabelecimento.HasValue && conversa.IdEstabelecimento != idEstabelecimento.Value) return Task.FromResult<ConversationHistoryDto?>(null);
            var msgs = Messages(conversa.IdConversa); var offset = (page - 1) * pageSize;
            return Task.FromResult<ConversationHistoryDto?>(new ConversationHistoryDto { Conversa = Details(conversa), Mensagens = msgs.Skip(offset).Take(pageSize).Select(ToItem).ToList(), Page = page, PageSize = pageSize, Total = msgs.Count });
        }

        public Task<bool> AtribuirConversaAsync(Guid idConversa, int idAgente, Guid? idEstabelecimento = null)
        {
            var alvo = OperationTarget(idConversa, idEstabelecimento); if (alvo == null) return Task.FromResult(false);
            alvo.AgenteDesignadoId = idAgente; alvo.Modo = ModoConversa.Humano; alvo.Estado = EstadoConversa.EmAtendimento; alvo.AtualizadoEm = DateTime.UtcNow; return Task.FromResult(true);
        }

        public Task<bool> FecharConversaAsync(Guid idConversa, int? idAgente, string? motivo, Guid? idEstabelecimento = null)
        {
            var alvo = OperationTarget(idConversa, idEstabelecimento); if (alvo == null) return Task.FromResult(false);
            alvo.Estado = idAgente.HasValue ? EstadoConversa.FechadoAgente : EstadoConversa.FechadoAutomaticamente; alvo.Modo = ModoConversa.Bot; alvo.MotivoFechamento = string.IsNullOrWhiteSpace(motivo) ? null : motivo.Trim(); alvo.FechadoPorId = idAgente; alvo.DataFechamento = DateTime.UtcNow; alvo.AtualizadoEm = DateTime.UtcNow; return Task.FromResult(true);
        }

        public Task<ConversationDetailsDto?> ArquivarConversaAsync(Guid idConversa, Guid? idEstabelecimento = null)
        {
            var alvo = OperationTarget(idConversa, idEstabelecimento); if (alvo == null) return Task.FromResult<ConversationDetailsDto?>(null);
            alvo.Estado = EstadoConversa.Arquivada; alvo.DataFechamento ??= DateTime.UtcNow; alvo.AtualizadoEm = DateTime.UtcNow;
            return Task.FromResult<ConversationDetailsDto?>(idEstabelecimento.HasValue && IsCentral(idEstabelecimento.Value) ? CentralDetails(GetRoot(alvo) ?? alvo) : Details(alvo));
        }

        public Task<ConversationDetailsDto?> ObterDetalhesConversaAsync(Guid idConversa, Guid? idEstabelecimento = null)
        {
            if (!_conversas.TryGetValue(idConversa, out var conversa)) return Task.FromResult<ConversationDetailsDto?>(null);
            if (idEstabelecimento.HasValue && IsCentral(idEstabelecimento.Value))
            {
                var raiz = GetRoot(conversa);
                return Task.FromResult<ConversationDetailsDto?>(raiz != null && raiz.IdEstabelecimento == idEstabelecimento.Value ? CentralDetails(raiz) : null);
            }
            return Task.FromResult<ConversationDetailsDto?>(!idEstabelecimento.HasValue || conversa.IdEstabelecimento == idEstabelecimento.Value ? Details(conversa) : null);
        }

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

        public Task<ConversationContext?> ObterContextoAsync(Guid idConversa) => Task.FromResult(_conversas.TryGetValue(idConversa, out var c) ? Deserialize(c.ContextoEstadoJson) : null);

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

        private Conversation? OperationTarget(Guid idConversa, Guid? idEstabelecimento)
        {
            if (!_conversas.TryGetValue(idConversa, out var conversa)) return null;
            if (!idEstabelecimento.HasValue) return conversa;
            if (IsCentral(idEstabelecimento.Value))
            {
                var raiz = GetRoot(conversa);
                if (raiz == null || raiz.IdEstabelecimento != idEstabelecimento.Value) return null;
                return conversa.EhRaizDoGrupo ? ResolveOperational(raiz.IdConversa) ?? conversa : conversa;
            }
            return conversa.IdEstabelecimento == idEstabelecimento.Value ? conversa : null;
        }

        private Conversation? GetRoot(Conversation conversa) => _conversas.TryGetValue(GroupId(conversa), out var raiz) ? raiz : null;
        private Conversation? ResolveOperational(Guid grupoId) => _conversas.Values.Where(c => GroupId(c) == grupoId).OrderBy(c => c.EhRaizDoGrupo ? 1 : 0).ThenBy(c => IsOpen(c) ? 0 : 1).ThenByDescending(ConvDate).FirstOrDefault();
        private List<Message> Messages(Guid idConversa) => _mensagens.TryGetValue(idConversa, out var q) ? q.ToArray().Select(Clone).OrderBy(MsgDate).ToList() : new List<Message>();
        private List<Message> GroupMessages(Guid grupoId) => _conversas.Values.Where(c => GroupId(c) == grupoId).SelectMany(c => Messages(c.IdConversa)).OrderBy(MsgDate).ToList();

        private ConversationListItemDto StandardListItem(Conversation c)
        {
            var ultima = Messages(c.IdConversa).LastOrDefault();
            return new ConversationListItemDto { Id = c.IdConversa, IdCliente = c.IdCliente, ClienteNumero = c.TelefoneCliente, Estado = Estado(c.Estado), IdAgenteAtribuido = c.AgenteDesignadoId, DataPrimeiraMensagem = c.CriadoEm, DataUltimaMensagem = c.AtualizadoEm, DataCriacao = c.CriadoEm, DataAtualizacao = c.AtualizadoEm ?? c.CriadoEm, DataFechamento = c.DataFechamento, UltimaMensagemConteudo = ultima?.Conteudo, UltimaMensagemData = ultima == null ? null : MsgDate(ultima), UltimaMensagemCriadaPor = ultima?.CriadaPor };
        }

        private ConversationListItemDto? CentralListItem(Conversation raiz)
        {
            var op = ResolveOperational(raiz.IdConversa) ?? raiz; var msgs = GroupMessages(raiz.IdConversa); var ultima = msgs.LastOrDefault();
            return new ConversationListItemDto { Id = raiz.IdConversa, IdCliente = raiz.IdCliente, ClienteNumero = raiz.TelefoneCliente ?? op.TelefoneCliente, Estado = Estado(op.Estado), IdAgenteAtribuido = op.AgenteDesignadoId, DataPrimeiraMensagem = GroupCreated(raiz.IdConversa), DataUltimaMensagem = GroupUpdated(raiz.IdConversa), DataCriacao = raiz.CriadoEm, DataAtualizacao = GroupUpdated(raiz.IdConversa) ?? raiz.CriadoEm, DataFechamento = op.DataFechamento, UltimaMensagemConteudo = ultima?.Conteudo, UltimaMensagemData = ultima == null ? null : MsgDate(ultima), UltimaMensagemCriadaPor = ultima?.CriadaPor };
        }

        private ConversationDetailsDto Details(Conversation c) => new ConversationDetailsDto { Id = c.IdConversa, IdCliente = c.IdCliente, ClienteNumero = c.TelefoneCliente, Estado = Estado(c.Estado), IdAgenteAtribuido = c.AgenteDesignadoId, DataPrimeiraMensagem = c.CriadoEm, DataUltimaMensagem = c.AtualizadoEm, DataCriacao = c.CriadoEm, DataAtualizacao = c.AtualizadoEm ?? c.CriadoEm, DataFechamento = c.DataFechamento, FechadoPorId = c.FechadoPorId, MotivoFechamento = c.MotivoFechamento };
        private ConversationDetailsDto CentralDetails(Conversation raiz) { var op = ResolveOperational(raiz.IdConversa) ?? raiz; return new ConversationDetailsDto { Id = raiz.IdConversa, IdCliente = raiz.IdCliente, ClienteNumero = raiz.TelefoneCliente ?? op.TelefoneCliente, Estado = Estado(op.Estado), IdAgenteAtribuido = op.AgenteDesignadoId, DataPrimeiraMensagem = GroupCreated(raiz.IdConversa), DataUltimaMensagem = GroupUpdated(raiz.IdConversa), DataCriacao = raiz.CriadoEm, DataAtualizacao = GroupUpdated(raiz.IdConversa) ?? raiz.CriadoEm, DataFechamento = op.DataFechamento, FechadoPorId = op.FechadoPorId, MotivoFechamento = op.MotivoFechamento }; }
        private static ConversationMessageItemDto ToItem(Message m) => new ConversationMessageItemDto { Id = m.Id, CriadaPor = m.CriadaPor ?? string.Empty, Conteudo = m.Conteudo ?? string.Empty, DataEnvio = m.DataEnvio, DataCriacao = MsgDate(m) };

        private DateTime? GroupCreated(Guid grupoId) => GroupMessages(grupoId).FirstOrDefault() is { } m ? MsgDate(m) : _conversas.Values.Where(c => GroupId(c) == grupoId).Select(c => c.CriadoEm).DefaultIfEmpty(DateTime.MinValue).Min();
        private DateTime? GroupUpdated(Guid grupoId) => GroupMessages(grupoId).LastOrDefault() is { } m ? MsgDate(m) : _conversas.Values.Where(c => GroupId(c) == grupoId).Select(ConvDate).DefaultIfEmpty(DateTime.MinValue).Max();
        private bool IsCentral(Guid idEstabelecimento) => _centralEstabelecimentoId.HasValue && _centralEstabelecimentoId.Value == idEstabelecimento;
        private static bool IsOpen(Conversation c) => c.Estado != EstadoConversa.FechadoAutomaticamente && c.Estado != EstadoConversa.FechadoAgente && c.Estado != EstadoConversa.Arquivada;
        private static Guid GroupId(Conversation c) => c.IdConversaGrupo == Guid.Empty ? c.IdConversa : c.IdConversaGrupo;
        private static DateTime ConvDate(Conversation c) => c.AtualizadoEm ?? c.CriadoEm;
        private static DateTime MsgDate(Message m) => m.DataCriacao ?? m.DataEnvio ?? m.DataHora;
        private static string Estado(EstadoConversa e) => e switch { EstadoConversa.Aberto => "aberto", EstadoConversa.EmAtendimento => "em_atendimento", EstadoConversa.FechadoAgente => "fechado_agente", EstadoConversa.FechadoAutomaticamente => "fechado_bot", EstadoConversa.Arquivada => "arquivada", _ => e.ToString().ToLowerInvariant() };
        private static string NormalizeEstado(string e) => (e ?? string.Empty).Trim().ToLowerInvariant() switch { "fechado_bot" => "fechado_automaticamente", "arquivado" => "arquivada", _ => (e ?? string.Empty).Trim().ToLowerInvariant() };
        private static ConversationContext? Deserialize(string? json) { try { return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<ConversationContext>(json); } catch { return null; } }
        private static Conversation Clone(Conversation c) => new Conversation { IdConversa = c.IdConversa, IdConversaGrupo = GroupId(c), IdEstabelecimento = c.IdEstabelecimento, IdCliente = c.IdCliente, TelefoneCliente = c.TelefoneCliente, IdWa = c.IdWa, Modo = c.Modo, AgenteDesignadoId = c.AgenteDesignadoId, UltimoUsuarioEm = c.UltimoUsuarioEm, Janela24hExpiraEm = c.Janela24hExpiraEm, CriadoEm = c.CriadoEm, AtualizadoEm = c.AtualizadoEm, MessageIdWhatsapp = c.MessageIdWhatsapp, Estado = c.Estado, MotivoFechamento = c.MotivoFechamento, FechadoPorId = c.FechadoPorId, DataFechamento = c.DataFechamento, ContextoEstadoJson = c.ContextoEstadoJson };
        private static Message Clone(Message m) => new Message { Id = m.Id, IdConversa = m.IdConversa, IdMensagemWa = m.IdMensagemWa, Direcao = m.Direcao, Tipo = m.Tipo, TipoOriginal = m.TipoOriginal, Status = m.Status, IdProvedor = m.IdProvedor, CodigoErro = m.CodigoErro, MensagemErro = m.MensagemErro, Tentativas = m.Tentativas, CriadaPor = m.CriadaPor, DataHora = m.DataHora, DataEnvio = m.DataEnvio, DataEntrega = m.DataEntrega, DataLeitura = m.DataLeitura, DataCriacao = m.DataCriacao, Conteudo = m.Conteudo };
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
