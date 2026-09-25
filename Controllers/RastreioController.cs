using System;
using System.Threading.Tasks;
using APIBack.Attributes;
using APIBack.DTOs.Common;
using APIBack.DTOs.Rastreio;
using APIBack.Extensions;
using APIBack.Repository.Interface;
using APIBack.Service;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace APIBack.Controllers
{
    /// <summary>Avisos de rastreio ao cliente (Fase 6): opt-in por pedido, historico dos avisos e configuracao.</summary>
    [Route("api/v2/delivery")]
    [ApiController]
    public sealed class RastreioController : ControllerBase
    {
        private readonly IRastreioRepository _repository;

        public RastreioController(IRastreioRepository repository)
        {
            _repository = repository;
        }

        [HttpGet("pedidos/{pedidoId:int}/avisos")]
        [RequirePermission("Delivery", "visualizar")]
        public Task<IActionResult> GetPedido(int pedidoId) =>
            Run((est, _) => _repository.GetPedidoAsync(est, pedidoId));

        /// <summary>Liga/desliga o aceite do cliente para receber os avisos deste pedido.</summary>
        [HttpPut("pedidos/{pedidoId:int}/rastreio-opt-in")]
        [RequirePermission("Delivery", "editar_pedido")]
        public Task<IActionResult> SetOptIn(int pedidoId, [FromBody] SetOptInRequest? request) =>
            Run(async (est, _) =>
            {
                if (request == null) throw new DeliveryDomainException(400, "INVALID_REQUEST", "Corpo da requisicao obrigatorio.");
                if (!await _repository.SetOptInAsync(est, pedidoId, request.OptIn, "atendente"))
                {
                    throw new DeliveryDomainException(404, "PEDIDO_NOT_FOUND", "Pedido nao encontrado neste estabelecimento.");
                }
                return await _repository.GetPedidoAsync(est, pedidoId);
            });

        /// <summary>Libera um aviso que falhou/foi ignorado; o servico tenta de novo na proxima passada (se ainda fizer sentido).</summary>
        [HttpPost("pedidos/{pedidoId:int}/avisos/{tipo}/reenviar")]
        [RequirePermission("Delivery", "atribuir_motoboy")]
        public Task<IActionResult> Resend(int pedidoId, string tipo) =>
            Run(async (est, _) =>
            {
                if (!NoticeTypes.IsValid(tipo)) throw new DeliveryDomainException(422, "INVALID_REQUEST", "Tipo de aviso invalido.");
                if (!await _repository.ReleaseForResendAsync(est, pedidoId, tipo))
                {
                    throw new DeliveryDomainException(409, "NOTICE_NOT_RESENDABLE", "So aviso que falhou ou foi ignorado pode ser reenviado.");
                }
                return await _repository.GetPedidoAsync(est, pedidoId);
            });

        [HttpGet("avisos/config")]
        [RequirePermission("Delivery", "visualizar")]
        public Task<IActionResult> GetConfig() => Run(async (est, _) => ToDto(await _repository.GetSettingsAsync(est)));

        [HttpPut("avisos/config")]
        [RequirePermission("Delivery", "configurar")]
        public Task<IActionResult> UpdateConfig([FromBody] UpdateAvisosConfigRequest? request) =>
            Run(async (est, user) =>
            {
                if (request == null) throw new DeliveryDomainException(400, "INVALID_REQUEST", "Corpo da requisicao obrigatorio.");
                var settings = new NoticeSettings
                {
                    DispatchEnabled = request.DispatchEnabled,
                    ArrivingEnabled = request.ArrivingEnabled,
                    ArrivingMinutes = NoticeSettingsRules.NormalizeMinutes(request.ArrivingMinutes),
                    ArrivingRadiusM = NoticeSettingsRules.NormalizeRadius(request.ArrivingRadiusM),
                    TemplateDispatch = NoticeSettingsRules.NormalizeTemplate(request.TemplateDispatch),
                    TemplateArriving = NoticeSettingsRules.NormalizeTemplate(request.TemplateArriving)
                };
                return ToDto(await _repository.UpsertSettingsAsync(est, user, settings));
            });

        private static AvisosConfigDto ToDto(NoticeSettings settings) => new()
        {
            DispatchEnabled = settings.DispatchEnabled,
            ArrivingEnabled = settings.ArrivingEnabled,
            ArrivingMinutes = settings.ArrivingMinutes,
            ArrivingRadiusM = settings.ArrivingRadiusM,
            TemplateDispatch = settings.TemplateDispatch,
            TemplateArriving = settings.TemplateArriving
        };

        private async Task<IActionResult> Run<T>(Func<Guid, int, Task<T>> action)
        {
            var userId = HttpContext.GetUserId() ?? 0;
            var estabelecimentoId = HttpContext.GetEstabelecimentoId() ?? Guid.Empty;
            if (userId <= 0 || estabelecimentoId == Guid.Empty)
            {
                return Unauthorized(ApiResponse<object>.Fail("Contexto autenticado invalido.", "UNAUTHENTICATED"));
            }
            try
            {
                return Ok(ApiResponse<T>.Ok(await action(estabelecimentoId, userId)));
            }
            catch (DeliveryDomainException ex)
            {
                return StatusCode(ex.StatusCode, ApiResponse<object>.Fail(ex.Message, ex.Code, ex.Details));
            }
        }
    }

    /// <summary>Link publico de rastreio (sem login): so o que o cliente pode ver.</summary>
    [Route("api/public/rastreio")]
    [ApiController]
    [AllowAnonymous]
    public sealed class PublicRastreioController : ControllerBase
    {
        private readonly TrackingNoticeService _service;

        public PublicRastreioController(TrackingNoticeService service)
        {
            _service = service;
        }

        [HttpGet("{token}")]
        [EnableRateLimiting("public-tracking")]
        public async Task<IActionResult> Get(string token)
        {
            try
            {
                Response.Headers["Cache-Control"] = "no-store";
                return Ok(ApiResponse<PublicTrackingView>.Ok(await _service.GetPublicViewAsync(token)));
            }
            catch (DeliveryDomainException ex)
            {
                return StatusCode(ex.StatusCode, ApiResponse<object>.Fail(ex.Message, ex.Code));
            }
        }
    }
}
