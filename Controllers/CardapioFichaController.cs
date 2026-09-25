using System;
using System.Threading.Tasks;
using APIBack.Attributes;
using APIBack.DTOs.Cardapio;
using APIBack.DTOs.Common;
using APIBack.Service;
using APIBack.Service.Interface;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers
{
    /// <summary>
    /// Ficha de atendimento (leitura unica do cardapio para quem monta pedido) e os campos de
    /// atendimento de cada produto. O estabelecimento da ficha vem sempre do token.
    /// </summary>
    [ApiController]
    public class CardapioFichaController : EstabelecimentoScopedControllerBase
    {
        private readonly ICardapioFichaService _service;
        private readonly ICardapioContractService _cardapio;

        public CardapioFichaController(ICardapioFichaService service, ICardapioContractService cardapio)
        {
            _service = service;
            _cardapio = cardapio;
        }

        [HttpGet("api/v2/cardapio/ficha")]
        [RequirePermission("Delivery", "criar_pedido")]
        public async Task<IActionResult> GetFicha()
        {
            if (!TryResolveCurrentEstabelecimento(out var estabelecimentoId, out var error)) return error!;
            return await RunAsync(async () => Ok(ApiResponse<FichaAtendimentoDto>.Ok(await _service.GetAsync(estabelecimentoId))));
        }

        [HttpGet("api/cardapio/{estabelecimentoId:guid}/produtos/{produtoId:guid}/atendimento")]
        [RequirePermission("Cardapio", "visualizar")]
        public async Task<IActionResult> GetAtendimento(Guid estabelecimentoId, Guid produtoId)
        {
            var error = await ValidateScopeAsync(estabelecimentoId);
            if (error != null) return error;
            return await RunAsync(async () => Ok(ApiResponse<ProdutoAtendimentoDto>.Ok(await _service.GetAtendimentoAsync(estabelecimentoId, produtoId))));
        }

        [HttpPut("api/cardapio/{estabelecimentoId:guid}/produtos/{produtoId:guid}/atendimento")]
        [RequirePermission("Cardapio", "editar")]
        public async Task<IActionResult> SaveAtendimento(Guid estabelecimentoId, Guid produtoId, [FromBody] ProdutoAtendimentoDto? request)
        {
            var error = await ValidateScopeAsync(estabelecimentoId);
            if (error != null) return error;
            return await RunAsync(async () => Ok(ApiResponse<ProdutoAtendimentoDto>.Ok(
                await _service.SaveAtendimentoAsync(estabelecimentoId, produtoId, request!))));
        }

        private async Task<IActionResult?> ValidateScopeAsync(Guid estabelecimentoId)
        {
            if (!CurrentUserId.HasValue || CurrentUserId.Value <= 0) return UnauthorizedResponse();
            if (!CurrentIsSuperAdmin)
            {
                if (!CurrentEstabelecimentoId.HasValue || CurrentEstabelecimentoId.Value == Guid.Empty)
                {
                    return Unauthorized(ApiResponse<object>.Fail("Sessao invalida para o estabelecimento atual."));
                }
                if (CurrentEstabelecimentoId.Value != estabelecimentoId) return ForbiddenResponse();
            }
            if (!await _cardapio.EstabelecimentoTemModuloAtivoAsync(estabelecimentoId, "Cardapio"))
            {
                return StatusCode(403, ApiResponse<object>.Fail("Modulo inativo para o estabelecimento informado."));
            }
            return null;
        }

        private async Task<IActionResult> RunAsync(Func<Task<IActionResult>> action)
        {
            try
            {
                return await action();
            }
            catch (DeliveryDomainException ex)
            {
                return StatusCode(ex.StatusCode, ApiResponse<object>.Fail(ex.Message, ex.Code, ex.Details));
            }
        }
    }
}
