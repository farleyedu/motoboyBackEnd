using System;
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
        /// GET: api/reservas?month=11&year=2025&estabelecimentoId=uuid
        /// Retorna reservas de restaurante
        /// </summary>
        [HttpGet]
        public IActionResult GetReservasRestaurante(
            [FromQuery] int month,
            [FromQuery] int year,
            [FromQuery] Guid estabelecimentoId)
        {
            try
            {
                if (month < 1 || month > 12)
                {
                    return BadRequest(new
                    {
                        error = "Parâmetro 'month' deve ser um número entre 1 e 12."
                    });
                }

                if (year < 1)
                {
                    return BadRequest(new
                    {
                        error = "Parâmetro 'year' deve ser um número válido maior que zero."
                    });
                }

                if (estabelecimentoId == Guid.Empty)
                {
                    return BadRequest(new
                    {
                        error = "Parâmetro 'estabelecimentoId' é obrigatório."
                    });
                }

                var reservas = _reservasService.GetReservasRestaurante(month, year, estabelecimentoId);
                return Ok(reservas);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Erro ao buscar reservas: {ex.Message}");
                return StatusCode(500, new
                {
                    error = "Erro ao consultar reservas."
                });
            }
        }

        /// <summary>
        /// GET: api/reservas/barbearia?month=11&year=2025&estabelecimentoId=uuid
        /// Retorna reservas de barbearia + lista de barbeiros
        /// </summary>
        [HttpGet("barbearia")]
        public IActionResult GetReservasBarbearia(
            [FromQuery] int month,
            [FromQuery] int year,
            [FromQuery] Guid estabelecimentoId)
        {
            try
            {
                if (month < 1 || month > 12)
                {
                    return BadRequest(new
                    {
                        error = "Parâmetro 'month' deve ser um número entre 1 e 12."
                    });
                }

                if (year < 1)
                {
                    return BadRequest(new
                    {
                        error = "Parâmetro 'year' deve ser um número válido maior que zero."
                    });
                }

                if (estabelecimentoId == Guid.Empty)
                {
                    return BadRequest(new
                    {
                        error = "Parâmetro 'estabelecimentoId' é obrigatório."
                    });
                }

                var resultado = _reservasService.GetReservasBarbearia(month, year, estabelecimentoId);
                return Ok(resultado);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Erro ao buscar agendamentos: {ex.Message}");
                return StatusCode(500, new
                {
                    error = "Erro ao consultar agendamentos ou barbeiros."
                });
            }
        }
    }
}

