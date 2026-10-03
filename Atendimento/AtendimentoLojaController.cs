using APIBack.Atendimento.Motor;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.Controllers;
using APIBack.DTOs.Common;
using APIBack.Extensions;
using APIBack.Service;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace APIBack.Atendimento
{
    public sealed class ServicosRequest
    {
        public List<string>? Servicos { get; set; }
    }

    public sealed class SimulacaoRequest
    {
        public string Texto { get; set; } = string.Empty;
        public string? Modo { get; set; }
        public List<string>? Servicos { get; set; }
        public bool PrimeiraMensagem { get; set; } = true;
        /// <summary>Estado devolvido na resposta anterior, para o teste seguir a conversa.</summary>
        public EstadoFluxo? Estado { get; set; }
    }

    /// <summary>Acao do motor em forma serializavel (o tipo abstrato AcaoMotor sairia vazio no JSON).</summary>
    public sealed class AcaoSimulada
    {
        /// <summary>responder, botoes ou chamar_atendente.</summary>
        public string Tipo { get; set; } = string.Empty;
        public string? Mensagem { get; set; }
        public List<string>? Botoes { get; set; }
        public string? Motivo { get; set; }

        public static AcaoSimulada De(AcaoMotor acao) => acao switch
        {
            AcaoResponder r => new AcaoSimulada { Tipo = "responder", Mensagem = r.Mensagem },
            AcaoBotoes b => new AcaoSimulada { Tipo = "botoes", Mensagem = b.Mensagem, Botoes = b.Opcoes.Select(o => o.Titulo).ToList() },
            AcaoChamarAtendente c => new AcaoSimulada { Tipo = "chamar_atendente", Motivo = c.Motivo },
            _ => new AcaoSimulada { Tipo = acao.GetType().Name }
        };
    }

    public sealed class SimulacaoResposta
    {
        public List<AcaoSimulada> Acoes { get; set; } = new();
        public EstadoFluxo Estado { get; set; } = new();
        public string Fluxo { get; set; } = string.Empty;
        public string Passo { get; set; } = string.Empty;
        public string Regra { get; set; } = string.Empty;
    }

    public sealed class ModoRequest
    {
        public string? Modo { get; set; }
    }

    public sealed class MoverCanalRequest
    {
        public Guid Destino { get; set; }
    }

    /// <summary>
    /// Servicos e numeros de WhatsApp de uma loja. Quem pode o que e decidido nos servicos (nunca aqui): a Gestao liga
    /// servicos e cadastra numeros; o dono da loja le tudo da propria loja e escolhe o modo de atendimento do numero.
    /// </summary>
    [ApiController]
    [Route("api/v2/atendimento/estabelecimentos/{estabelecimentoId:guid}")]
    public sealed class AtendimentoLojaController : ApiControllerBase
    {
        private readonly IServicosDaLojaService _servicos;
        private readonly ICanaisWhatsappService _canais;
        private readonly IExecutorDeAtendimento _executor;
        private readonly ILogger<AtendimentoLojaController> _logger;

        public AtendimentoLojaController(
            IServicosDaLojaService servicos, ICanaisWhatsappService canais, IExecutorDeAtendimento executor, ILogger<AtendimentoLojaController> logger)
        {
            _servicos = servicos;
            _canais = canais;
            _executor = executor;
            _logger = logger;
        }

        [HttpGet("servicos")]
        public Task<IActionResult> ObterServicos(Guid estabelecimentoId) =>
            Executar(ator => _servicos.ObterAsync(ator, estabelecimentoId));

        [HttpPut("servicos")]
        public Task<IActionResult> DefinirServicos(Guid estabelecimentoId, [FromBody] ServicosRequest request) =>
            Executar(ator => _servicos.DefinirAsync(ator, estabelecimentoId, request.Servicos));

        [HttpGet("canais")]
        public Task<IActionResult> ListarCanais(Guid estabelecimentoId) =>
            Executar(ator => _canais.ListarAsync(ator, estabelecimentoId));

        [HttpPost("canais")]
        public Task<IActionResult> CriarCanal(Guid estabelecimentoId, [FromBody] CanalRequest request) =>
            Executar(ator => _canais.CriarAsync(ator, estabelecimentoId, request), StatusCodes.Status201Created);

        [HttpPut("canais/{canalId:guid}")]
        public Task<IActionResult> AtualizarCanal(Guid estabelecimentoId, Guid canalId, [FromBody] CanalRequest request) =>
            Executar(ator => _canais.AtualizarAsync(ator, canalId, request));

        [HttpPut("canais/{canalId:guid}/atendimento")]
        public Task<IActionResult> DefinirModo(Guid estabelecimentoId, Guid canalId, [FromBody] ModoRequest request) =>
            Executar(ator => _canais.DefinirModoAsync(ator, canalId, request.Modo));

        [HttpPut("canais/{canalId:guid}/servicos")]
        public Task<IActionResult> DefinirServicosDoCanal(Guid estabelecimentoId, Guid canalId, [FromBody] ServicosRequest request) =>
            Executar(ator => _canais.DefinirServicosAsync(ator, canalId, request.Servicos));

        [HttpPost("canais/{canalId:guid}/mover")]
        public Task<IActionResult> MoverCanal(Guid estabelecimentoId, Guid canalId, [FromBody] MoverCanalRequest request) =>
            Executar(ator => _canais.MoverAsync(ator, canalId, request.Destino));

        [HttpPost("canais/{canalId:guid}/verificar")]
        public Task<IActionResult> VerificarCanal(Guid estabelecimentoId, Guid canalId) =>
            Executar(ator => _canais.VerificarAsync(ator, canalId));

        [HttpPost("simulador")]
        public Task<IActionResult> Simular(Guid estabelecimentoId, [FromBody] SimulacaoRequest request) =>
            Executar(async ator =>
            {
                ServicosDaLojaService.ExigirLeitura(ator, estabelecimentoId);
                var servicos = request.Servicos ?? (await _servicos.ObterAsync(ator, estabelecimentoId)).Servicos.Where(s => s.Ativo).Select(s => s.Codigo).ToList();
                var modo = ModoAtendimento.Valido(request.Modo) ? request.Modo! : ModoAtendimento.Hibrido;
                var texto = (request.Texto ?? string.Empty).Trim();
                if (texto.Length is 0 or > 500) throw new RequestValidationException("Mensagem invalida.", new Dictionary<string, List<string>> { ["texto"] = new() { "Escreva a mensagem do cliente (ate 500 caracteres)." } });
                var r = await _executor.SimularAsync(estabelecimentoId, modo, servicos, texto, request.Estado, request.PrimeiraMensagem);
                return new SimulacaoResposta { Acoes = r.Acoes.Select(AcaoSimulada.De).ToList(), Estado = r.Estado, Fluxo = r.Fluxo, Passo = r.Passo, Regra = r.Regra };
            });

        [HttpDelete("canais/{canalId:guid}")]
        public Task<IActionResult> RemoverCanal(Guid estabelecimentoId, Guid canalId) =>
            Executar(async ator =>
            {
                await _canais.RemoverAsync(ator, canalId);
                return new object();
            });

        private async Task<IActionResult> Executar<T>(Func<AtendimentoAtor, Task<T>> operacao, int sucesso = StatusCodes.Status200OK)
        {
            var usuarioId = HttpContext.GetUserId();
            if (!usuarioId.HasValue)
            {
                return Unauthorized(ApiResponse<object>.Fail("Usuario nao autenticado."));
            }

            var ator = new AtendimentoAtor(
                usuarioId.Value, HttpContext.IsSuperAdmin(), HttpContext.GetEstabelecimentoId(), (modulo, acao) => HttpContext.TemPermissao(modulo, acao));

            try
            {
                return StatusCode(sucesso, ApiResponse<T>.Ok(await operacao(ator)));
            }
            catch (RequestValidationException ex)
            {
                return ValidationErrorResponse(ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.Fail(ex.Message));
            }
            catch (KeyNotFoundException ex)
            {
                return NotFoundErrorResponse(ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[atend.api] ev=erro rota={Rota} usuario={Usuario}", Request.Path.Value, usuarioId.Value);
                return StatusCode(StatusCodes.Status500InternalServerError, ApiResponse<object>.Fail("Nao foi possivel concluir a operacao."));
            }
        }
    }
}
