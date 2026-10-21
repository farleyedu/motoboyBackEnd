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
        private readonly ISituacaoComercialService _situacoes;

        public PublicCardapioSnapshotController(ICardapioContractService service, ISituacaoComercialService situacoes)
        {
            _service = service;
            _situacoes = situacoes;
        }

        [HttpGet("{estabelecimentoId:guid}/publico")]
        public async Task<IActionResult> ObterSnapshotPublico(Guid estabelecimentoId)
        {
            try
            {
                var situacao = await _situacoes.ObterAsync(estabelecimentoId);
                if (situacao?.Situacao is SituacoesComerciais.Bloqueado or SituacoesComerciais.Implantacao)
                    return NotFoundErrorResponse("Cardapio indisponivel no momento.");
                if (!await _situacoes.TemModuloAsync(estabelecimentoId, "CARDAPIOWEB"))
                    return NotFoundErrorResponse("Cardapio indisponivel no momento.");
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
