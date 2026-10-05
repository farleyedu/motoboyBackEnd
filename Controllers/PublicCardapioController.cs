using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.DTOs.Cardapio;
using APIBack.DTOs.Common;
using APIBack.Service;
using APIBack.Service.Interface;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace APIBack.Controllers
{
    [ApiController]
    [Route("api/cardapio/web")]
    [AllowAnonymous]
    public class PublicCardapioController : ApiControllerBase
    {
        private readonly ICardapioPublicService _service;
        private readonly ICardapioPedidoWebService _pedidosWeb;
        private readonly IMemoryCache _cache;

        // Cada busca de endereco custa uma chamada ao Google: sem este teto, qualquer script poderia gastar a cota.
        private const int MaximoDeBuscasPorJanela = 40;
        private static readonly TimeSpan JanelaDeBuscas = TimeSpan.FromMinutes(10);

        // Nao revela dado de ninguem sem a janela de 2h (Fase 3c), mas o teto evita que um script varra numeros ao acaso.
        private const int MaximoDeIdentificacoesPorJanela = 30;
        private static readonly TimeSpan JanelaDeIdentificacoes = TimeSpan.FromMinutes(10);

        public PublicCardapioController(ICardapioPublicService service, ICardapioPedidoWebService pedidosWeb, IMemoryCache cache)
        {
            _service = service;
            _pedidosWeb = pedidosWeb;
            _cache = cache;
        }

        /// <summary>Verdadeiro quando este IP ainda pode buscar endereco.</summary>
        private bool PodeBuscarEndereco()
        {
            var chave = "busca-endereco:" + (HttpContext.Connection.RemoteIpAddress?.ToString() ?? "desconhecido");
            var usadas = _cache.GetOrCreate(chave, entrada =>
            {
                entrada.AbsoluteExpirationRelativeToNow = JanelaDeBuscas;
                return new int[1];
            })!;
            return System.Threading.Interlocked.Increment(ref usadas[0]) <= MaximoDeBuscasPorJanela;
        }

        [HttpPost("localizar-endereco")]
        public async Task<IActionResult> LocalizarEndereco([FromBody] LocalizarCardapioEnderecoRequest? request)
        {
            if (request == null) return BadRequestErrorResponse("Corpo da requisicao e obrigatorio.");
            if (!PodeBuscarEndereco()) return StatusCode(429, ApiResponse<object>.Fail("Muitas buscas de endereco. Tente de novo em alguns minutos."));

            try
            {
                return Ok(ApiResponse<CardapioLocalizacaoDto>.Ok(await _service.LocalizarEnderecoAsync(request)));
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

        [HttpPost("endereco-do-ponto")]
        public async Task<IActionResult> EnderecoDoPonto([FromBody] CardapioEnderecoDoPontoRequest? request)
        {
            if (request == null) return BadRequestErrorResponse("Corpo da requisicao e obrigatorio.");
            if (!PodeBuscarEndereco()) return StatusCode(429, ApiResponse<object>.Fail("Muitas buscas de endereco. Tente de novo em alguns minutos."));

            try
            {
                return Ok(ApiResponse<CardapioEnderecoDoPontoDto>.Ok(await _service.ObterEnderecoDoPontoAsync(request)));
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

        /// <summary>
        /// Prefill seguro do checkout (Fase 3c): so devolve nome/endereco quando o telefone falou com a loja
        /// ha pouco; senao, devolve "nao identificado" sem revelar se existe cadastro.
        /// </summary>
        [HttpGet("cliente")]
        public async Task<IActionResult> IdentificarCliente(
            [FromQuery] string telefone,
            [FromQuery] Guid? estabelecimentoId = null,
            [FromQuery] string? estabelecimentoSlug = null)
        {
            var chave = "identificar-cliente:" + (HttpContext.Connection.RemoteIpAddress?.ToString() ?? "desconhecido");
            var usadas = _cache.GetOrCreate(chave, entrada =>
            {
                entrada.AbsoluteExpirationRelativeToNow = JanelaDeIdentificacoes;
                return new int[1];
            })!;
            if (System.Threading.Interlocked.Increment(ref usadas[0]) > MaximoDeIdentificacoesPorJanela)
            {
                return StatusCode(429, ApiResponse<object>.Fail("Muitas tentativas. Tente de novo em alguns minutos."));
            }

            try
            {
                var response = await _service.IdentificarClienteAsync(estabelecimentoId, estabelecimentoSlug, telefone);
                return Ok(ApiResponse<CardapioClienteIdentificadoDto>.Ok(response));
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

        [HttpPost("pedidos/{id:guid}/trocar-telefone")]
        public async Task<IActionResult> TrocarTelefone(Guid id, [FromBody] TrocarTelefonePedidoPublicoRequest? request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Telefone))
            {
                return BadRequestErrorResponse("Informe o WhatsApp do pedido.");
            }

            try
            {
                var response = await _pedidosWeb.TrocarTelefoneAsync(id, request.Telefone);
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
