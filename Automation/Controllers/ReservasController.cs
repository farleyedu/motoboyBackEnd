using System;
using System.Threading.Tasks;
using APIBack.DTOs;
using APIBack.Service;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers
{
    /// <summary>
    /// Controller para gerenciamento de reservas
    /// </summary>
    [Route("api/[controller]")]
    [ApiController]
    public class ReservaController : ControllerBase
    {
        private readonly ReservaService _reservaService;

        public ReservaController(ReservaService reservaService)
        {
            _reservaService = reservaService;
        }

        // ========================================================================
        // VERIFICAR DISPONIBILIDADE
        // ========================================================================

        /// <summary>
        /// Verifica disponibilidade para uma reserva
        /// Valida: dia da semana (terça fechada), quantidade mínima (10), capacidade
        /// </summary>
        /// <param name="dto">Dados para verificação</param>
        /// <returns>Informação de disponibilidade</returns>
        [HttpPost("verificar-disponibilidade")]
        [ProducesResponseType(typeof(DisponibilidadeResponseDTO), 200)]
        [ProducesResponseType(400)]
        public async Task<IActionResult> VerificarDisponibilidade([FromBody] VerificarDisponibilidadeDTO dto)
        {
            try
            {
                // Validação básica do modelo
                if (!ModelState.IsValid)
                {
                    return BadRequest(new
                    {
                        success = false,
                        error = "Dados inválidos",
                        details = ModelState,
                        traceId = HttpContext.TraceIdentifier
                    });
                }

                // Verifica disponibilidade (inclui todas as validações)
                var (disponivel, mensagem) = await _reservaService.BuscarDisponibilidadeAsync(
                    dto.IdEstabelecimento,
                    dto.DataReserva,
                    dto.QtdPessoas
                );

                if (!disponivel)
                {
                    return Ok(new DisponibilidadeResponseDTO
                    {
                        Disponivel = false,
                        Mensagem = mensagem,
                        PessoasDisponiveis = null,
                        PessoasOcupadas = null,
                        CapacidadeTotal = null
                    });
                }

                // TODO: Buscar informações detalhadas de capacidade
                return Ok(new DisponibilidadeResponseDTO
                {
                    Disponivel = true,
                    Mensagem = mensagem,
                    PessoasDisponiveis = null, // Calcular se necessário
                    PessoasOcupadas = null,
                    CapacidadeTotal = null
                });
            }
            catch (Exception ex)
            {
                // Log do erro (implementar logging adequado)
                Console.WriteLine($"Erro ao verificar disponibilidade: {ex.Message}");

                return StatusCode(500, new
                {
                    success = false,
                    error = "Erro interno ao verificar disponibilidade",
                    traceId = HttpContext.TraceIdentifier
                });
            }
        }

        // ========================================================================
        // CRIAR RESERVA
        // ========================================================================

        /// <summary>
        /// Cria uma nova reserva
        /// Valida todas as regras antes de criar
        /// </summary>
        /// <param name="dto">Dados da reserva</param>
        /// <returns>Reserva criada</returns>
        [HttpPost]
        [ProducesResponseType(typeof(ReservaResponseDTO), 201)]
        [ProducesResponseType(400)]
        public async Task<IActionResult> CriarReserva([FromBody] CriarReservaDTO dto)
        {
            try
            {
                // Validação do modelo
                if (!ModelState.IsValid)
                {
                    return BadRequest(new
                    {
                        success = false,
                        error = "Dados inválidos",
                        details = ModelState,
                        traceId = HttpContext.TraceIdentifier
                    });
                }

                // ===== VALIDAÇÃO 1: Verificar capacidade (inclui todas validações) =====
                var (disponivel, mensagem) = await _reservaService.VerificarCapacidadeDiaAsync(
                    dto.IdEstabelecimento,
                    dto.DataReserva,
                    dto.QtdPessoas
                );

                if (!disponivel)
                {
                    return BadRequest(new
                    {
                        success = false,
                        error = mensagem,
                        traceId = HttpContext.TraceIdentifier
                    });
                }

                // ===== VALIDAÇÃO 2: Horário de chegada =====
                // TODO: Adicionar validação de horário de chegada
                // Seg a Sex: 17h às 20h
                // Sáb e Dom: 12h às 20h

                // ===== CRIAR RESERVA =====
                // TODO: Implementar lógica de criação no repository
                // var reserva = await _reservaService.CriarReservaAsync(dto);

                // Exemplo de resposta (substituir por lógica real)
                return StatusCode(201, new
                {
                    success = true,
                    message = "Reserva criada com sucesso",
                    data = new
                    {
                        id = 0, // TODO: ID real
                        codigoReserva = "RES-001", // TODO: Código real
                        nomeCliente = dto.NomeClienteReserva,
                        qtdPessoas = dto.QtdPessoas,
                        dataReserva = dto.DataReserva.ToString("yyyy-MM-dd"),
                        horaInicio = dto.HoraInicio,
                        isAniversariante = dto.IsAniversariante ?? false,
                        status = "confirmada"
                    },
                    traceId = HttpContext.TraceIdentifier
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Erro ao criar reserva: {ex.Message}");

                return StatusCode(500, new
                {
                    success = false,
                    error = "Erro interno ao criar reserva",
                    traceId = HttpContext.TraceIdentifier
                });
            }
        }

        // ========================================================================
        // ATUALIZAR RESERVA
        // ========================================================================

        /// <summary>
        /// Atualiza uma reserva existente
        /// Valida regras se data ou quantidade forem alteradas
        /// </summary>
        /// <param name="id">ID da reserva</param>
        /// <param name="dto">Dados a serem atualizados</param>
        /// <returns>Reserva atualizada</returns>
        [HttpPut("{id}")]
        [ProducesResponseType(typeof(ReservaResponseDTO), 200)]
        [ProducesResponseType(404)]
        [ProducesResponseType(400)]
        public async Task<IActionResult> AtualizarReserva(int id, [FromBody] AtualizarReservaDTO dto)
        {
            try
            {
                // Validação do modelo
                if (!ModelState.IsValid)
                {
                    return BadRequest(new
                    {
                        success = false,
                        error = "Dados inválidos",
                        details = ModelState,
                        traceId = HttpContext.TraceIdentifier
                    });
                }

                // Verificar se ID do body corresponde ao da URL
                if (dto.Id != id)
                {
                    return BadRequest(new
                    {
                        success = false,
                        error = "ID da reserva não corresponde ao informado na URL",
                        traceId = HttpContext.TraceIdentifier
                    });
                }

                // TODO: Buscar reserva existente
                // var reservaExistente = await _reservaService.BuscarPorIdAsync(id);
                // if (reservaExistente == null)
                //     return NotFound(...);

                // ===== VALIDAR SE DATA OU QUANTIDADE MUDARAM =====
                bool validarCapacidade = dto.DataReserva.HasValue || dto.QtdPessoas.HasValue;

                if (validarCapacidade)
                {
                    // Usar nova data ou manter existente
                    // var dataFinal = dto.DataReserva ?? reservaExistente.DataReserva;
                    // var qtdFinal = dto.QtdPessoas ?? reservaExistente.QtdPessoas;

                    // var (disponivel, mensagem) = await _reservaService.VerificarCapacidadeDiaAsync(
                    //     reservaExistente.IdEstabelecimento,
                    //     dataFinal,
                    //     qtdFinal
                    // );

                    // if (!disponivel)
                    // {
                    //     return BadRequest(new { success = false, error = mensagem });
                    // }
                }

                // TODO: Atualizar reserva no repository
                // var reservaAtualizada = await _reservaService.AtualizarReservaAsync(id, dto);

                return Ok(new
                {
                    success = true,
                    message = "Reserva atualizada com sucesso",
                    data = new { } // TODO: Retornar reserva atualizada
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Erro ao atualizar reserva: {ex.Message}");

                return StatusCode(500, new
                {
                    success = false,
                    error = "Erro interno ao atualizar reserva",
                    traceId = HttpContext.TraceIdentifier
                });
            }
        }

        // ========================================================================
        // LISTAR RESERVAS
        // ========================================================================

        /// <summary>
        /// Lista reservas com filtros opcionais
        /// </summary>
        /// <param name="dataInicio">Data inicial (opcional)</param>
        /// <param name="dataFim">Data final (opcional)</param>
        /// <param name="status">Status (opcional)</param>
        /// <returns>Lista de reservas</returns>
        [HttpGet]
        [ProducesResponseType(typeof(ReservaListagemDTO[]), 200)]
        public async Task<IActionResult> ListarReservas(
            [FromQuery] DateTime? dataInicio,
            [FromQuery] DateTime? dataFim,
            [FromQuery] string status)
        {
            try
            {
                // TODO: Implementar listagem no service/repository
                // var reservas = await _reservaService.ListarReservasAsync(dataInicio, dataFim, status);

                return Ok(new
                {
                    success = true,
                    data = new ReservaListagemDTO[] { }, // TODO: Retornar lista real
                    traceId = HttpContext.TraceIdentifier
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Erro ao listar reservas: {ex.Message}");

                return StatusCode(500, new
                {
                    success = false,
                    error = "Erro interno ao listar reservas",
                    traceId = HttpContext.TraceIdentifier
                });
            }
        }

        // ========================================================================
        // BUSCAR RESERVA POR ID
        // ========================================================================

        /// <summary>
        /// Busca uma reserva específica por ID
        /// </summary>
        /// <param name="id">ID da reserva</param>
        /// <returns>Dados da reserva</returns>
        [HttpGet("{id}")]
        [ProducesResponseType(typeof(ReservaResponseDTO), 200)]
        [ProducesResponseType(404)]
        public async Task<IActionResult> BuscarReservaPorId(int id)
        {
            try
            {
                // TODO: Implementar busca no service/repository
                // var reserva = await _reservaService.BuscarPorIdAsync(id);

                // if (reserva == null)
                // {
                //     return NotFound(new
                //     {
                //         success = false,
                //         error = $"Reserva com ID {id} não encontrada",
                //         traceId = HttpContext.TraceIdentifier
                //     });
                // }

                return Ok(new
                {
                    success = true,
                    data = new { }, // TODO: Retornar reserva encontrada
                    traceId = HttpContext.TraceIdentifier
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Erro ao buscar reserva: {ex.Message}");

                return StatusCode(500, new
                {
                    success = false,
                    error = "Erro interno ao buscar reserva",
                    traceId = HttpContext.TraceIdentifier
                });
            }
        }

        // ========================================================================
        // CANCELAR RESERVA
        // ========================================================================

        /// <summary>
        /// Cancela uma reserva existente
        /// </summary>
        /// <param name="id">ID da reserva a ser cancelada</param>
        /// <returns>Confirmação de cancelamento</returns>
        [HttpDelete("{id}")]
        [ProducesResponseType(200)]
        [ProducesResponseType(404)]
        public async Task<IActionResult> CancelarReserva(int id)
        {
            try
            {
                // TODO: Implementar cancelamento no service/repository
                // var resultado = await _reservaService.CancelarReservaAsync(id);

                // if (!resultado)
                // {
                //     return NotFound(new
                //     {
                //         success = false,
                //         error = $"Reserva com ID {id} não encontrada",
                //         traceId = HttpContext.TraceIdentifier
                //     });
                // }

                return Ok(new
                {
                    success = true,
                    message = "Reserva cancelada com sucesso",
                    traceId = HttpContext.TraceIdentifier
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Erro ao cancelar reserva: {ex.Message}");

                return StatusCode(500, new
                {
                    success = false,
                    error = "Erro interno ao cancelar reserva",
                    traceId = HttpContext.TraceIdentifier
                });
            }
        }
    }
}