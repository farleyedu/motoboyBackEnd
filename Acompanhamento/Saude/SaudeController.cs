using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.Controllers;
using APIBack.DTOs.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace APIBack.Acompanhamento.Saude
{
    public sealed class SalvarRegrasRequest
    {
        /// <summary>chave -> valor inteiro; nulo so para regra anulavel (escalonar_sem_pagamento_dias).</summary>
        public Dictionary<string, int?> Regras { get; set; } = new();
        /// <summary>Versao das regras que a tela mostrava. Diferente da atual = 409.</summary>
        public int? VersaoEsperada { get; set; }
        public string? Motivo { get; set; }
    }

    /// <summary>
    /// Quadro, regras de saude e fim do teste (so a equipe Zippy: super admin). Erros: 401 sem login, 403 sem permissao,
    /// 404 loja inexistente, 409 regras mudaram, 422 regra ou prévia invalida (details lista o que corrigir).
    /// O servico e montado aqui com o NpgsqlDataSource: nao ha registro novo no Program.cs.
    /// </summary>
    [ApiController]
    public sealed class SaudeController : CrmControllerBase
    {
        private readonly ISaudeService _service;
        private readonly ILogger<SaudeController> _logger;

        public SaudeController(NpgsqlDataSource db, ILogger<SaudeController> logger)
        {
            _service = new SaudeService(db);
            _logger = logger;
        }

        [HttpGet("api/acompanhamento/quadro")]
        public Task<IActionResult> Quadro() => Executar(async ct => Ok(ApiResponse<QuadroDto>.Ok(await _service.QuadroAsync(ct))));

        /// <summary>Sem parametro: as regras e o historico. Com ?previa={json}: as lojas que mudariam de faixa, sem gravar nada.</summary>
        [HttpGet("api/acompanhamento/regras")]
        public Task<IActionResult> Regras([FromQuery] string? previa) => Executar(async ct =>
        {
            if (string.IsNullOrWhiteSpace(previa)) return Ok(ApiResponse<RegrasDto>.Ok(await _service.RegrasAsync(ct)));
            return Ok(ApiResponse<PreviaDto>.Ok(await _service.PreviaAsync(SaudeService.LerMudancas(previa), ct)));
        });

        [HttpPut("api/acompanhamento/regras")]
        public Task<IActionResult> SalvarRegras([FromBody] SalvarRegrasRequest? request) => Executar(async ct =>
        {
            if (request == null) throw new SaudeException(422, "REGRAS_INVALIDAS", "Envie as regras a mudar.");
            var regras = await _service.SalvarRegrasAsync(request.Regras, request.VersaoEsperada, request.Motivo, CurrentUserId!.Value, ct);
            return Ok(ApiResponse<RegrasDto>.Ok(regras));
        });

        [HttpGet("api/acompanhamento/lojas/{id:guid}/fim-de-teste")]
        public Task<IActionResult> FimDeTeste(Guid id) => Executar(async ct => Ok(ApiResponse<FimDeTesteDto>.Ok(await _service.FimDeTesteAsync(id, ct))));

        private async Task<IActionResult> Executar(Func<System.Threading.CancellationToken, Task<IActionResult>> acao)
        {
            var access = EnsureCrmAccess();
            if (access != null) return access;
            Response.Headers.CacheControl = "no-store";
            try
            {
                return await acao(HttpContext.RequestAborted);
            }
            catch (SaudeException ex)
            {
                return StatusCode(ex.Status, ApiResponse<object>.Fail(ex.Message, ex.Code, ex.Details));
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
            {
                _logger.LogWarning("[acompanhamento.saude] ev=migracao_pendente tabela={T}", ex.TableName);
                return StatusCode(StatusCodes.Status422UnprocessableEntity,
                    ApiResponse<object>.Fail("Faltam migrações do acompanhamento neste banco.", "MIGRACAO_PENDENTE"));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Erro na saude do acompanhamento");
                return StatusCode(StatusCodes.Status500InternalServerError, ApiResponse<object>.Fail("Erro ao calcular a saúde."));
            }
        }
    }
}
