using System;
using APIBack.Service.Interface;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace APIBack.Controllers
{
    [ApiController]
    [Route("api/reservas")]
    public class ReservasDashboardController : ControllerBase
    {
        private readonly IReservasService _reservasService;
        private readonly ILogger<ReservasDashboardController> _logger;

        public ReservasDashboardController(
            IReservasService reservasService,
            ILogger<ReservasDashboardController> logger)
        {
            _reservasService = reservasService;
            _logger = logger;
        }

        [HttpGet]
        public IActionResult ListarReservasRestaurante([FromQuery] int month, [FromQuery] int year, [FromQuery] Guid estabelecimentoId)
        {
            if (!MesAnoValidos(month, year) || estabelecimentoId == Guid.Empty)
            {
                return BadRequest(new { error = "Parâmetros inválidos. Informe month (1-12), year (> 2000) e estabelecimentoId válido." });
            }

            try
            {
                var resultado = _reservasService.GetReservasRestaurante(month, year, estabelecimentoId);
                return Ok(resultado);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao listar reservas de restaurante (month={Month}, year={Year}, estabelecimento={EstabelecimentoId})", month, year, estabelecimentoId);
                return StatusCode(500, new { error = "Erro interno ao listar reservas." });
            }
        }

        [HttpGet("barbearia")]
        public IActionResult ListarReservasBarbearia([FromQuery] int month, [FromQuery] int year, [FromQuery] Guid estabelecimentoId)
        {
            if (!MesAnoValidos(month, year) || estabelecimentoId == Guid.Empty)
            {
                return BadRequest(new { error = "Parâmetros inválidos. Informe month (1-12), year (> 2000) e estabelecimentoId válido." });
            }

            try
            {
                var resultado = _reservasService.GetReservasBarbearia(month, year, estabelecimentoId);
                return Ok(resultado);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao listar reservas de barbearia (month={Month}, year={Year}, estabelecimento={EstabelecimentoId})", month, year, estabelecimentoId);
                return StatusCode(500, new { error = "Erro interno ao listar reservas da barbearia." });
            }
        }

        [HttpGet("metricas-dia")]
        public IActionResult ObterMetricasDia([FromQuery] DateTime data, [FromQuery] Guid estabelecimentoId)
        {
            if (estabelecimentoId == Guid.Empty || data == default)
            {
                return BadRequest(new { error = "Parâmetros inválidos. Informe data e estabelecimentoId válidos." });
            }

            try
            {
                var metricas = _reservasService.GetMetricasDia(data, estabelecimentoId);
                return Ok(metricas);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao buscar métricas do dia {Data} (estabelecimento={EstabelecimentoId})", data, estabelecimentoId);
                return StatusCode(500, new { error = "Erro interno ao buscar métricas do dia." });
            }
        }

        [HttpGet("metricas-mes")]
        public IActionResult ObterMetricasMes([FromQuery] int month, [FromQuery] int year, [FromQuery] Guid estabelecimentoId)
        {
            if (!MesAnoValidos(month, year) || estabelecimentoId == Guid.Empty)
            {
                return BadRequest(new { error = "Parâmetros inválidos. Informe month (1-12), year (> 2000) e estabelecimentoId válido." });
            }

            try
            {
                var metricas = _reservasService.GetMetricasMes(month, year, estabelecimentoId);
                return Ok(metricas);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao buscar métricas mensais (month={Month}, year={Year}, estabelecimento={EstabelecimentoId})", month, year, estabelecimentoId);
                return StatusCode(500, new { error = "Erro interno ao buscar métricas do mês." });
            }
        }

        [HttpGet("metricas-periodo")]
        public IActionResult ObterMetricasPeriodo(
            [FromQuery] DateTime dataInicio,
            [FromQuery] DateTime dataFim,
            [FromQuery] Guid estabelecimentoId,
            [FromQuery] long? barbeiroId = null)
        {
            if (estabelecimentoId == Guid.Empty || dataInicio == default || dataFim == default)
            {
                return BadRequest(new { error = "Parâmetros inválidos. Informe dataInicio, dataFim e estabelecimentoId válidos." });
            }

            if (dataFim < dataInicio)
            {
                return BadRequest(new { error = "dataFim não pode ser anterior a dataInicio." });
            }

            try
            {
                var metricas = _reservasService.GetMetricasPeriodo(dataInicio, dataFim, estabelecimentoId, barbeiroId);
                return Ok(metricas);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao buscar métricas do período {Inicio} - {Fim} (estabelecimento={EstabelecimentoId}, barbeiro={BarbeiroId})", dataInicio, dataFim, estabelecimentoId, barbeiroId);
                return StatusCode(500, new { error = "Erro interno ao buscar métricas do período." });
            }
        }

        private static bool MesAnoValidos(int month, int year)
        {
            return month is >= 1 and <= 12 && year >= 2000;
        }
    }
}
