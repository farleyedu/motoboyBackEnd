using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.Attributes;
using APIBack.DTOs.Cardapio;
using APIBack.DTOs.Common;
using APIBack.DTOs.Configuracoes;
using APIBack.Service;
using APIBack.Service.Interface;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers
{
    [ApiController]
    [Route("api/cardapio")]
    public class CardapioController : EstabelecimentoScopedControllerBase
    {
        private readonly ICardapioService _service;

        public CardapioController(ICardapioService service)
        {
            _service = service;
        }

        [HttpGet("snapshot")]
        [RequirePermission("Cardapio", "visualizar")]
        public async Task<IActionResult> Snapshot()
        {
            if (!TryResolveCurrentEstabelecimentoAndModule("Cardapio", out var estabelecimentoId, out var error))
            {
                return error!;
            }

            var response = await _service.ObterSnapshotAsync(estabelecimentoId);
            return Ok(ApiResponse<CardapioSnapshotDto>.Ok(response));
        }

        [HttpGet("categorias")]
        [RequirePermission("Cardapio", "visualizar")]
        public async Task<IActionResult> ListarCategorias(
            [FromQuery] string? busca = null,
            [FromQuery] bool? ativo = null,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20)
        {
            if (!TryResolveCurrentEstabelecimentoAndModule("Cardapio", out var estabelecimentoId, out var error))
            {
                return error!;
            }

            var response = await _service.ListarCategoriasAsync(estabelecimentoId, busca, ativo, page, pageSize);
            return Ok(ApiResponse<PagedResultDto<CardapioCategoriaDto>>.Ok(response));
        }

        [HttpGet("categorias/{id:guid}")]
        [RequirePermission("Cardapio", "visualizar")]
        public async Task<IActionResult> ObterCategoria(Guid id)
        {
            if (!TryResolveCurrentEstabelecimentoAndModule("Cardapio", out var estabelecimentoId, out var error))
            {
                return error!;
            }

            var response = await _service.ObterCategoriaPorIdAsync(estabelecimentoId, id);
            return response == null
                ? NotFoundErrorResponse("Categoria nao encontrada.")
                : Ok(ApiResponse<CardapioCategoriaDto>.Ok(response));
        }

        [HttpPost("categorias")]
        [RequirePermission("Cardapio", "criar")]
        public async Task<IActionResult> CriarCategoria([FromBody] SalvarCardapioCategoriaRequest? request)
        {
            if (request == null)
            {
                return BadRequestErrorResponse("Corpo da requisicao e obrigatorio.");
            }

            if (!TryResolveCurrentEstabelecimentoAndModule("Cardapio", out var estabelecimentoId, out var error))
            {
                return error!;
            }

            try
            {
                var response = await _service.CriarCategoriaAsync(estabelecimentoId, request);
                return CreatedAtAction(nameof(ObterCategoria), new { id = response.Id }, ApiResponse<CardapioCategoriaDto>.Ok(response));
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

        [HttpPut("categorias/{id:guid}")]
        [RequirePermission("Cardapio", "editar")]
        public async Task<IActionResult> AtualizarCategoria(Guid id, [FromBody] SalvarCardapioCategoriaRequest? request)
        {
            if (request == null)
            {
                return BadRequestErrorResponse("Corpo da requisicao e obrigatorio.");
            }

            if (!TryResolveCurrentEstabelecimentoAndModule("Cardapio", out var estabelecimentoId, out var error))
            {
                return error!;
            }

            try
            {
                var response = await _service.AtualizarCategoriaAsync(estabelecimentoId, id, request);
                return Ok(ApiResponse<CardapioCategoriaDto>.Ok(response));
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

        [HttpPatch("categorias/{id:guid}/status")]
        [RequirePermission("Cardapio", "editar")]
        public async Task<IActionResult> AtualizarCategoriaStatus(Guid id, [FromBody] AtualizarStatusRequest? request)
        {
            if (request == null)
            {
                return BadRequestErrorResponse("Corpo da requisicao e obrigatorio.");
            }

            if (!TryResolveCurrentEstabelecimentoAndModule("Cardapio", out var estabelecimentoId, out var error))
            {
                return error!;
            }

            var updated = await _service.AtualizarCategoriaStatusAsync(estabelecimentoId, id, request.Ativo);
            return updated
                ? Ok(ApiResponse<object>.Ok(new { }))
                : NotFoundErrorResponse("Categoria nao encontrada.");
        }

        [HttpDelete("categorias/{id:guid}")]
        [RequirePermission("Cardapio", "excluir")]
        public async Task<IActionResult> ExcluirCategoria(Guid id)
        {
            if (!TryResolveCurrentEstabelecimentoAndModule("Cardapio", out var estabelecimentoId, out var error))
            {
                return error!;
            }

            try
            {
                var removed = await _service.ExcluirCategoriaAsync(estabelecimentoId, id);
                return removed
                    ? Ok(ApiResponse<object>.Ok(new { }))
                    : NotFoundErrorResponse("Categoria nao encontrada.");
            }
            catch (InvalidOperationException ex)
            {
                return ConflictErrorResponse(ex.Message);
            }
        }

        [HttpGet("grupos")]
        [RequirePermission("Cardapio", "visualizar")]
        public async Task<IActionResult> ListarGrupos(
            [FromQuery] string? busca = null,
            [FromQuery] bool? ativo = null,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20)
        {
            if (!TryResolveCurrentEstabelecimentoAndModule("Cardapio", out var estabelecimentoId, out var error))
            {
                return error!;
            }

            var response = await _service.ListarGruposAsync(estabelecimentoId, busca, ativo, page, pageSize);
            return Ok(ApiResponse<PagedResultDto<CardapioGrupoAdicionalDto>>.Ok(response));
        }

        [HttpGet("grupos/{id:guid}")]
        [RequirePermission("Cardapio", "visualizar")]
        public async Task<IActionResult> ObterGrupo(Guid id)
        {
            if (!TryResolveCurrentEstabelecimentoAndModule("Cardapio", out var estabelecimentoId, out var error))
            {
                return error!;
            }

            var response = await _service.ObterGrupoPorIdAsync(estabelecimentoId, id);
            return response == null
                ? NotFoundErrorResponse("Grupo nao encontrado.")
                : Ok(ApiResponse<CardapioGrupoAdicionalDto>.Ok(response));
        }

        [HttpPost("grupos")]
        [RequirePermission("Cardapio", "criar")]
        public async Task<IActionResult> CriarGrupo([FromBody] SalvarCardapioGrupoAdicionalRequest? request)
        {
            if (request == null)
            {
                return BadRequestErrorResponse("Corpo da requisicao e obrigatorio.");
            }

            if (!TryResolveCurrentEstabelecimentoAndModule("Cardapio", out var estabelecimentoId, out var error))
            {
                return error!;
            }

            try
            {
                var response = await _service.CriarGrupoAsync(estabelecimentoId, request);
                return CreatedAtAction(nameof(ObterGrupo), new { id = response.Id }, ApiResponse<CardapioGrupoAdicionalDto>.Ok(response));
            }
            catch (RequestValidationException ex)
            {
                return ValidationErrorResponse(ex);
            }
        }

        [HttpPut("grupos/{id:guid}")]
        [RequirePermission("Cardapio", "editar")]
        public async Task<IActionResult> AtualizarGrupo(Guid id, [FromBody] SalvarCardapioGrupoAdicionalRequest? request)
        {
            if (request == null)
            {
                return BadRequestErrorResponse("Corpo da requisicao e obrigatorio.");
            }

            if (!TryResolveCurrentEstabelecimentoAndModule("Cardapio", out var estabelecimentoId, out var error))
            {
                return error!;
            }

            try
            {
                var response = await _service.AtualizarGrupoAsync(estabelecimentoId, id, request);
                return Ok(ApiResponse<CardapioGrupoAdicionalDto>.Ok(response));
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

        [HttpPatch("grupos/{id:guid}/status")]
        [RequirePermission("Cardapio", "editar")]
        public async Task<IActionResult> AtualizarGrupoStatus(Guid id, [FromBody] AtualizarStatusRequest? request)
        {
            if (request == null)
            {
                return BadRequestErrorResponse("Corpo da requisicao e obrigatorio.");
            }

            if (!TryResolveCurrentEstabelecimentoAndModule("Cardapio", out var estabelecimentoId, out var error))
            {
                return error!;
            }

            var updated = await _service.AtualizarGrupoStatusAsync(estabelecimentoId, id, request.Ativo);
            return updated
                ? Ok(ApiResponse<object>.Ok(new { }))
                : NotFoundErrorResponse("Grupo nao encontrado.");
        }

        [HttpDelete("grupos/{id:guid}")]
        [RequirePermission("Cardapio", "excluir")]
        public async Task<IActionResult> ExcluirGrupo(Guid id)
        {
            if (!TryResolveCurrentEstabelecimentoAndModule("Cardapio", out var estabelecimentoId, out var error))
            {
                return error!;
            }

            try
            {
                var removed = await _service.ExcluirGrupoAsync(estabelecimentoId, id);
                return removed
                    ? Ok(ApiResponse<object>.Ok(new { }))
                    : NotFoundErrorResponse("Grupo nao encontrado.");
            }
            catch (InvalidOperationException ex)
            {
                return ConflictErrorResponse(ex.Message);
            }
        }

        [HttpGet("produtos")]
        [RequirePermission("Cardapio", "visualizar")]
        public async Task<IActionResult> ListarProdutos(
            [FromQuery] string? busca = null,
            [FromQuery] bool? ativo = null,
            [FromQuery] bool? destaque = null,
            [FromQuery] bool? disponivel = null,
            [FromQuery] Guid? categoriaId = null,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20)
        {
            if (!TryResolveCurrentEstabelecimentoAndModule("Cardapio", out var estabelecimentoId, out var error))
            {
                return error!;
            }

            var response = await _service.ListarProdutosAsync(estabelecimentoId, busca, ativo, destaque, disponivel, categoriaId, page, pageSize);
            return Ok(ApiResponse<PagedResultDto<CardapioProdutoDto>>.Ok(response));
        }

        [HttpGet("produtos/{id:guid}")]
        [RequirePermission("Cardapio", "visualizar")]
        public async Task<IActionResult> ObterProduto(Guid id)
        {
            if (!TryResolveCurrentEstabelecimentoAndModule("Cardapio", out var estabelecimentoId, out var error))
            {
                return error!;
            }

            var response = await _service.ObterProdutoPorIdAsync(estabelecimentoId, id);
            return response == null
                ? NotFoundErrorResponse("Produto nao encontrado.")
                : Ok(ApiResponse<CardapioProdutoDto>.Ok(response));
        }

        [HttpPost("produtos")]
        [RequirePermission("Cardapio", "criar")]
        public async Task<IActionResult> CriarProduto([FromBody] SalvarCardapioProdutoRequest? request)
        {
            if (request == null)
            {
                return BadRequestErrorResponse("Corpo da requisicao e obrigatorio.");
            }

            if (!TryResolveCurrentEstabelecimentoAndModule("Cardapio", out var estabelecimentoId, out var error))
            {
                return error!;
            }

            try
            {
                var response = await _service.CriarProdutoAsync(estabelecimentoId, request);
                return CreatedAtAction(nameof(ObterProduto), new { id = response.Id }, ApiResponse<CardapioProdutoDto>.Ok(response));
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

        [HttpPut("produtos/{id:guid}")]
        [RequirePermission("Cardapio", "editar")]
        public async Task<IActionResult> AtualizarProduto(Guid id, [FromBody] SalvarCardapioProdutoRequest? request)
        {
            if (request == null)
            {
                return BadRequestErrorResponse("Corpo da requisicao e obrigatorio.");
            }

            if (!TryResolveCurrentEstabelecimentoAndModule("Cardapio", out var estabelecimentoId, out var error))
            {
                return error!;
            }

            try
            {
                var response = await _service.AtualizarProdutoAsync(estabelecimentoId, id, request);
                return Ok(ApiResponse<CardapioProdutoDto>.Ok(response));
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

        [HttpPatch("produtos/{id:guid}/status")]
        [RequirePermission("Cardapio", "editar")]
        public async Task<IActionResult> AtualizarProdutoStatus(Guid id, [FromBody] AtualizarStatusRequest? request)
        {
            if (request == null)
            {
                return BadRequestErrorResponse("Corpo da requisicao e obrigatorio.");
            }

            if (!TryResolveCurrentEstabelecimentoAndModule("Cardapio", out var estabelecimentoId, out var error))
            {
                return error!;
            }

            var updated = await _service.AtualizarProdutoStatusAsync(estabelecimentoId, id, request.Ativo);
            return updated
                ? Ok(ApiResponse<object>.Ok(new { }))
                : NotFoundErrorResponse("Produto nao encontrado.");
        }

        [HttpPatch("produtos/{id:guid}/disponibilidade")]
        [RequirePermission("Cardapio", "editar")]
        public async Task<IActionResult> AtualizarProdutoDisponibilidade(Guid id, [FromBody] AtualizarDisponibilidadeCardapioRequest? request)
        {
            if (request == null)
            {
                return BadRequestErrorResponse("Corpo da requisicao e obrigatorio.");
            }

            if (!TryResolveCurrentEstabelecimentoAndModule("Cardapio", out var estabelecimentoId, out var error))
            {
                return error!;
            }

            var updated = await _service.AtualizarProdutoDisponibilidadeAsync(estabelecimentoId, id, request.Disponivel);
            return updated
                ? Ok(ApiResponse<object>.Ok(new { }))
                : NotFoundErrorResponse("Produto nao encontrado.");
        }

        [HttpDelete("produtos/{id:guid}")]
        [RequirePermission("Cardapio", "excluir")]
        public async Task<IActionResult> ExcluirProduto(Guid id)
        {
            if (!TryResolveCurrentEstabelecimentoAndModule("Cardapio", out var estabelecimentoId, out var error))
            {
                return error!;
            }

            var removed = await _service.ExcluirProdutoAsync(estabelecimentoId, id);
            return removed
                ? Ok(ApiResponse<object>.Ok(new { }))
                : NotFoundErrorResponse("Produto nao encontrado.");
        }
    }
}
