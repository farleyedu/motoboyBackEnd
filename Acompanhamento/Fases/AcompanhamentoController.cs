using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.Controllers;
using APIBack.DTOs.Common;
using APIBack.Hubs;
using APIBack.Service;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace APIBack.Acompanhamento.Fases
{
    /// <summary>
    /// Fases e passagens do acompanhamento (so a equipe Zippy: super admin). {id} aceita o id do acompanhamento, da oportunidade
    /// ou da loja. Erros: 401 sem login, 403 sem permissao, 404 nao encontrado, 409 concorrencia (FASE_MUDOU, chave reutilizada),
    /// 422 passagem que nao existe ou requisito faltando (details: motivo, caminho, sequencia, requisitosPendentes).
    /// </summary>
    [ApiController]
    public sealed class AcompanhamentoController : CrmControllerBase
    {
        private readonly IAcompanhamentoService _service;
        private readonly ISituacaoComercialService _situacao;
        private readonly IHubContext<DeliveryHub> _hub;
        private readonly DeliveryHubConnections _connections;
        private readonly ILogger<AcompanhamentoController> _logger;

        public AcompanhamentoController(IAcompanhamentoService service, ISituacaoComercialService situacao, IHubContext<DeliveryHub> hub,
            DeliveryHubConnections connections, ILogger<AcompanhamentoController> logger)
        {
            _service = service;
            _situacao = situacao;
            _hub = hub;
            _connections = connections;
            _logger = logger;
        }

        /// <summary>As passagens que existem (quem decide, requisitos, o que acontece, se volta) e as que nao existem (motivo e caminho).</summary>
        [HttpGet("api/acompanhamento/passagens")]
        public IActionResult Passagens()
        {
            var access = EnsureCrmAccess();
            if (access != null) return access;
            return Ok(ApiResponse<PassagensCatalogoDto>.Ok(_service.Catalogo()));
        }

        /// <summary>A passagem e o estado de cada requisito para esta loja. Os campos da query entram na conferencia (ex.: ?documento=).</summary>
        [HttpGet("api/acompanhamento/lojas/{id:guid}/passagens/{para}")]
        public Task<IActionResult> Avaliar(Guid id, string para, [FromQuery] DadosPassagem? dados) =>
            ExecutarComErrosAsync(async () =>
            {
                Response.Headers.CacheControl = "no-store";
                return Ok(ApiResponse<AvaliacaoPassagemDto>.Ok(await _service.AvaliarAsync(id, para, dados, HttpContext.RequestAborted)));
            }, id);

        /// <summary>Executa a passagem. Mesma chave de idempotencia = mesma resposta, sem fazer de novo (repetida = true).</summary>
        [HttpPost("api/acompanhamento/lojas/{id:guid}/passagens/{para}")]
        public Task<IActionResult> Executar(Guid id, string para, [FromBody] DadosPassagem? dados) =>
            ExecutarComErrosAsync(async () =>
            {
                dados ??= new DadosPassagem();
                if (string.IsNullOrWhiteSpace(dados.ChaveIdempotencia) && Request.Headers.TryGetValue("Idempotency-Key", out var chave))
                    dados.ChaveIdempotencia = chave.ToString();
                var resultado = await _service.ExecutarAsync(id, para, dados, CurrentUserId, AutoresPassagem.Usuario, HttpContext.RequestAborted);
                if (!resultado.Repetida && resultado.EstabelecimentoId.HasValue)
                    await NotificarAsync(resultado.EstabelecimentoId.Value);
                return Ok(ApiResponse<PassagemResultadoDto>.Ok(resultado));
            }, id);

        // ---- propostas: {versaoId} e a linha da versao; versaoEsperada e a "edicao" que a tela leu

        [HttpGet("api/acompanhamento/lojas/{id:guid}/propostas")]
        public Task<IActionResult> Propostas(Guid id) =>
            ExecutarComErrosAsync(async () =>
            {
                Response.Headers.CacheControl = "no-store";
                return Ok(ApiResponse<PropostasDto>.Ok(await _service.PropostasAsync(id, HttpContext.RequestAborted)));
            }, id);

        /// <summary>Cria rascunho ou revisao (copia a anterior). Ja ha rascunho aberto = devolve ele, com repetida = true.</summary>
        [HttpPost("api/acompanhamento/lojas/{id:guid}/propostas")]
        public Task<IActionResult> CriarProposta(Guid id, [FromBody] PropostaRequest? request) =>
            ExecutarComErrosAsync(async () => Ok(ApiResponse<PropostaResultadoDto>.Ok(
                await _service.CriarPropostaAsync(id, request ?? new PropostaRequest(), CurrentUserId, HttpContext.RequestAborted))), id);

        [HttpPut("api/acompanhamento/lojas/{id:guid}/propostas/{versaoId:guid}")]
        public Task<IActionResult> EditarProposta(Guid id, Guid versaoId, [FromBody] PropostaRequest? request) =>
            ExecutarComErrosAsync(async () => Ok(ApiResponse<PropostaResultadoDto>.Ok(
                await _service.EditarPropostaAsync(id, versaoId, request ?? new PropostaRequest(), CurrentUserId, HttpContext.RequestAborted))), id);

        /// <summary>Registra o envio declarado. Mesma chave = mesma resposta (repetida = true). A chave tambem pode vir em Idempotency-Key.</summary>
        [HttpPost("api/acompanhamento/lojas/{id:guid}/propostas/{versaoId:guid}/enviar")]
        public Task<IActionResult> EnviarProposta(Guid id, Guid versaoId, [FromBody] EnviarPropostaRequest? request) =>
            ExecutarComErrosAsync(async () =>
            {
                request ??= new EnviarPropostaRequest();
                if (string.IsNullOrWhiteSpace(request.ChaveIdempotencia) && Request.Headers.TryGetValue("Idempotency-Key", out var chave))
                    request.ChaveIdempotencia = chave.ToString();
                return Ok(ApiResponse<PropostaResultadoDto>.Ok(await _service.EnviarPropostaAsync(id, versaoId, request, CurrentUserId, HttpContext.RequestAborted)));
            }, id);

        [HttpPost("api/acompanhamento/lojas/{id:guid}/propostas/{versaoId:guid}/resposta")]
        public Task<IActionResult> ResponderProposta(Guid id, Guid versaoId, [FromBody] ResponderPropostaRequest? request) =>
            ExecutarComErrosAsync(async () =>
            {
                request ??= new ResponderPropostaRequest();
                if (string.IsNullOrWhiteSpace(request.ChaveIdempotencia) && Request.Headers.TryGetValue("Idempotency-Key", out var chave))
                    request.ChaveIdempotencia = chave.ToString();
                return Ok(ApiResponse<PropostaResultadoDto>.Ok(await _service.ResponderPropostaAsync(id, versaoId, request, CurrentUserId, HttpContext.RequestAborted)));
            }, id);

        private async Task<IActionResult> ExecutarComErrosAsync(Func<Task<IActionResult>> acao, Guid id)
        {
            var access = EnsureCrmAccess();
            if (access != null) return access;
            try
            {
                return await acao();
            }
            catch (AcompanhamentoException ex)
            {
                return StatusCode(ex.Status, ApiResponse<object>.Fail(ex.Message, ex.Code, ex.Details));
            }
            catch (SituacaoComercialException ex)
            {
                return StatusCode(ex.Status, ApiResponse<object>.Fail(ex.Message, ex.Code, ex.Details));
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(ApiResponse<object>.Fail(ex.Message, CodigosAcompanhamento.NaoEncontrado));
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("nao pertence", StringComparison.OrdinalIgnoreCase))
            {
                return StatusCode(StatusCodes.Status422UnprocessableEntity, ApiResponse<object>.Fail(ex.Message, CodigosAcompanhamento.RequisitosPendentes));
            }
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.DeadlockDetected
                                                   or PostgresErrorCodes.SerializationFailure)
            {
                // Duas passagens disputando a mesma loja: a outra ganhou. A tela atualiza e tenta de novo.
                return StatusCode(StatusCodes.Status409Conflict, ApiResponse<object>.Fail(
                    "Outra mudança nesta loja aconteceu ao mesmo tempo. Atualize e tente de novo.", CodigosAcompanhamento.FaseMudou));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Erro na passagem do acompanhamento {Id}", id);
                return StatusCode(StatusCodes.Status500InternalServerError, ApiResponse<object>.Fail("Erro ao processar a passagem."));
            }
        }

        /// <summary>O painel da loja aberto recebe a situacao nova (como em /api/gestao/.../situacao); bloqueio derruba as conexoes.</summary>
        private async Task NotificarAsync(Guid estabelecimentoId)
        {
            var dto = await _situacao.ObterAsync(estabelecimentoId, HttpContext.RequestAborted);
            if (dto == null) return;
            await _hub.Clients.Group(DeliveryRealtimeEvents.EstablishmentGroup(estabelecimentoId))
                .SendAsync(DeliveryRealtimeEvents.EstabelecimentoSituacaoAlterada, dto, HttpContext.RequestAborted);
            if (dto.Situacao == SituacoesComerciais.Bloqueado) _connections.Abort(estabelecimentoId);
        }
    }
}
