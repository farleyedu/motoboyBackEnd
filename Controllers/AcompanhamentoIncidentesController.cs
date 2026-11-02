using System;
using System.Threading.Tasks;
using APIBack.Acompanhamento.Incidentes;
using APIBack.DTOs.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace APIBack.Controllers
{
    /// <summary>
    /// Falhas, incidentes compartilhados (3+ lojas) e avisos da equipe Zippy. Erros: 401 sem login, 403 sem Gestao, 404 inexistente,
    /// 409 repeticao/concorrencia, 422 dado invalido (sem destinatario, mensagem nao validada, prova ausente).
    /// O servico e montado aqui com o NpgsqlDataSource: nao ha registro novo no Program.cs, e sem servico de mensagens o envio fica pendente.
    /// </summary>
    [ApiController]
    public sealed class AcompanhamentoIncidentesController : CrmControllerBase
    {
        private readonly IIncidenteService _service;
        private readonly ILogger<AcompanhamentoIncidentesController> _logger;

        public AcompanhamentoIncidentesController(NpgsqlDataSource db, ILogger<AcompanhamentoIncidentesController> logger)
        {
            _service = new IncidenteService(db);
            _logger = logger;
        }

        [HttpGet("api/acompanhamento/incidentes")]
        public Task<IActionResult> Listar([FromQuery] Guid? lojaId) =>
            Executar(async ct => Ok(ApiResponse<IncidentesDto>.Ok(await _service.ListarAsync(lojaId, ct))));

        [HttpPost("api/acompanhamento/incidentes")]
        public Task<IActionResult> Registrar([FromBody] RegistrarFalhaRequest? request) => Executar(async ct =>
        {
            if (request == null) throw new IncidenteException(422, "FALHA_INVALIDA", "Envie a falha a registrar.");
            var falha = await _service.RegistrarAsync(request, CurrentUserId!.Value, ct);
            return StatusCode(StatusCodes.Status201Created, ApiResponse<FalhaDto>.Ok(falha));
        });

        [HttpGet("api/acompanhamento/avisos")]
        public Task<IActionResult> Avisos([FromQuery] bool somenteNaoLidos = false) =>
            Executar(async ct => Ok(ApiResponse<System.Collections.Generic.IReadOnlyList<AvisoDto>>.Ok(await _service.AvisosAsync(CurrentUserId!.Value, somenteNaoLidos, ct))));

        [HttpPost("api/acompanhamento/avisos/lidos")]
        public Task<IActionResult> Lidos([FromBody] LidosRequest? request) => Executar(async ct =>
        {
            var marcados = await _service.MarcarLidosAsync(request?.Ids ?? new(), CurrentUserId!.Value, ct);
            return Ok(ApiResponse<object>.Ok(new { marcados }));
        });

        [HttpPost("api/acompanhamento/incidentes/{id:guid}/avisar")]
        public Task<IActionResult> Avisar(Guid id, [FromBody] AvisarRequest? request) => Executar(async ct =>
        {
            if (request == null) throw new IncidenteException(422, "SEM_DESTINATARIO", "Informe destinatarios e mensagem.");
            return Ok(ApiResponse<AvisarResultadoDto>.Ok(await _service.AvisarAsync(id, request, CurrentUserId!.Value, ct)));
        });

        [HttpPost("api/acompanhamento/incidentes/{id:guid}/normalizou")]
        public Task<IActionResult> Normalizou(Guid id, [FromBody] NormalizouRequest? request) => Executar(async ct =>
            Ok(ApiResponse<IncidenteDto>.Ok(await _service.NormalizouAsync(id, request ?? new NormalizouRequest(), CurrentUserId!.Value, ct))));

        private async Task<IActionResult> Executar(Func<System.Threading.CancellationToken, Task<IActionResult>> acao)
        {
            var access = EnsureCrmAccess();
            if (access != null) return access;
            Response.Headers.CacheControl = "no-store";
            try
            {
                return await acao(HttpContext.RequestAborted);
            }
            catch (IncidenteException ex)
            {
                return StatusCode(ex.Status, ApiResponse<object>.Fail(ex.Message, ex.Code, ex.Details));
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
            {
                _logger.LogWarning("[acompanhamento.incidentes] ev=migracao_pendente tabela={T}", ex.TableName);
                return StatusCode(StatusCodes.Status422UnprocessableEntity,
                    ApiResponse<object>.Fail("Faltam migrações do acompanhamento neste banco.", "MIGRACAO_PENDENTE"));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Erro nos incidentes do acompanhamento");
                return StatusCode(StatusCodes.Status500InternalServerError, ApiResponse<object>.Fail("Erro ao processar incidentes."));
            }
        }
    }
}
