using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using APIBack.Automation.Services;
using APIBack.DTOs.Atendimento;
using APIBack.Repository.Interface;
using APIBack.Service.Interface;

namespace APIBack.Service
{
    /// <summary>Casos de uso do atendimento (Fase 5): validacao aqui, dados no repositorio.</summary>
    public sealed class AtendimentoService
    {
        private readonly IAtendimentoRepository _repository;
        private readonly IPedidoQueueService _queueService;
        private readonly ConversationManagementService _conversationManagement;

        public AtendimentoService(IAtendimentoRepository repository, IPedidoQueueService queueService, ConversationManagementService conversationManagement)
        {
            _repository = repository;
            _queueService = queueService;
            _conversationManagement = conversationManagement;
        }

        // ---- configuracao ------------------------------------------------------

        public Task<AtendimentoConfigDto> GetConfigAsync(Guid estabelecimentoId) => _repository.GetConfigAsync(estabelecimentoId);

        public Task<AtendimentoConfigDto> UpdateConfigAsync(Guid estabelecimentoId, int actorUserId, UpdateAtendimentoConfigRequest? request) =>
            _repository.UpsertConfigAsync(estabelecimentoId, actorUserId, AtendimentoConfigRules.Validate(request));

        // ---- respostas rapidas -------------------------------------------------

        public Task<IReadOnlyList<RespostaRapidaDto>> ListRespostasAsync(Guid estabelecimentoId, bool onlyActive) =>
            _repository.ListRespostasAsync(estabelecimentoId, onlyActive);

        public Task<RespostaRapidaDto> CreateRespostaAsync(Guid estabelecimentoId, SalvarRespostaRapidaRequest? request) =>
            _repository.CreateRespostaAsync(estabelecimentoId, QuickReplyRules.Normalize(request));

        public async Task<RespostaRapidaDto> UpdateRespostaAsync(Guid estabelecimentoId, Guid id, SalvarRespostaRapidaRequest? request) =>
            await _repository.UpdateRespostaAsync(estabelecimentoId, id, QuickReplyRules.Normalize(request))
            ?? throw new DeliveryDomainException(404, "RESPOSTA_NOT_FOUND", "Resposta rapida nao encontrada.");

        public async Task DeleteRespostaAsync(Guid estabelecimentoId, Guid id)
        {
            if (!await _repository.DeleteRespostaAsync(estabelecimentoId, id))
            {
                throw new DeliveryDomainException(404, "RESPOSTA_NOT_FOUND", "Resposta rapida nao encontrada.");
            }
        }

        /// <summary>Preenche as variaveis com os dados do pedido; o que nao tem valor volta em <c>Pendentes</c>.</summary>
        public async Task<RenderRespostaResult> RenderAsync(Guid estabelecimentoId, RenderRespostaRequest? request)
        {
            var text = request?.Texto?.Trim();
            if (string.IsNullOrEmpty(text)) throw new DeliveryDomainException(422, "INVALID_REQUEST", "Informe o texto da resposta.");
            if (text.Length > QuickReplyRules.MaxTexto) throw new DeliveryDomainException(422, "INVALID_REQUEST", "Texto grande demais.");
            var values = await _repository.GetPedidoVariablesAsync(estabelecimentoId, request!.PedidoId);
            return QuickReplyRenderer.Render(text, values);
        }

        // ---- conversa <-> pedido -----------------------------------------------

        public Task<ConversaDoPedidoDto> GetConversaDoPedidoAsync(Guid estabelecimentoId, int pedidoId) =>
            _repository.GetConversaDoPedidoAsync(estabelecimentoId, EnsurePositive(pedidoId));

        public Task<ConversaDoPedidoDto> VincularAsync(Guid estabelecimentoId, Guid conversaId, int pedidoId) =>
            _repository.VincularAsync(estabelecimentoId, conversaId, EnsurePositive(pedidoId));

        public Task<ConversaDoPedidoDto> AbrirConversaDoPedidoAsync(Guid estabelecimentoId, int pedidoId) =>
            _repository.AbrirConversaDoPedidoAsync(estabelecimentoId, EnsurePositive(pedidoId));

        // ---- aviso de despacho ao cliente --------------------------------------

        public const int MaxPedidosPorConsultaDeCanal = 30;

        /// <summary>Para cada pedido da rota: a loja consegue mandar mensagem ao cliente? Pedido que nao e desta loja fica de fora.</summary>
        public async Task<IReadOnlyList<PedidoCanalDto>> GetCanaisAsync(Guid estabelecimentoId, PedidosCanaisRequest? request)
        {
            var ids = (request?.PedidoIds ?? new List<int>()).Where(id => id > 0).Distinct().ToList();
            if (ids.Count == 0) throw new DeliveryDomainException(422, "INVALID_REQUEST", "Informe ao menos um pedido.");
            if (ids.Count > MaxPedidosPorConsultaDeCanal)
            {
                throw new DeliveryDomainException(422, "INVALID_REQUEST", $"Consulte no maximo {MaxPedidosPorConsultaDeCanal} pedidos por vez.");
            }

            var canais = new List<PedidoCanalDto>(ids.Count);
            foreach (var id in ids)
            {
                try
                {
                    canais.Add(await _repository.GetCanalDoPedidoAsync(estabelecimentoId, id));
                }
                catch (DeliveryDomainException ex) when (ex.Code == "PEDIDO_NOT_FOUND")
                {
                    // some da resposta: quem chamou trata como "sem canal"
                }
            }
            return canais;
        }

        /// <summary>
        /// Manda ao cliente o aviso de que o pedido foi para a rota de um motoboy. A regra de quem pode receber e
        /// conferida aqui de novo (nao so na tela): iFood, quem nunca escreveu e janela fechada sao recusados.
        /// </summary>
        public async Task<AvisoDespachoResultDto> SendAvisoDespachoAsync(Guid estabelecimentoId, int pedidoId, SendAvisoDespachoRequest? request)
        {
            var texto = ValidateClientMessageText(request?.Mensagem);
            var conversaId = await EnsureClientePodeReceberAsync(estabelecimentoId, EnsurePositive(pedidoId));
            var mensagemId = await SendPelaConversaAsync(conversaId, estabelecimentoId, texto, "atendente");
            return new AvisoDespachoResultDto { PedidoId = pedidoId, ConversaId = conversaId, MensagemId = mensagemId };
        }

        /// <summary>
        /// Barra, antes de qualquer envio, o cliente com quem a loja nao tem canal (iFood, sem conversa, nunca escreveu,
        /// janela de 24h fechada). Vale para toda mensagem ligada a um pedido, venha do atendente ou do motoboy.
        /// </summary>
        private async Task<Guid> EnsureClientePodeReceberAsync(Guid estabelecimentoId, int pedidoId)
        {
            var canal = await _repository.GetCanalDoPedidoAsync(estabelecimentoId, pedidoId);
            if (!canal.PodeReceber || !canal.ConversaId.HasValue)
            {
                throw new DeliveryDomainException(409, "CLIENTE_SEM_CANAL", ClienteCanalRules.Explain(canal.Motivo), new { motivo = canal.Motivo });
            }
            return canal.ConversaId.Value;
        }

        /// <summary>
        /// Envia pelo WhatsApp da loja. O erro do modulo de conversas vira erro de dominio: os controllers do delivery
        /// so tratam <see cref="DeliveryDomainException"/>, e sem isto a falha chegava ao front como 500 sem explicacao.
        /// </summary>
        private async Task<Guid> SendPelaConversaAsync(Guid conversaId, Guid estabelecimentoId, string texto, string criadaPor)
        {
            try
            {
                return await _conversationManagement.SendSystemNoticeAsync(conversaId, estabelecimentoId, texto, criadaPor);
            }
            catch (ConversationManagementException ex)
            {
                throw new DeliveryDomainException(ex.StatusCode, ex.Code ?? "MENSAGEM_NAO_ENVIADA", ex.Message);
            }
        }

        // ---- mensagens atendente <-> motoboy -----------------------------------

        public Task<MotoboyMessageDto> SendToMotoboyAsync(Guid estabelecimentoId, int actorUserId, int motoboyId, SendMotoboyMessageRequest? request)
        {
            var (body, key) = MotoboyMessageRules.Normalize(request?.Body, request?.QuickKey, operatorSide: true);
            return _repository.SendMotoboyMessageAsync(estabelecimentoId, EnsurePositive(motoboyId), request?.PedidoId, "operator", body, key, actorUserId);
        }

        public Task<MotoboyMessageDto> SendFromMotoboyAsync(Guid estabelecimentoId, int motoboyId, SendMotoboyMessageRequest? request)
        {
            var (body, key) = MotoboyMessageRules.Normalize(request?.Body, request?.QuickKey, operatorSide: false);
            return _repository.SendMotoboyMessageAsync(estabelecimentoId, EnsurePositive(motoboyId), request?.PedidoId, "motoboy", body, key, null);
        }

        public Task<IReadOnlyList<MotoboyMessageDto>> ListMessagesAsync(Guid estabelecimentoId, int? motoboyId, int? pedidoId, int limit) =>
            _repository.ListMotoboyMessagesAsync(estabelecimentoId, motoboyId, pedidoId, limit <= 0 ? 50 : limit);

        public Task<int> MarkReadAsync(Guid estabelecimentoId, int motoboyId, bool readerIsMotoboy) =>
            _repository.MarkReadAsync(estabelecimentoId, EnsurePositive(motoboyId), readerIsMotoboy ? "motoboy" : "operator");

        // ---- lista de motoboys (contatos) e grupo da loja (Fase D/E) -----------

        public Task<IReadOnlyList<MotoboyRosterEntryDto>> ListMotoboysAsync(Guid estabelecimentoId) =>
            _repository.ListMotoboysVinculadosAsync(estabelecimentoId);

        public Task<IReadOnlyList<MotoboyGroupMessageDto>> ListGroupMessagesAsync(Guid estabelecimentoId, int limit) =>
            _repository.ListGroupMessagesAsync(estabelecimentoId, limit <= 0 ? 50 : limit);

        public Task<MotoboyGroupMessageDto> SendGroupMessageFromOperatorAsync(Guid estabelecimentoId, int actorUserId, SendMotoboyGroupMessageRequest? request)
        {
            var (body, _) = MotoboyMessageRules.Normalize(request?.Body, null, operatorSide: true);
            return _repository.SendGroupMessageAsync(estabelecimentoId, "operator", null, actorUserId, body);
        }

        public Task<MotoboyGroupMessageDto> SendGroupMessageFromMotoboyAsync(Guid estabelecimentoId, int motoboyId, SendMotoboyGroupMessageRequest? request)
        {
            var (body, _) = MotoboyMessageRules.Normalize(request?.Body, null, operatorSide: false);
            return _repository.SendGroupMessageAsync(estabelecimentoId, "motoboy", EnsurePositive(motoboyId), null, body);
        }

        // ---- mensagem motoboy -> cliente (Fase D) ------------------------------

        /// <summary>
        /// Motoboy manda mensagem ao cliente do pedido, pelo WhatsApp da loja (unico canal que o cliente tem).
        /// So permitido se o pedido estiver AGORA na fila do motoboy (atual ou nas proximas paradas); a mensagem
        /// fica marcada internamente com o nome do motoboy, mas o cliente ve como veio da loja.
        /// </summary>
        public async Task<MotoboyClientMessageResultDto> SendToClientAsync(Guid estabelecimentoId, int motoboyId, int pedidoId, SendMotoboyClientMessageRequest? request)
        {
            var texto = ValidateClientMessageText(request?.Mensagem);

            var queue = await _queueService.GetQueueAsync(estabelecimentoId, EnsurePositive(motoboyId));
            if (!PedidoEstaNaFila(queue, pedidoId))
            {
                throw new DeliveryDomainException(403, "PEDIDO_NOT_IN_YOUR_QUEUE", "Este pedido nao esta na sua fila.");
            }

            // Antes isto abria (ou criava) a conversa e tentava enviar de qualquer jeito: para quem pediu no balcao e
            // nunca escreveu, ou para pedido do iFood, o WhatsApp recusava e sobrava uma conversa vazia. Agora o
            // motoboy recebe o motivo na hora e nada e criado.
            await EnsureClientePodeReceberAsync(estabelecimentoId, pedidoId);

            // Com o canal conferido a conversa ja existe: isto so liga o pedido a ela, se ainda nao estiver ligado.
            var conversa = await _repository.AbrirConversaDoPedidoAsync(estabelecimentoId, pedidoId);
            if (!conversa.ConversaId.HasValue)
            {
                throw new DeliveryDomainException(422, "CONVERSA_NOT_FOUND", "Nao foi possivel abrir a conversa do pedido.");
            }

            var nomeMotoboy = await _repository.ObterNomeMotoboyAsync(motoboyId);
            var criadaPor = BuildCriadaPorLabel(nomeMotoboy);

            var mensagemId = await SendPelaConversaAsync(conversa.ConversaId.Value, estabelecimentoId, texto, criadaPor);
            return new MotoboyClientMessageResultDto { ConversaId = conversa.ConversaId.Value, MensagemId = mensagemId };
        }

        /// <summary>Internal (nao private) para ser testada direto por Tests/Unit/AtendimentoMotoboyClienteTests.cs.</summary>
        internal static string ValidateClientMessageText(string? mensagem)
        {
            var texto = mensagem?.Trim();
            if (string.IsNullOrWhiteSpace(texto))
            {
                throw new DeliveryDomainException(422, "INVALID_REQUEST", "Mensagem obrigatoria.");
            }
            if (texto.Length > 1000)
            {
                throw new DeliveryDomainException(422, "INVALID_REQUEST", "Mensagem grande demais (maximo 1000 caracteres).");
            }
            return texto;
        }

        internal static bool PedidoEstaNaFila(APIBack.DTOs.Delivery.MotoboyQueueDto queue, int pedidoId) =>
            queue.Current?.PedidoId == pedidoId || queue.Next.Any(stop => stop.PedidoId == pedidoId);

        internal static string BuildCriadaPorLabel(string? nomeMotoboy) =>
            string.IsNullOrWhiteSpace(nomeMotoboy) ? "motoboy" : $"Motoboy {nomeMotoboy.Trim()}";

        public static IReadOnlyList<MotoboyShortcutDto> Shortcuts(bool operatorSide) =>
            (operatorSide ? MotoboyMessageRules.OperatorShortcuts : MotoboyMessageRules.MotoboyShortcuts)
                .Select(s => new MotoboyShortcutDto { Key = s.Key, Text = s.Text }).ToList();

        private static int EnsurePositive(int value)
        {
            if (value <= 0) throw new DeliveryDomainException(422, "INVALID_REQUEST", "Identificador invalido.");
            return value;
        }
    }
}
