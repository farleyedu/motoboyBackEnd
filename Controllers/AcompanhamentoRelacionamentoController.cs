using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.Acompanhamento.Fases;
using APIBack.Acompanhamento.Relacionamento;
using APIBack.DTOs.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace APIBack.Controllers
{
    /// <summary>
    /// Carteira da equipe, responsavel, proximo passo, marcacao, cobranca ao responsavel, retencao e saida (so a equipe Zippy: super admin).
    /// {id} aceita o id do acompanhamento, da oportunidade ou da loja. Erros: 401 sem login, 403 sem permissao, 404 inexistente,
    /// 409 concorrencia/repeticao (VERSAO_MUDOU, COBRANCA_RECENTE, MARCACAO_JA_ATIVA), 422 dado invalido (details traz as opcoes validas).
    /// O servico e montado aqui com o NpgsqlDataSource: sem registro novo no Program.cs.
    /// </summary>
    [ApiController]
    public sealed class AcompanhamentoRelacionamentoController : CrmControllerBase
    {
        private readonly IRelacionamentoService _service;
        private readonly ILogger<AcompanhamentoRelacionamentoController> _logger;

        public AcompanhamentoRelacionamentoController(NpgsqlDataSource db, IAcompanhamentoStore acompanhamento, ILogger<AcompanhamentoRelacionamentoController> logger)
        {
            _service = new RelacionamentoService(new RelacionamentoPostgresStore(db, acompanhamento));
            _logger = logger;
        }

        [HttpGet("api/acompanhamento/equipe")]
        public Task<IActionResult> Equipe() =>
            Executar(async ct => Ok(ApiResponse<EquipeDto>.Ok(await _service.EquipeAsync(CurrentUserId!.Value, ct))));

        [HttpGet("api/acompanhamento/lojas/{id:guid}/relacionamento")]
        public Task<IActionResult> Obter(Guid id) =>
            Executar(async ct => Ok(ApiResponse<RelacionamentoDto>.Ok(await _service.ObterAsync(id, ct))));

        [HttpPost("api/acompanhamento/lojas/passar")]
        public Task<IActionResult> Passar([FromBody] PassarRequest? request) => Executar(async ct =>
        {
            if (request == null) throw new RelacionamentoException(422, "LOTE_VAZIO", "Informe as lojas e o novo responsavel.");
            return Ok(ApiResponse<PassarResultadoDto>.Ok(await _service.PassarAsync(request, CurrentUserId!.Value, ct)));
        });

        [HttpPost("api/acompanhamento/lojas/{id:guid}/assumir")]
        public Task<IActionResult> Assumir(Guid id, [FromBody] AssumirRequest? request) =>
            Executar(async ct => Ok(ApiResponse<RelacionamentoDto>.Ok(await _service.AssumirAsync(id, request, CurrentUserId!.Value, ct))));

        [HttpPost("api/acompanhamento/lojas/{id:guid}/cobrar-responsavel")]
        public Task<IActionResult> CobrarResponsavel(Guid id, [FromBody] CobrarResponsavelRequest? request) =>
            Executar(async ct => Ok(ApiResponse<RelacionamentoDto>.Ok(await _service.CobrarResponsavelAsync(id, request, CurrentUserId!.Value, ct))));

        [HttpPost("api/acompanhamento/lojas/{id:guid}/proximo-passo")]
        public Task<IActionResult> ProximoPasso(Guid id, [FromBody] DefinirProximoPassoRequest? request) => Executar(async ct =>
        {
            if (request == null) throw new RelacionamentoException(422, "PROXIMO_PASSO_OBRIGATORIO", "Informe o tipo e a data do proximo passo.");
            return Ok(ApiResponse<RelacionamentoDto>.Ok(await _service.DefinirProximoPassoAsync(id, request, CurrentUserId!.Value, ct)));
        });

        [HttpPost("api/acompanhamento/lojas/{id:guid}/marcacao")]
        public Task<IActionResult> Marcar(Guid id, [FromBody] MarcarRequest? request) => Executar(async ct =>
        {
            if (request == null) throw new RelacionamentoException(422, "MARCACAO_TIPO_INVALIDO", "Informe o tipo e o motivo da marcacao.");
            return Ok(ApiResponse<RelacionamentoDto>.Ok(await _service.MarcarAsync(id, request, CurrentUserId!.Value, ct)));
        });

        /// <summary>O motivo vai na query (?motivo=) ou no corpo ({"motivo": "..."}).</summary>
        [HttpDelete("api/acompanhamento/lojas/{id:guid}/marcacao")]
        public Task<IActionResult> Desmarcar(Guid id, [FromQuery] string? motivo, [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] MarcarRequest? body) =>
            Executar(async ct => Ok(ApiResponse<RelacionamentoDto>.Ok(await _service.DesmarcarAsync(id, motivo ?? body?.Motivo, CurrentUserId!.Value, ct))));

        [HttpPost("api/acompanhamento/lojas/{id:guid}/cancelamento")]
        public Task<IActionResult> Cancelamento(Guid id, [FromBody] CancelamentoRequest? request) => Executar(async ct =>
        {
            if (request == null) throw new RelacionamentoException(422, "PEDIDO_OBRIGATORIO", "Registre o pedido, a leitura, a oferta, o canal e o resultado.");
            if (string.IsNullOrWhiteSpace(request.ChaveIdempotencia) && Request.Headers.TryGetValue("Idempotency-Key", out var chave))
                request.ChaveIdempotencia = chave.ToString();
            var retencao = await _service.RegistrarCancelamentoAsync(id, request, CurrentUserId!.Value, ct);
            return Ok(ApiResponse<RetencaoDto>.Ok(retencao));
        });

        [HttpPost("api/acompanhamento/equipe/avisos/lidos")]
        public Task<IActionResult> AvisosLidos([FromBody] AvisosLidosRequest? request) => Executar(async ct =>
        {
            var marcados = await _service.MarcarAvisosLidosAsync(request?.Ids ?? new List<long>(), CurrentUserId!.Value, ct);
            return Ok(ApiResponse<object>.Ok(new { marcados }));
        });

        private async Task<IActionResult> Executar(Func<System.Threading.CancellationToken, Task<IActionResult>> acao)
        {
            var access = EnsureCrmAccess();
            if (access != null) return access;
            Response.Headers.CacheControl = "no-store";
            try
            {
                return await acao(HttpContext.RequestAborted);
            }
            catch (RelacionamentoException ex)
            {
                return StatusCode(ex.Status, ApiResponse<object>.Fail(ex.Message, ex.Code, ex.Details));
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
            {
                _logger.LogWarning("[acompanhamento.relacionamento] ev=migracao_pendente tabela={T}", ex.TableName);
                return StatusCode(StatusCodes.Status422UnprocessableEntity,
                    ApiResponse<object>.Fail("Faltam migracoes do acompanhamento neste banco.", "MIGRACAO_PENDENTE"));
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                _logger.LogWarning("[acompanhamento.relacionamento] ev=conflito constraint={C}", ex.ConstraintName);
                return StatusCode(StatusCodes.Status409Conflict,
                    ApiResponse<object>.Fail("Outra pessoa fez a mesma mudanca ao mesmo tempo. Recarregue e confira.", "CONFLITO"));
            }
            catch (OperationCanceledException)
            {
                return StatusCode(StatusCodes.Status499ClientClosedRequest);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[acompanhamento.relacionamento] ev=erro");
                return StatusCode(StatusCodes.Status500InternalServerError, ApiResponse<object>.Fail("Erro no acompanhamento da equipe.", "INTERNAL_ERROR"));
            }
        }
    }
}
