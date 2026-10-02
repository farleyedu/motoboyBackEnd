using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.DTOs.Cardapio;
using APIBack.DTOs.Common;
using APIBack.Service;
using APIBack.Service.Interface;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers
{
    [ApiController]
    [Route("api/cardapio/web")]
    [AllowAnonymous]
    public class PublicCardapioController : ApiControllerBase
    {
        private readonly ICardapioPublicService _service;
        private readonly ICardapioPedidoWebService _pedidosWeb;

        public PublicCardapioController(ICardapioPublicService service, ICardapioPedidoWebService pedidosWeb)
        {
            _service = service;
            _pedidosWeb = pedidosWeb;
        }

        [HttpGet("catalogo")]
        public async Task<IActionResult> ObterCatalogo(
            [FromQuery] Guid? estabelecimentoId = null,
            [FromQuery] string? estabelecimentoSlug = null,
            [FromQuery] string? busca = null)
        {
            try
            {
                var response = await _service.ObterCatalogoAsync(estabelecimentoId, estabelecimentoSlug, busca);
                return Ok(ApiResponse<CardapioPublicoCatalogoDto>.Ok(response));
            }
            catch (RequestValidationException ex)
            {
                return ValidationErrorResponse(ex);
            }
            catch (KeyNotFoundException ex)
            {
                return NotFoundErrorResponse(ex.Message);
            }
        }

        [HttpGet("produtos/{slug}")]
        public async Task<IActionResult> ObterProduto(
            string slug,
            [FromQuery] Guid? estabelecimentoId = null,
            [FromQuery] string? estabelecimentoSlug = null)
        {
            try
            {
                var response = await _service.ObterProdutoAsync(estabelecimentoId, estabelecimentoSlug, slug);
                return response == null
                    ? NotFoundErrorResponse("Produto nao encontrado.")
                    : Ok(ApiResponse<CardapioPublicoProdutoDto>.Ok(response));
            }
            catch (RequestValidationException ex)
            {
                return ValidationErrorResponse(ex);
            }
            catch (KeyNotFoundException ex)
            {
                return NotFoundErrorResponse(ex.Message);
            }
        }

        [HttpPost("cotacao")]
        public async Task<IActionResult> CalcularCotacao([FromBody] CalcularCardapioPedidoPublicoRequest? request)
        {
            if (request == null)
            {
                return BadRequestErrorResponse("Corpo da requisicao e obrigatorio.");
            }

            try
            {
                var response = await _service.CalcularCotacaoAsync(request);
                return Ok(ApiResponse<CardapioCotacaoDto>.Ok(response));
            }
            catch (RequestValidationException ex)
            {
                return ValidationErrorResponse(ex);
            }
            catch (KeyNotFoundException ex)
            {
                return NotFoundErrorResponse(ex.Message);
            }
        }

        [HttpPost("pedidos")]
        public async Task<IActionResult> CriarPedido([FromBody] CriarCardapioPedidoPublicoRequest? request)
        {
            if (request == null)
            {
                return BadRequestErrorResponse("Corpo da requisicao e obrigatorio.");
            }

            try
            {
                var response = await _service.CriarPedidoAsync(request);
                return Created(string.Empty, ApiResponse<CardapioPedidoPublicoCriadoDto>.Ok(response));
            }
            catch (RequestValidationException ex)
            {
                return ValidationErrorResponse(ex);
            }
            catch (KeyNotFoundException ex)
            {
                return NotFoundErrorResponse(ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return ConflictErrorResponse(ex.Message);
            }
            catch (DeliveryDomainException ex)
            {
                return StatusCode(ex.StatusCode, ApiResponse<object>.Fail(ex.Message, ex.Code));
            }
        }

        /// <summary>Acompanhamento do pedido pela tela do cardapio. O id (GUID) e o segredo de quem fez o pedido.</summary>
        [HttpGet("pedidos/{id:guid}/status")]
        public async Task<IActionResult> ObterStatusPedido(Guid id)
        {
            try
            {
                var response = await _pedidosWeb.ObterStatusAsync(id);
                return response == null
                    ? NotFoundErrorResponse("Pedido nao encontrado.")
                    : Ok(ApiResponse<CardapioPedidoPublicoStatusDto>.Ok(response));
            }
            catch (DeliveryDomainException ex)
            {
                return StatusCode(ex.StatusCode, ApiResponse<object>.Fail(ex.Message, ex.Code));
            }
        }

        /// <summary>Codigo novo para quem deixou o anterior vencer (o codigo vale poucos minutos).</summary>
        [HttpPost("pedidos/{id:guid}/novo-codigo")]
        public async Task<IActionResult> GerarNovoCodigo(Guid id)
        {
            try
            {
                var response = await _pedidosWeb.GerarNovoCodigoAsync(id);
                return Ok(ApiResponse<CardapioConfirmacaoDto>.Ok(response));
            }
            catch (KeyNotFoundException ex)
            {
                return NotFoundErrorResponse(ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return ConflictErrorResponse(ex.Message);
            }
            catch (DeliveryDomainException ex)
            {
                return StatusCode(ex.StatusCode, ApiResponse<object>.Fail(ex.Message, ex.Code));
            }
        }
    }
}
