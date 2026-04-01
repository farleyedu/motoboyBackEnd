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
    [Route("api/cardapio")]
    [AllowAnonymous]
    public class PublicCardapioSnapshotController : ApiControllerBase
    {
        private readonly ICardapioContractService _service;

        public PublicCardapioSnapshotController(ICardapioContractService service)
        {
            _service = service;
        }

        [HttpGet("{estabelecimentoId:guid}/publico")]
        public async Task<IActionResult> ObterSnapshotPublico(Guid estabelecimentoId)
        {
            try
            {
                var response = await _service.ObterSnapshotPublicoAsync(estabelecimentoId);
                return Ok(ApiResponse<CardapioPublicoSnapshotDto>.Ok(response));
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
    }
}
