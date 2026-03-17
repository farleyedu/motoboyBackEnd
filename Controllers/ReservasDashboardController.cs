using System;
using APIBack.Attributes;
using APIBack.Extensions;
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
        [RequirePermission("Reservas", "visualizar")]
        public IActionResult ListarReservasRestaurante([FromQuery] int month, [FromQuery] int year, [FromQuery] Guid estabelecimentoId)
        {
            if (!MesAnoValidos(month, year) || estabelecimentoId == Guid.Empty)
            {
                return BadRequest(new { error = "Parametros invalidos. Informe month (1-12), year (> 2000) e estabelecimentoId valido." });
            }

            if (!TryValidarEscopoEstabelecimento(estabelecimentoId, out var escopoErro))
            {
                return escopoErro!;
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
        [RequirePermission("Agendamentos", "visualizar")]
        public IActionResult ListarReservasBarbearia([FromQuery] int month, [FromQuery] int year, [FromQuery] Guid estabelecimentoId)
        {
            if (!MesAnoValidos(month, year) || estabelecimentoId == Guid.Empty)
            {
                return BadRequest(new { error = "Parametros invalidos. Informe month (1-12), year (> 2000) e estabelecimentoId valido." });
            }

            if (!TryValidarEscopoEstabelecimento(estabelecimentoId, out var escopoErro))
            {
                return escopoErro!;
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
        [RequirePermission("Relatorios", "ver_metricas")]
        public IActionResult ObterMetricasDia([FromQuery] DateTime data, [FromQuery] Guid estabelecimentoId)
        {
            if (estabelecimentoId == Guid.Empty || data == default)
            {
                return BadRequest(new { error = "Parametros invalidos. Informe data e estabelecimentoId validos." });
            }

            if (!TryValidarEscopoEstabelecimento(estabelecimentoId, out var escopoErro))
            {
                return escopoErro!;
            }

            try
            {
                var metricas = _reservasService.GetMetricasDia(data, estabelecimentoId);
                return Ok(metricas);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao buscar metricas do dia {Data} (estabelecimento={EstabelecimentoId})", data, estabelecimentoId);
                return StatusCode(500, new { error = "Erro interno ao buscar metricas do dia." });
            }
        }

        [HttpGet("metricas-mes")]
        [RequirePermission("Relatorios", "ver_metricas")]
        public IActionResult ObterMetricasMes([FromQuery] int month, [FromQuery] int year, [FromQuery] Guid estabelecimentoId)
        {
            if (!MesAnoValidos(month, year) || estabelecimentoId == Guid.Empty)
            {
                return BadRequest(new { error = "Parametros invalidos. Informe month (1-12), year (> 2000) e estabelecimentoId valido." });
            }

            if (!TryValidarEscopoEstabelecimento(estabelecimentoId, out var escopoErro))
            {
                return escopoErro!;
            }

            try
            {
                var metricas = _reservasService.GetMetricasMes(month, year, estabelecimentoId);
                return Ok(metricas);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao buscar metricas mensais (month={Month}, year={Year}, estabelecimento={EstabelecimentoId})", month, year, estabelecimentoId);
                return StatusCode(500, new { error = "Erro interno ao buscar metricas do mes." });
            }
        }

        [HttpGet("metricas-periodo")]
        [RequirePermission("Relatorios", "ver_metricas")]
        public IActionResult ObterMetricasPeriodo(
            [FromQuery] DateTime dataInicio,
            [FromQuery] DateTime dataFim,
            [FromQuery] Guid estabelecimentoId,
            [FromQuery] long? barbeiroId = null)
        {
            if (estabelecimentoId == Guid.Empty || dataInicio == default || dataFim == default)
            {
                return BadRequest(new { error = "Parametros invalidos. Informe dataInicio, dataFim e estabelecimentoId validos." });
            }

            if (dataFim < dataInicio)
            {
                return BadRequest(new { error = "dataFim nao pode ser anterior a dataInicio." });
            }

            if (!TryValidarEscopoEstabelecimento(estabelecimentoId, out var escopoErro))
            {
                return escopoErro!;
            }

            try
            {
                var metricas = _reservasService.GetMetricasPeriodo(dataInicio, dataFim, estabelecimentoId, barbeiroId);
                return Ok(metricas);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao buscar metricas do periodo {Inicio} - {Fim} (estabelecimento={EstabelecimentoId}, barbeiro={BarbeiroId})", dataInicio, dataFim, estabelecimentoId, barbeiroId);
                return StatusCode(500, new { error = "Erro interno ao buscar metricas do periodo." });
            }
        }

        private static bool MesAnoValidos(int month, int year)
        {
            return month is >= 1 and <= 12 && year >= 2000;
        }

        private bool TryValidarEscopoEstabelecimento(Guid estabelecimentoId, out IActionResult? erro)
        {
            erro = null;

            if (HttpContext.IsSuperAdmin())
            {
                return true;
            }

            var estabelecimentoToken = HttpContext.GetEstabelecimentoId();
            if (!estabelecimentoToken.HasValue)
            {
                erro = BadRequest(new { error = "Selecione um estabelecimento para consultar o dashboard." });
                return false;
            }

            if (estabelecimentoToken.Value != estabelecimentoId)
            {
                erro = StatusCode(403, new { error = "Acesso negado para este estabelecimento." });
                return false;
            }

            return true;
        }
    }
}
