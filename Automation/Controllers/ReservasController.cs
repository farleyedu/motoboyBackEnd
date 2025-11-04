using System;
using System.Globalization;
using APIBack.Service.Interface;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Automation.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class ReservasController : ControllerBase
    {
        private readonly IReservasService _reservasService;

        public ReservasController(IReservasService reservasService)
        {
            _reservasService = reservasService;
        }

        /// <summary>
        /// GET: api/reservas?month=11&amp;year=2025&amp;estabelecimentoId=uuid
        /// Retorna reservas de restaurante agrupadas por dia.
        /// </summary>
        [HttpGet]
        public IActionResult GetReservasRestaurante(
            [FromQuery] int? month,
            [FromQuery] int? year,
            [FromQuery] Guid estabelecimentoId)
        {
            try
            {
                month ??= TryParseAlternateInt("mes");
                year ??= TryParseAlternateInt("ano");

                if (!month.HasValue || month < 1 || month > 12)
                {
                    return BadRequest(new
                    {
                        error = "Parametro 'month' (ou 'mes') deve ser um numero entre 1 e 12."
                    });
                }

                if (!year.HasValue || year < 1)
                {
                    return BadRequest(new
                    {
                        error = "Parametro 'year' (ou 'ano') deve ser um numero valido maior que zero."
                    });
                }

                if (estabelecimentoId == Guid.Empty)
                {
                    return BadRequest(new
                    {
                        error = "Parametro 'estabelecimentoId' e obrigatorio."
                    });
                }

                var reservas = _reservasService.GetReservasRestaurante(month.Value, year.Value, estabelecimentoId);
                return Ok(reservas);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Erro ao buscar reservas: {ex.Message}");
                return StatusCode(500, new
                {
                    error = "Erro ao consultar reservas."
                });
            }
        }

        /// <summary>
        /// GET: api/reservas/barbearia?month=11&amp;year=2025&amp;estabelecimentoId=uuid
        /// Retorna reservas de barbearia + lista de barbeiros.
        /// </summary>
        [HttpGet("barbearia")]
        public IActionResult GetReservasBarbearia(
            [FromQuery] int? month,
            [FromQuery] int? year,
            [FromQuery] Guid estabelecimentoId)
        {
            try
            {
                month ??= TryParseAlternateInt("mes");
                year ??= TryParseAlternateInt("ano");

                if (!month.HasValue || month < 1 || month > 12)
                {
                    return BadRequest(new
                    {
                        error = "Parametro 'month' (ou 'mes') deve ser um numero entre 1 e 12."
                    });
                }

                if (!year.HasValue || year < 1)
                {
                    return BadRequest(new
                    {
                        error = "Parametro 'year' (ou 'ano') deve ser um numero valido maior que zero."
                    });
                }

                if (estabelecimentoId == Guid.Empty)
                {
                    return BadRequest(new
                    {
                        error = "Parametro 'estabelecimentoId' e obrigatorio."
                    });
                }

                var resultado = _reservasService.GetReservasBarbearia(month.Value, year.Value, estabelecimentoId);
                return Ok(resultado);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Erro ao buscar agendamentos: {ex.Message}");
                return StatusCode(500, new
                {
                    error = "Erro ao consultar agendamentos ou barbeiros."
                });
            }
        }

        /// <summary>
        /// GET: api/reservas/metricas-dia?data=2025-11-05
        /// Retorna métricas de reservas confirmadas e total de pessoas para uma data específica.
        /// </summary>
        [HttpGet("metricas-dia")]
        public IActionResult GetMetricasDia([FromQuery] string data)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(data))
                {
                    return BadRequest(new
                    {
                        error = "Parametro 'data' e obrigatorio no formato yyyy-MM-dd."
                    });
                }

                if (!DateTime.TryParseExact(data, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dataConsulta))
                {
                    return BadRequest(new
                    {
                        error = "Parametro 'data' deve estar no formato yyyy-MM-dd."
                    });
                }

                var metricas = _reservasService.GetMetricasDia(dataConsulta);
                return Ok(metricas);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Erro ao calcular metricas diarias: {ex.Message}");
                return StatusCode(500, new
                {
                    error = "Erro ao consultar metricas do dia."
                });
            }
        }

        private int? TryParseAlternateInt(string queryKey)
        {
            if (Request?.Query.TryGetValue(queryKey, out var value) == true &&
                int.TryParse(value, out var parsed))
            {
                return parsed;
            }

            return null;
        }
    }
}
