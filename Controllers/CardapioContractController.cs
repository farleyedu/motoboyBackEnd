using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.Attributes;
using APIBack.DTOs.Cardapio;
using APIBack.DTOs.Common;
using APIBack.DTOs.Configuracoes;
using APIBack.Service;
using APIBack.Service.Interface;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers
{
    [ApiController]
    [Route("api/cardapio/{estabelecimentoId:guid}")]
    public class CardapioContractController : EstabelecimentoScopedControllerBase
    {
        private readonly ICardapioContractService _service;

        public CardapioContractController(ICardapioContractService service)
        {
            _service = service;
        }

        [HttpGet("categorias")]
        [RequirePermission("Cardapio", "visualizar")]
        public async Task<IActionResult> ListarCategorias(
            Guid estabelecimentoId,
            [FromQuery] string? busca = null,
            [FromQuery] bool? ativo = null,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20)
        {
            var error = await ValidateEstabelecimentoModuloAsync(estabelecimentoId, "Cardapio");
            if (error != null) return error;

            var response = await _service.ListarCategoriasAsync(estabelecimentoId, busca, ativo, page, pageSize);
            return Ok(ApiResponse<PagedResultDto<CardapioCategoriaContractDto>>.Ok(response));
        }

        [HttpGet("categorias/{categoriaId:guid}")]
        [RequirePermission("Cardapio", "visualizar")]
        public async Task<IActionResult> ObterCategoria(Guid estabelecimentoId, Guid categoriaId)
        {
            var error = await ValidateEstabelecimentoModuloAsync(estabelecimentoId, "Cardapio");
            if (error != null) return error;

            var response = await _service.ObterCategoriaPorIdAsync(estabelecimentoId, categoriaId);
            return response == null
                ? NotFoundErrorResponse("Categoria nao encontrada.")
                : Ok(ApiResponse<CardapioCategoriaContractDto>.Ok(response));
        }

        [HttpPost("categorias")]
        [RequirePermission("Cardapio", "criar")]
        public async Task<IActionResult> CriarCategoria(Guid estabelecimentoId, [FromBody] SalvarCardapioCategoriaContractRequest? request)
        {
            if (request == null) return BadRequestErrorResponse("Corpo da requisicao e obrigatorio.");

            var error = await ValidateEstabelecimentoModuloAsync(estabelecimentoId, "Cardapio");
            if (error != null) return error;

            try
            {
                var response = await _service.CriarCategoriaAsync(estabelecimentoId, request);
                return CreatedAtAction(nameof(ObterCategoria), new { estabelecimentoId, categoriaId = response.Id }, ApiResponse<CardapioCategoriaContractDto>.Ok(response));
            }
            catch (RequestValidationException ex)
            {
                return ValidationErrorResponse(ex);
            }
            catch (InvalidOperationException ex)
            {
                return ConflictErrorResponse(ex.Message);
            }
        }

        [HttpPut("categorias/{categoriaId:guid}")]
        [RequirePermission("Cardapio", "editar")]
        public async Task<IActionResult> AtualizarCategoria(Guid estabelecimentoId, Guid categoriaId, [FromBody] SalvarCardapioCategoriaContractRequest? request)
        {
            if (request == null) return BadRequestErrorResponse("Corpo da requisicao e obrigatorio.");

            var error = await ValidateEstabelecimentoModuloAsync(estabelecimentoId, "Cardapio");
            if (error != null) return error;

            try
            {
                var response = await _service.AtualizarCategoriaAsync(estabelecimentoId, categoriaId, request);
                return Ok(ApiResponse<CardapioCategoriaContractDto>.Ok(response));
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
        }

        [HttpPatch("categorias/{categoriaId:guid}/ativo")]
        [RequirePermission("Cardapio", "editar")]
        public async Task<IActionResult> AtualizarCategoriaAtivo(Guid estabelecimentoId, Guid categoriaId, [FromBody] AtualizarStatusRequest? request)
        {
            if (request == null) return BadRequestErrorResponse("Corpo da requisicao e obrigatorio.");

            var error = await ValidateEstabelecimentoModuloAsync(estabelecimentoId, "Cardapio");
            if (error != null) return error;

            var updated = await _service.AtualizarCategoriaStatusAsync(estabelecimentoId, categoriaId, request.Ativo);
            return updated
                ? Ok(ApiResponse<object>.Ok(new { }))
                : NotFoundErrorResponse("Categoria nao encontrada.");
        }

        [HttpDelete("categorias/{categoriaId:guid}")]
        [RequirePermission("Cardapio", "excluir")]
        public async Task<IActionResult> ExcluirCategoria(Guid estabelecimentoId, Guid categoriaId)
        {
            var error = await ValidateEstabelecimentoModuloAsync(estabelecimentoId, "Cardapio");
            if (error != null) return error;

            try
            {
                var removed = await _service.ExcluirCategoriaAsync(estabelecimentoId, categoriaId);
                return removed
                    ? Ok(ApiResponse<object>.Ok(new { }))
                    : NotFoundErrorResponse("Categoria nao encontrada.");
            }
            catch (InvalidOperationException ex)
            {
                return ConflictErrorResponse(ex.Message);
            }
        }

        [HttpGet("adicionais")]
        [RequirePermission("Cardapio", "visualizar")]
        public async Task<IActionResult> ListarAdicionais(
            Guid estabelecimentoId,
            [FromQuery] string? busca = null,
            [FromQuery] bool? ativo = null,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20)
        {
            var error = await ValidateEstabelecimentoModuloAsync(estabelecimentoId, "Cardapio");
            if (error != null) return error;

            var response = await _service.ListarAdicionaisAsync(estabelecimentoId, busca, ativo, page, pageSize);
            return Ok(ApiResponse<PagedResultDto<CardapioAdicionalDto>>.Ok(response));
        }

        [HttpGet("adicionais/{adicionalId:guid}")]
        [RequirePermission("Cardapio", "visualizar")]
        public async Task<IActionResult> ObterAdicional(Guid estabelecimentoId, Guid adicionalId)
        {
            var error = await ValidateEstabelecimentoModuloAsync(estabelecimentoId, "Cardapio");
            if (error != null) return error;

            var response = await _service.ObterAdicionalPorIdAsync(estabelecimentoId, adicionalId);
            return response == null
                ? NotFoundErrorResponse("Adicional nao encontrado.")
                : Ok(ApiResponse<CardapioAdicionalDto>.Ok(response));
        }

        [HttpPost("adicionais")]
        [RequirePermission("Cardapio", "criar")]
        public async Task<IActionResult> CriarAdicional(Guid estabelecimentoId, [FromBody] SalvarCardapioAdicionalRequest? request)
        {
            if (request == null) return BadRequestErrorResponse("Corpo da requisicao e obrigatorio.");

            var error = await ValidateEstabelecimentoModuloAsync(estabelecimentoId, "Cardapio");
            if (error != null) return error;

            try
            {
                var response = await _service.CriarAdicionalAsync(estabelecimentoId, request);
                return CreatedAtAction(nameof(ObterAdicional), new { estabelecimentoId, adicionalId = response.Id }, ApiResponse<CardapioAdicionalDto>.Ok(response));
            }
            catch (RequestValidationException ex)
            {
                return ValidationErrorResponse(ex);
            }
        }

        [HttpPut("adicionais/{adicionalId:guid}")]
        [RequirePermission("Cardapio", "editar")]
        public async Task<IActionResult> AtualizarAdicional(Guid estabelecimentoId, Guid adicionalId, [FromBody] SalvarCardapioAdicionalRequest? request)
        {
            if (request == null) return BadRequestErrorResponse("Corpo da requisicao e obrigatorio.");

            var error = await ValidateEstabelecimentoModuloAsync(estabelecimentoId, "Cardapio");
            if (error != null) return error;

            try
            {
                var response = await _service.AtualizarAdicionalAsync(estabelecimentoId, adicionalId, request);
                return Ok(ApiResponse<CardapioAdicionalDto>.Ok(response));
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

        [HttpPatch("adicionais/{adicionalId:guid}/ativo")]
        [RequirePermission("Cardapio", "editar")]
        public async Task<IActionResult> AtualizarAdicionalAtivo(Guid estabelecimentoId, Guid adicionalId, [FromBody] AtualizarStatusRequest? request)
        {
            if (request == null) return BadRequestErrorResponse("Corpo da requisicao e obrigatorio.");

            var error = await ValidateEstabelecimentoModuloAsync(estabelecimentoId, "Cardapio");
            if (error != null) return error;

            var updated = await _service.AtualizarAdicionalStatusAsync(estabelecimentoId, adicionalId, request.Ativo);
            return updated
                ? Ok(ApiResponse<object>.Ok(new { }))
                : NotFoundErrorResponse("Adicional nao encontrado.");
        }

        [HttpDelete("adicionais/{adicionalId:guid}")]
        [RequirePermission("Cardapio", "excluir")]
        public async Task<IActionResult> ExcluirAdicional(Guid estabelecimentoId, Guid adicionalId)
        {
            var error = await ValidateEstabelecimentoModuloAsync(estabelecimentoId, "Cardapio");
            if (error != null) return error;

            try
            {
                var removed = await _service.ExcluirAdicionalAsync(estabelecimentoId, adicionalId);
                return removed
                    ? Ok(ApiResponse<object>.Ok(new { }))
                    : NotFoundErrorResponse("Adicional nao encontrado.");
            }
            catch (InvalidOperationException ex)
            {
                return ConflictErrorResponse(ex.Message);
            }
        }

        [HttpGet("produtos")]
        [RequirePermission("Cardapio", "visualizar")]
        public async Task<IActionResult> ListarProdutos(
            Guid estabelecimentoId,
            [FromQuery] string? busca = null,
            [FromQuery] bool? ativo = null,
            [FromQuery] Guid? categoriaId = null,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20)
        {
            var error = await ValidateEstabelecimentoModuloAsync(estabelecimentoId, "Cardapio");
            if (error != null) return error;

            var response = await _service.ListarProdutosAsync(estabelecimentoId, busca, ativo, categoriaId, page, pageSize);
            return Ok(ApiResponse<PagedResultDto<CardapioProdutoContractDto>>.Ok(response));
        }

        [HttpGet("produtos/{produtoId:guid}")]
        [RequirePermission("Cardapio", "visualizar")]
        public async Task<IActionResult> ObterProduto(Guid estabelecimentoId, Guid produtoId)
        {
            var error = await ValidateEstabelecimentoModuloAsync(estabelecimentoId, "Cardapio");
            if (error != null) return error;

            var response = await _service.ObterProdutoPorIdAsync(estabelecimentoId, produtoId);
            return response == null
                ? NotFoundErrorResponse("Produto nao encontrado.")
                : Ok(ApiResponse<CardapioProdutoContractDto>.Ok(response));
        }

        [HttpPost("produtos")]
        [RequirePermission("Cardapio", "criar")]
        public async Task<IActionResult> CriarProduto(Guid estabelecimentoId, [FromBody] SalvarCardapioProdutoContractRequest? request)
        {
            if (request == null) return BadRequestErrorResponse("Corpo da requisicao e obrigatorio.");

            var error = await ValidateEstabelecimentoModuloAsync(estabelecimentoId, "Cardapio");
            if (error != null) return error;

            try
            {
                var response = await _service.CriarProdutoAsync(estabelecimentoId, request);
                return CreatedAtAction(nameof(ObterProduto), new { estabelecimentoId, produtoId = response.Id }, ApiResponse<CardapioProdutoContractDto>.Ok(response));
            }
            catch (RequestValidationException ex)
            {
                return ValidationErrorResponse(ex);
            }
            catch (InvalidOperationException ex)
            {
                return ConflictErrorResponse(ex.Message);
            }
        }

        [HttpPut("produtos/{produtoId:guid}")]
        [RequirePermission("Cardapio", "editar")]
        public async Task<IActionResult> AtualizarProduto(Guid estabelecimentoId, Guid produtoId, [FromBody] SalvarCardapioProdutoContractRequest? request)
        {
            if (request == null) return BadRequestErrorResponse("Corpo da requisicao e obrigatorio.");

            var error = await ValidateEstabelecimentoModuloAsync(estabelecimentoId, "Cardapio");
            if (error != null) return error;

            try
            {
                var response = await _service.AtualizarProdutoAsync(estabelecimentoId, produtoId, request);
                return Ok(ApiResponse<CardapioProdutoContractDto>.Ok(response));
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
        }

        [HttpPatch("produtos/{produtoId:guid}/ativo")]
        [RequirePermission("Cardapio", "editar")]
        public async Task<IActionResult> AtualizarProdutoAtivo(Guid estabelecimentoId, Guid produtoId, [FromBody] AtualizarStatusRequest? request)
        {
            if (request == null) return BadRequestErrorResponse("Corpo da requisicao e obrigatorio.");

            var error = await ValidateEstabelecimentoModuloAsync(estabelecimentoId, "Cardapio");
            if (error != null) return error;

            var updated = await _service.AtualizarProdutoStatusAsync(estabelecimentoId, produtoId, request.Ativo);
            return updated
                ? Ok(ApiResponse<object>.Ok(new { }))
                : NotFoundErrorResponse("Produto nao encontrado.");
        }

        [HttpPatch("produtos/{produtoId:guid}/disponivel")]
        [RequirePermission("Cardapio", "editar")]
        public async Task<IActionResult> AtualizarProdutoDisponivel(Guid estabelecimentoId, Guid produtoId, [FromBody] AtualizarDisponibilidadeCardapioRequest? request)
        {
            if (request == null) return BadRequestErrorResponse("Corpo da requisicao e obrigatorio.");

            var error = await ValidateEstabelecimentoModuloAsync(estabelecimentoId, "Cardapio");
            if (error != null) return error;

            var updated = await _service.AtualizarProdutoDisponibilidadeAsync(estabelecimentoId, produtoId, request.Disponivel);
            return updated
                ? Ok(ApiResponse<object>.Ok(new { }))
                : NotFoundErrorResponse("Produto nao encontrado.");
        }

        [HttpDelete("produtos/{produtoId:guid}")]
        [RequirePermission("Cardapio", "excluir")]
        public async Task<IActionResult> ExcluirProduto(Guid estabelecimentoId, Guid produtoId)
        {
            var error = await ValidateEstabelecimentoModuloAsync(estabelecimentoId, "Cardapio");
            if (error != null) return error;

            var removed = await _service.ExcluirProdutoAsync(estabelecimentoId, produtoId);
            return removed
                ? Ok(ApiResponse<object>.Ok(new { }))
                : NotFoundErrorResponse("Produto nao encontrado.");
        }

        [HttpGet("web-config")]
        public async Task<IActionResult> ObterWebConfig(Guid estabelecimentoId)
        {
            if (!HasAnyPermission(("CardapioWeb", "configurar"), ("CardapioWeb", "publicar")))
            {
                return ForbiddenResponse();
            }

            var error = await ValidateEstabelecimentoModuloAsync(estabelecimentoId, "CardapioWeb");
            if (error != null) return error;

            try
            {
                var response = await _service.ObterWebConfigAsync(estabelecimentoId);
                return Ok(ApiResponse<CardapioWebConfigDto>.Ok(response));
            }
            catch (KeyNotFoundException ex)
            {
                return NotFoundErrorResponse(ex.Message);
            }
        }

        [HttpPut("web-config")]
        [RequirePermission("CardapioWeb", "configurar")]
        public async Task<IActionResult> SalvarWebConfig(Guid estabelecimentoId, [FromBody] SalvarCardapioWebConfigRequest? request)
        {
            if (request == null) return BadRequestErrorResponse("Corpo da requisicao e obrigatorio.");

            var error = await ValidateEstabelecimentoModuloAsync(estabelecimentoId, "CardapioWeb");
            if (error != null) return error;

            try
            {
                var response = await _service.SalvarWebConfigAsync(estabelecimentoId, request);
                return Ok(ApiResponse<CardapioWebConfigDto>.Ok(response));
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

        [HttpPatch("web-config/publicacao")]
        [RequirePermission("CardapioWeb", "publicar")]
        public async Task<IActionResult> AtualizarPublicacao(Guid estabelecimentoId, [FromBody] AtualizarPublicacaoCardapioWebRequest? request)
        {
            if (request == null) return BadRequestErrorResponse("Corpo da requisicao e obrigatorio.");

            var error = await ValidateEstabelecimentoModuloAsync(estabelecimentoId, "CardapioWeb");
            if (error != null) return error;

            try
            {
                var response = await _service.AtualizarPublicacaoAsync(estabelecimentoId, request.Publicado);
                return Ok(ApiResponse<CardapioWebConfigDto>.Ok(response));
            }
            catch (KeyNotFoundException ex)
            {
                return NotFoundErrorResponse(ex.Message);
            }
        }

        private async Task<IActionResult?> ValidateEstabelecimentoModuloAsync(Guid estabelecimentoId, string modulo)
        {
            if (!CurrentUserId.HasValue || CurrentUserId.Value <= 0)
            {
                return UnauthorizedResponse();
            }

            if (!CurrentIsSuperAdmin)
            {
                if (!CurrentEstabelecimentoId.HasValue || CurrentEstabelecimentoId.Value == Guid.Empty)
                {
                    return Unauthorized(ApiResponse<object>.Fail("Sessao invalida para o estabelecimento atual."));
                }

                if (CurrentEstabelecimentoId.Value != estabelecimentoId)
                {
                    return ForbiddenResponse();
                }
            }

            if (!await _service.EstabelecimentoTemModuloAtivoAsync(estabelecimentoId, modulo))
            {
                return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.Fail("Modulo inativo para o estabelecimento informado."));
            }

            return null;
        }
    }
}
