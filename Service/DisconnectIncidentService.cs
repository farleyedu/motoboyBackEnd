using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using APIBack.DTOs.Delivery;
using APIBack.Repository;
using APIBack.Repository.Interface;

namespace APIBack.Service
{
    /// <summary>
    /// Atencao ao motoboy desconectado: a loja assume os pedidos de um turno encerrado. Cada acao reaproveita as operacoes
    /// de fila existentes (atribuir, cancelar, concluir...), por pedido e com idempotencia; o incidente so guarda a decisao.
    /// </summary>
    public sealed class DisconnectIncidentService
    {
        private const int MaxReason = 500;
        private readonly DisconnectIncidentRepository _repository;
        private readonly IPedidoQueueRepository _queue;

        public DisconnectIncidentService(DisconnectIncidentRepository repository, IPedidoQueueRepository queue)
        {
            _repository = repository;
            _queue = queue;
        }

        public Task<IncidenteAtencaoListaDto> ListAsync(Guid estabelecimentoId, bool includeResolved, DateTimeOffset? since, bool exceptional) =>
            _repository.ListAsync(estabelecimentoId, includeResolved, since, exceptional);

        public async Task<IncidenteAtencaoDto> GetAsync(Guid estabelecimentoId, Guid incidentId, bool exceptional) =>
            await _repository.GetAsync(estabelecimentoId, incidentId, exceptional)
            ?? throw new DeliveryDomainException(404, "INCIDENT_NOT_FOUND", "Incidente nao encontrado neste estabelecimento.");

        public async Task<AcaoIncidenteResponse> ActAsync(
            Guid estabelecimentoId, int actorUserId, bool exceptional, Guid incidentId, AcaoIncidenteRequest? request)
        {
            if (request == null) throw new DeliveryDomainException(400, "INVALID_REQUEST", "Corpo da requisicao obrigatorio.");
            if (!IncidentActions.IsKnown(request.TipoAcao))
                throw new DeliveryDomainException(422, "ACAO_DESCONHECIDA", "tipoAcao invalido.");
            request.IdempotencyKey = request.IdempotencyKey?.Trim();
            if (string.IsNullOrEmpty(request.IdempotencyKey) || request.IdempotencyKey.Length > 100)
                throw new DeliveryDomainException(422, "IDEMPOTENCY_KEY_REQUIRED", "idempotencyKey e obrigatorio (ate 100 caracteres).");
            if (request.VersaoEsperada is null)
                throw new DeliveryDomainException(422, "VERSAO_OBRIGATORIA", "versaoEsperada e obrigatoria.");
            request.Motivo = request.Motivo?.Trim();
            if (request.Motivo is { Length: > MaxReason })
                throw new DeliveryDomainException(422, "MOTIVO_MUITO_LONGO", $"motivo excede {MaxReason} caracteres.");
            if (request.PedidoIds is { Count: > 50 })
                throw new DeliveryDomainException(422, "LOTE_MUITO_GRANDE", "Ate 50 pedidos por acao.");

            var plan = await _repository.BeginActionAsync(estabelecimentoId, actorUserId, exceptional, incidentId, request);
            if (plan.Replay != null) return plan.Replay;

            var outcomes = new List<IncidentItemOutcome>();
            if (plan.Items.Count > 0)
                outcomes.AddRange(await RunItemsAsync(estabelecimentoId, actorUserId, request, plan));
            return await _repository.CompleteActionAsync(estabelecimentoId, actorUserId, exceptional, incidentId, request, plan, outcomes);
        }

        private async Task<List<IncidentItemOutcome>> RunItemsAsync(
            Guid est, int actor, AcaoIncidenteRequest request, IncidentActionPlan plan)
        {
            var action = request.TipoAcao!;
            var outcomes = new List<IncidentItemOutcome>();

            // Pedidos que voltam a um motoboy: uma oferta so por motoboy (tudo ou nada), como a rota do painel.
            var toAssign = plan.Items.Where(i =>
                action == IncidentActions.Redistribuir ||
                (action == IncidentActions.ReofertarMesmoMotoboy && !(i.Collected && i.State == DisconnectIncidentRules.Pendente))).ToList();
            if (toAssign.Count > 0)
            {
                var target = action == IncidentActions.Redistribuir ? request.MotoboyDestinoId!.Value : plan.PreviousMotoboyId;
                var resolution = action == IncidentActions.Redistribuir ? "redistribuido" : "reofertado";
                try
                {
                    if (toAssign.Count == 1) await _queue.AssignAsync(est, actor, target, toAssign[0].PedidoId);
                    else await _queue.AssignRouteAsync(est, actor, target, toAssign.Select(i => i.PedidoId).ToList());
                    outcomes.AddRange(toAssign.Select(i => Done(i.PedidoId, "resolvido", resolution)));
                }
                catch (DeliveryDomainException ex)
                {
                    outcomes.AddRange(toAssign.Select(i => Failed(i.PedidoId, ex)));
                }
            }

            foreach (var item in plan.Items.Except(toAssign))
            {
                try { outcomes.Add(await RunOneAsync(est, actor, request, item)); }
                catch (DeliveryDomainException ex) { outcomes.Add(Failed(item.PedidoId, ex)); }
            }
            return outcomes;
        }

        private async Task<IncidentItemOutcome> RunOneAsync(Guid est, int actor, AcaoIncidenteRequest request, IncidentItemPlan item)
        {
            var withMotoboy = item.Collected && item.State == DisconnectIncidentRules.Pendente;
            switch (request.TipoAcao)
            {
                case IncidentActions.ReofertarMesmoMotoboy:
                {
                    // Pedido coletado segue na fila do proprio motoboy: confere que ele a recuperou ao reconectar.
                    var queue = await _queue.GetQueueAsync(est, item.PreviousMotoboyId);
                    var has = queue.Current?.PedidoId == item.PedidoId || queue.Next.Any(n => n.PedidoId == item.PedidoId);
                    if (!has) throw new DeliveryDomainException(409, "PEDIDO_NOT_IN_QUEUE", "O pedido nao esta mais na fila deste motoboy.");
                    return Done(item.PedidoId, "resolvido", "retomado_pelo_motoboy");
                }
                case IncidentActions.ConfirmarDevolucaoFisica:
                    await _queue.ReleaseCollectedAfterReturnAsync(est, actor, item.PedidoId, request.Motivo);
                    return Done(item.PedidoId, DisconnectIncidentRules.Devolvido, null);
                case IncidentActions.RegistrarFalha:
                    if (withMotoboy) await _queue.ReleaseCollectedAfterReturnAsync(est, actor, item.PedidoId, request.Motivo);
                    return Done(item.PedidoId, "resolvido", "falha_registrada");
                case IncidentActions.ConcluirManual:
                    if (withMotoboy) await _queue.CompleteStopAsync(est, actor, item.PedidoId, request.Motivo);
                    else await _queue.MarcarEntregueManualAsync(est, actor, item.PedidoId, "incidente_desconexao", request.Motivo);
                    return Done(item.PedidoId, "resolvido", "concluido_manual");
                case IncidentActions.Cancelar:
                    await _queue.CancelAsync(est, actor, item.PedidoId, request.Motivo);
                    return Done(item.PedidoId, "resolvido", "cancelado");
                default:
                    throw new DeliveryDomainException(422, "ACAO_NAO_PERMITIDA", "Acao nao se aplica a pedidos.");
            }
        }

        private static IncidentItemOutcome Done(int pedidoId, string state, string? resolution) =>
            new() { PedidoId = pedidoId, Ok = true, NewState = state, Resolution = resolution };

        private static IncidentItemOutcome Failed(int pedidoId, DeliveryDomainException ex) =>
            new() { PedidoId = pedidoId, Ok = false, Code = ex.Code, Message = ex.Message };
    }
}
