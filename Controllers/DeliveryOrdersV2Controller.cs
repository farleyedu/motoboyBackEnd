using System;
using System.Threading.Tasks;
using APIBack.Attributes;
using APIBack.DTOs.Common;
using APIBack.DTOs.Delivery;
using APIBack.Extensions;
using APIBack.Repository.Interface;
using APIBack.Service;
using APIBack.Service.Interface;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers
{
    /// <summary>
    /// Comandos transacionais de fila/rota (Parte 2 do plano de delivery), usados pelo
    /// painel do atendente. O motoboy/app le a propria fila via
    /// MotoboyTrackingV2Controller.GetQueue (token operacional, sem ID arbitrario).
    /// </summary>
    [Route("api/v2/delivery")]
    [ApiController]
    public sealed class DeliveryOrdersV2Controller : ControllerBase
    {
        private readonly IPedidoQueueService _queueService;
        private readonly IPedidoCoreService _coreService;
        private readonly IPedidoConsultaRepository _consulta;
        private readonly IRastreioRepository _rastreio;
        private readonly IOperationalSessionService _sessionService;
        private readonly IPedidoHistoricoRepository _historicoRepository;
        private readonly IRestaurantSettingsRepository _restaurantRepository;
        private readonly IDeliveryZonaRepository _zonaRepository;
        private readonly IHorarioOperacaoRepository _horarioRepository;

        public DeliveryOrdersV2Controller(
            IPedidoQueueService queueService,
            IPedidoCoreService coreService,
            IPedidoConsultaRepository consulta,
            IRastreioRepository rastreio,
            IOperationalSessionService sessionService,
            IPedidoHistoricoRepository historicoRepository,
            IRestaurantSettingsRepository restaurantRepository,
            IDeliveryZonaRepository zonaRepository,
            IHorarioOperacaoRepository horarioRepository)
        {
            _queueService = queueService;
            _coreService = coreService;
            _consulta = consulta;
            _rastreio = rastreio;
            _sessionService = sessionService;
            _historicoRepository = historicoRepository;
            _restaurantRepository = restaurantRepository;
            _zonaRepository = zonaRepository;
            _horarioRepository = horarioRepository;
        }

        // ---- Lista e detalhe (tela de Pedidos) ----------------------------------

        /// <summary>Lista pedidos do estabelecimento com filtros e paginacao; inclui rascunhos.</summary>
        [HttpGet("pedidos")]
        [RequirePermission("Delivery", "visualizar")]
        public Task<IActionResult> ListPedidos([FromQuery] PedidoFiltroRequest filtro) =>
            ExecuteAsync((est, _) => _consulta.ListAsync(est, PedidoFiltro.From(filtro)));

        /// <summary>Detalhe do pedido com itens (estruturados ou lidos do texto legado).</summary>
        [HttpGet("pedidos/{pedidoId:int}")]
        [RequirePermission("Delivery", "visualizar")]
        public Task<IActionResult> GetPedido(int pedidoId) =>
            ExecuteAsync(async (est, _) =>
                await _consulta.GetAsync(est, pedidoId)
                ?? throw new DeliveryDomainException(404, "PEDIDO_NOT_FOUND", "Pedido nao encontrado."));

        // ---- Historico do pedido ------------------------------------------------

        /// <summary>Linha do tempo do pedido: atribuicoes, coleta, chegada, desfecho e transferencias.</summary>
        [HttpGet("pedidos/{pedidoId:int}/historico")]
        [RequirePermission("Delivery", "visualizar")]
        public Task<IActionResult> GetHistorico(int pedidoId) =>
            ExecuteAsync(async (est, _) =>
                await _historicoRepository.GetAsync(est, pedidoId)
                ?? throw new DeliveryDomainException(404, "PEDIDO_NOT_FOUND", "Pedido nao encontrado."));

        // ---- Pedido manual ------------------------------------------------------

        /// <summary>
        /// Cria um pedido pelo nucleo. Compativel com o formato antigo (items texto + value); com
        /// itens[] o preco vem do cardapio. Cabecalho Idempotency-Key (ou origemRef) torna a chamada
        /// repetivel: 201 quando cria, 200 com jaExistia = true quando o pedido ja existia.
        /// </summary>
        [HttpPost("pedidos")]
        [RequirePermission("Delivery", "criar_pedido")]
        public async Task<IActionResult> CreatePedido([FromBody] CreatePedidoRequest request)
        {
            if (!TryGetActor(out var actorUserId, out var estabelecimentoId, out var error)) return error!;
            try
            {
                var idempotencyKey = Request.Headers["Idempotency-Key"].ToString();
                var result = await _coreService.CreateAsync(estabelecimentoId, actorUserId, request, idempotencyKey);
                if (request.RastreioOptIn == true && !result.JaExistia)
                {
                    // Aceite do cliente registrado junto do pedido (mesma origem do pedido).
                    await _rastreio.SetOptInAsync(estabelecimentoId, result.Id, true, request.Origem ?? "atendente");
                }
                return result.JaExistia
                    ? Ok(ApiResponse<CreatedPedidoDto>.Ok(result))
                    : StatusCode(201, ApiResponse<CreatedPedidoDto>.Ok(result));
            }
            catch (DeliveryDomainException ex)
            {
                return DomainError(ex);
            }
        }

        /// <summary>Substitui os dados de um pedido em rascunho ou pendente (sem motoboy).</summary>
        [HttpPut("pedidos/{pedidoId:int}")]
        [RequirePermission("Delivery", "editar_pedido")]
        public Task<IActionResult> UpdatePedido(int pedidoId, [FromBody] CreatePedidoRequest request) =>
            ExecuteAsync((est, userId) => _coreService.UpdateAsync(est, userId, pedidoId, request));

        /// <summary>Rascunho -> pendente (o pedido passa a aparecer no mapa e a poder entrar em rota).</summary>
        [HttpPost("pedidos/{pedidoId:int}/confirmar")]
        [RequirePermission("Delivery", "criar_pedido")]
        public Task<IActionResult> ConfirmPedido(int pedidoId) =>
            ExecuteAsync((est, userId) => _coreService.ConfirmAsync(est, userId, pedidoId));

        // ---- Transferencia ------------------------------------------------------

        /// <summary>Atendente move o pedido para outro motoboy (sempre direto, sem aprovacao).</summary>
        [HttpPost("pedidos/{pedidoId:int}/transferir")]
        [RequirePermission("Delivery", "atribuir_motoboy")]
        public Task<IActionResult> TransferPedido(int pedidoId, [FromBody] TransferPedidoRequest request) =>
            ExecuteAsync((est, userId) => _queueService.TransferByOperatorAsync(est, userId, pedidoId, request.ParaMotoboyId, request.Motivo));

        [HttpGet("transferencias")]
        [RequirePermission("Delivery", "visualizar")]
        public Task<IActionResult> ListTransfers([FromQuery] string? status, [FromQuery] int limit = 50) =>
            ExecuteAsync((est, _) => _queueService.ListTransfersAsync(est, status, limit));

        [HttpPost("transferencias/{transferId:long}/aprovar")]
        [RequirePermission("Delivery", "atribuir_motoboy")]
        public Task<IActionResult> ApproveTransfer(long transferId, [FromBody] TransferDecisionRequest? request) =>
            ExecuteAsync((est, userId) => _queueService.ApproveTransferAsync(est, userId, transferId, request?.Observacao));

        [HttpPost("transferencias/{transferId:long}/rejeitar")]
        [RequirePermission("Delivery", "atribuir_motoboy")]
        public Task<IActionResult> RejectTransfer(long transferId, [FromBody] TransferDecisionRequest? request) =>
            ExecuteAsync((est, userId) => _queueService.RejectTransferAsync(est, userId, transferId, request?.Observacao));

        // ---- Trajeto do dia -----------------------------------------------------

        [HttpGet("motoboys/{motoboyId:int}/trajeto")]
        [RequirePermission("Delivery", "visualizar")]
        public async Task<IActionResult> GetTrajectory(int motoboyId, [FromQuery] string? date)
        {
            var localDate = OperationalDayWindow.Today(DateTimeOffset.UtcNow);
            if (!string.IsNullOrWhiteSpace(date) && !DateOnly.TryParse(date, System.Globalization.CultureInfo.InvariantCulture, out localDate))
            {
                return BadRequest(ApiResponse<object>.Fail("Parametro date invalido. Use YYYY-MM-DD.", "INVALID_REQUEST"));
            }
            return await ExecuteAsync((est, _) => _sessionService.GetTrajectoryAsync(est, motoboyId, localDate));
        }

        // ---- Configuracoes do estabelecimento (tela Delivery > Configuracoes) ----

        [HttpGet("configuracoes")]
        [RequirePermission("Delivery", "visualizar")]
        public Task<IActionResult> GetSettings() =>
            ExecuteAsync((est, _) => _queueService.GetSettingsAsync(est));

        /// <summary>Endereco, posicao no mapa, raio e regras de entrega do restaurante.</summary>
        [HttpGet("configuracoes/restaurante")]
        [RequirePermission("Delivery", "visualizar")]
        public Task<IActionResult> GetRestaurantSettings() =>
            ExecuteAsync(async (est, _) =>
                await _restaurantRepository.GetAsync(est)
                ?? throw new DeliveryDomainException(404, "ESTABELECIMENTO_NOT_FOUND", "Estabelecimento nao encontrado."));

        [HttpPut("configuracoes/restaurante")]
        [RequirePermission("Delivery", "configurar")]
        public Task<IActionResult> UpdateRestaurantSettings([FromBody] UpdateRestaurantSettingsRequest request) =>
            ExecuteAsync(async (est, _) =>
                await _restaurantRepository.UpdateAsync(est, RestaurantSettingsRules.Validate(request))
                ?? throw new DeliveryDomainException(404, "ESTABELECIMENTO_NOT_FOUND", "Estabelecimento nao encontrado."));

        [HttpPut("configuracoes")]
        [RequirePermission("Delivery", "configurar")]
        public Task<IActionResult> UpdateSettings([FromBody] UpdateDeliverySettingsRequest request) =>
            ExecuteAsync((est, userId) => _queueService.UpdateSettingsAsync(est, userId, request));

        // ---- Zonas de entrega (faixa de raio com taxa propria) ------------------

        [HttpGet("zonas")]
        [RequirePermission("Delivery", "visualizar")]
        public Task<IActionResult> ListZonas() => ExecuteAsync((est, _) => _zonaRepository.ListAsync(est));

        [HttpPost("zonas")]
        [RequirePermission("Delivery", "configurar")]
        public Task<IActionResult> CreateZona([FromBody] SalvarZonaRequest request) =>
            ExecuteAsync((est, _) => _zonaRepository.CreateAsync(est, DeliveryZonaRules.Validate(request)));

        [HttpPut("zonas/{zonaId:guid}")]
        [RequirePermission("Delivery", "configurar")]
        public Task<IActionResult> UpdateZona(Guid zonaId, [FromBody] SalvarZonaRequest request) =>
            ExecuteAsync(async (est, _) =>
                await _zonaRepository.UpdateAsync(est, zonaId, DeliveryZonaRules.Validate(request))
                ?? throw new DeliveryDomainException(404, "ZONA_NOT_FOUND", "Zona nao encontrada."));

        [HttpDelete("zonas/{zonaId:guid}")]
        [RequirePermission("Delivery", "configurar")]
        public Task<IActionResult> DeleteZona(Guid zonaId) =>
            ExecuteAsync(async (est, _) =>
            {
                var removed = await _zonaRepository.DeleteAsync(est, zonaId);
                if (!removed) throw new DeliveryDomainException(404, "ZONA_NOT_FOUND", "Zona nao encontrada.");
                return new { removida = true };
            });

        // ---- Horarios especiais (feriados/excecoes) ------------------------------

        [HttpGet("horarios-especiais")]
        [RequirePermission("Delivery", "visualizar")]
        public Task<IActionResult> ListHorariosEspeciais() => ExecuteAsync((est, _) => _horarioRepository.ListarEspeciaisAsync(est));

        [HttpPost("horarios-especiais")]
        [RequirePermission("Delivery", "configurar")]
        public Task<IActionResult> SaveHorarioEspecial([FromBody] SalvarHorarioEspecialRequest request) =>
            ExecuteAsync((est, _) => _horarioRepository.SalvarEspecialAsync(est, request));

        [HttpDelete("horarios-especiais/{id:long}")]
        [RequirePermission("Delivery", "configurar")]
        public Task<IActionResult> DeleteHorarioEspecial(long id) =>
            ExecuteAsync(async (est, _) =>
            {
                var removed = await _horarioRepository.ExcluirEspecialAsync(est, id);
                if (!removed) throw new DeliveryDomainException(404, "HORARIO_NOT_FOUND", "Excecao de horario nao encontrada.");
                return new { removida = true };
            });

        // ---- Simulador de SLA (usa o mesmo calculo do nucleo de pedido) ---------

        [HttpPost("simulador-sla")]
        [RequirePermission("Delivery", "visualizar")]
        public Task<IActionResult> SimularSla([FromBody] SimularSlaRequest request) =>
            ExecuteAsync(async (est, _) =>
            {
                var restaurant = await _restaurantRepository.GetAsync(est)
                    ?? throw new DeliveryDomainException(404, "ESTABELECIMENTO_NOT_FOUND", "Estabelecimento nao encontrado.");
                var zonas = await _zonaRepository.ListAtivasOrdenadasAsync(est);
                var aberto = await _horarioRepository.EstaAbertoAgoraAsync(est, DateTimeOffset.UtcNow, "America/Sao_Paulo");
                return DeliverySlaSimulator.Simular(restaurant, zonas, request, aberto);
            });

        // ---- Impacto operacional (metricas reais dos ultimos 7 dias) ------------

        [HttpGet("impacto-operacional")]
        [RequirePermission("Delivery", "visualizar")]
        public Task<IActionResult> GetImpactoOperacional() =>
            ExecuteAsync((est, _) => _historicoRepository.ObterImpactoOperacionalAsync(est));

        [HttpPost("pedidos/{pedidoId:int}/atribuir")]
        [RequirePermission("Delivery", "atribuir_motoboy")]
        public async Task<IActionResult> Assign(int pedidoId, [FromBody] AssignPedidoRequest request)
        {
            if (!TryGetActor(out var actorUserId, out var estabelecimentoId, out var error)) return error!;
            try
            {
                var snapshot = await _queueService.AssignAsync(estabelecimentoId, actorUserId, request.MotoboyId, pedidoId);
                return Ok(ApiResponse<MotoboyQueueDto>.Ok(snapshot));
            }
            catch (DeliveryDomainException ex)
            {
                return DomainError(ex);
            }
        }

        [HttpDelete("pedidos/{pedidoId:int}/fila")]
        [RequirePermission("Delivery", "atribuir_motoboy")]
        public async Task<IActionResult> Remove(int pedidoId)
        {
            if (!TryGetActor(out var actorUserId, out var estabelecimentoId, out var error)) return error!;
            try
            {
                var snapshot = await _queueService.RemoveAsync(estabelecimentoId, actorUserId, pedidoId);
                return Ok(ApiResponse<MotoboyQueueDto>.Ok(snapshot));
            }
            catch (DeliveryDomainException ex)
            {
                return DomainError(ex);
            }
        }

        [HttpPost("pedidos/{pedidoId:int}/cancelar")]
        [RequirePermission("Delivery", "atribuir_motoboy")]
        public async Task<IActionResult> Cancel(int pedidoId, [FromBody] CancelPedidoRequest request)
        {
            if (!TryGetActor(out var actorUserId, out var estabelecimentoId, out var error)) return error!;
            try
            {
                var snapshot = await _queueService.CancelAsync(estabelecimentoId, actorUserId, pedidoId, request?.Motivo);
                return Ok(ApiResponse<MotoboyQueueDto>.Ok(snapshot));
            }
            catch (DeliveryDomainException ex)
            {
                return DomainError(ex);
            }
        }

        [HttpGet("motoboys/{motoboyId:int}/fila")]
        [RequirePermission("Delivery", "visualizar")]
        public async Task<IActionResult> GetQueue(int motoboyId)
        {
            if (!TryGetActor(out _, out var estabelecimentoId, out var error)) return error!;
            try
            {
                var snapshot = await _queueService.GetQueueAsync(estabelecimentoId, motoboyId);
                return Ok(ApiResponse<MotoboyQueueDto>.Ok(snapshot));
            }
            catch (DeliveryDomainException ex)
            {
                return DomainError(ex);
            }
        }

        [HttpPut("motoboys/{motoboyId:int}/fila/reordenar")]
        [RequirePermission("Delivery", "atribuir_motoboy")]
        public async Task<IActionResult> Reorder(int motoboyId, [FromBody] ReorderQueueRequest request)
        {
            if (!TryGetActor(out var actorUserId, out var estabelecimentoId, out var error)) return error!;
            try
            {
                var snapshot = await _queueService.ReorderAsync(
                    estabelecimentoId, actorUserId, motoboyId, request.ExpectedVersion, request.PedidoIdsOrdenados);
                return Ok(ApiResponse<MotoboyQueueDto>.Ok(snapshot));
            }
            catch (DeliveryDomainException ex)
            {
                return DomainError(ex);
            }
        }

        [HttpPost("motoboys/{motoboyId:int}/fila/concluir-atual")]
        [RequirePermission("Delivery", "atribuir_motoboy")]
        public async Task<IActionResult> CompleteCurrent(int motoboyId)
        {
            if (!TryGetActor(out var actorUserId, out var estabelecimentoId, out var error)) return error!;
            try
            {
                var snapshot = await _queueService.CompleteCurrentAsync(estabelecimentoId, actorUserId, motoboyId);
                return Ok(ApiResponse<MotoboyQueueDto>.Ok(snapshot));
            }
            catch (DeliveryDomainException ex)
            {
                return DomainError(ex);
            }
        }

        /// <summary>Trava pedidos da fila: viram ancoras e o motoboy nao pode move-los, recusa-los nem transferi-los.</summary>
        [HttpPost("pedidos/travar")]
        [RequirePermission("Delivery", "atribuir_motoboy")]
        public async Task<IActionResult> LockPedidos([FromBody] LockPedidosRequest? request)
        {
            if (!TryGetActor(out var actorUserId, out var estabelecimentoId, out var error)) return error!;
            try
            {
                return Ok(ApiResponse<LockPedidosResultDto>.Ok(await _queueService.LockAsync(estabelecimentoId, actorUserId, request?.PedidoIds ?? new())));
            }
            catch (DeliveryDomainException ex)
            {
                return DomainError(ex);
            }
        }

        [HttpPost("pedidos/destravar")]
        [RequirePermission("Delivery", "atribuir_motoboy")]
        public async Task<IActionResult> UnlockPedidos([FromBody] LockPedidosRequest? request)
        {
            if (!TryGetActor(out var actorUserId, out var estabelecimentoId, out var error)) return error!;
            try
            {
                return Ok(ApiResponse<LockPedidosResultDto>.Ok(await _queueService.UnlockAsync(estabelecimentoId, actorUserId, request?.PedidoIds ?? new())));
            }
            catch (DeliveryDomainException ex)
            {
                return DomainError(ex);
            }
        }

        /// <summary>Encerra o retorno a loja do motoboy (acao manual do atendente).</summary>
        [HttpPost("motoboys/{motoboyId:int}/fila/chegou-loja")]
        [RequirePermission("Delivery", "atribuir_motoboy")]
        public async Task<IActionResult> ArriveAtStore(int motoboyId)
        {
            if (!TryGetActor(out _, out var estabelecimentoId, out var error)) return error!;
            try
            {
                return Ok(ApiResponse<MotoboyQueueDto>.Ok(await _queueService.ArriveAtStoreAsync(estabelecimentoId, motoboyId)));
            }
            catch (DeliveryDomainException ex)
            {
                return DomainError(ex);
            }
        }

        [HttpPost("motoboys/{motoboyId:int}/fila/retomar")]
        [RequirePermission("Delivery", "atribuir_motoboy")]
        public async Task<IActionResult> Resume(int motoboyId)
        {
            if (!TryGetActor(out var actorUserId, out var estabelecimentoId, out var error)) return error!;
            try
            {
                var snapshot = await _queueService.ResumeAsync(estabelecimentoId, actorUserId, motoboyId);
                return Ok(ApiResponse<MotoboyQueueDto>.Ok(snapshot));
            }
            catch (DeliveryDomainException ex)
            {
                return DomainError(ex);
            }
        }

        private bool TryGetActor(out int userId, out Guid estabelecimentoId, out IActionResult? error)
        {
            userId = HttpContext.GetUserId() ?? 0;
            estabelecimentoId = HttpContext.GetEstabelecimentoId() ?? Guid.Empty;
            if (userId <= 0 || estabelecimentoId == Guid.Empty)
            {
                error = Unauthorized(ApiResponse<object>.Fail("Contexto autenticado invalido.", "UNAUTHENTICATED"));
                return false;
            }

            error = null;
            return true;
        }

        private async Task<IActionResult> ExecuteAsync<T>(Func<Guid, int, Task<T>> action, bool created = false)
        {
            if (!TryGetActor(out var actorUserId, out var estabelecimentoId, out var error)) return error!;
            try
            {
                var result = await action(estabelecimentoId, actorUserId);
                return created
                    ? StatusCode(201, ApiResponse<T>.Ok(result))
                    : Ok(ApiResponse<T>.Ok(result));
            }
            catch (DeliveryDomainException ex)
            {
                return DomainError(ex);
            }
        }

        private IActionResult DomainError(DeliveryDomainException ex) =>
            StatusCode(ex.StatusCode, ApiResponse<object>.Fail(ex.Message, ex.Code, ex.Details));
    }
}
