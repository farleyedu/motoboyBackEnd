using System;
using System.Threading.Tasks;
using APIBack.Attributes;
using APIBack.Extensions;
using APIBack.Payments.Controllers;
using APIBack.Payments.MercadoPago;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;

namespace APIBack.Payments.Chat
{
    /// <summary>
    /// Cobranca Pix do atendente (CONTRATOS.md Â§8.5). A loja vem do token; cobranca/conversa/pedido de outra loja responde 404.
    /// Erros no formato { success, error, code, details } (PagamentoControllerBase).
    /// </summary>
    [ApiController]
    [Route("api/v2/atendimento")]
    public sealed class CobrancaChatController : PagamentoControllerBase
    {
        private readonly CobrancaChatService _service;
        private readonly ILogger<CobrancaChatController> _logger;

        public CobrancaChatController(CobrancaChatService service, ILogger<CobrancaChatController> logger)
        {
            _service = service;
            _logger = logger;
        }

        [HttpPost("conversas/{conversaId:guid}/cobrancas")]
        [RequirePermission(PermissoesCobranca.Modulo, PermissoesCobranca.Cobrar)]
        [EnableRateLimiting(CobrancaChatDependencias.PoliticaEscrita)]
        public Task<IActionResult> Criar(Guid conversaId, [FromBody] CriarCobrancaChatRequest? body) =>
            Executar(() => _service.CriarAsync(Loja(), conversaId, body, Ator(), HttpContext.RequestAborted), _logger, StatusCodes.Status201Created);

        [HttpGet("conversas/{conversaId:guid}/cobrancas")]
        [RequirePermission("WhatsApp", "visualizar")]
        public Task<IActionResult> Listar(Guid conversaId, [FromQuery] string? status) =>
            Executar(() => _service.ListarAsync(Loja(), conversaId, status, Ator(), HttpContext.RequestAborted), _logger);

        [HttpGet("cobrancas/{id:guid}")]
        [RequirePermission("WhatsApp", "visualizar")]
        public Task<IActionResult> Obter(Guid id) => Executar(() => _service.ObterAsync(Loja(), id, Ator(), HttpContext.RequestAborted), _logger);

        [HttpGet("cobrancas/{id:guid}/historico")]
        [RequirePermission("WhatsApp", "visualizar")]
        public Task<IActionResult> Historico(Guid id) => Executar(() => _service.HistoricoAsync(Loja(), id, HttpContext.RequestAborted), _logger);

        [HttpPost("cobrancas/{id:guid}/enviar")]
        [RequirePermission(PermissoesCobranca.Modulo, PermissoesCobranca.Cobrar)]
        [EnableRateLimiting(CobrancaChatDependencias.PoliticaEscrita)]
        public Task<IActionResult> Enviar(Guid id, [FromBody] EnviarCobrancaChatRequest? body) =>
            Executar(() => _service.EnviarAsync(Loja(), id, body, Ator(), HttpContext.RequestAborted), _logger);

        [HttpPost("cobrancas/{id:guid}/cancelar")]
        [RequirePermission(PermissoesCobranca.Modulo, PermissoesCobranca.Cobrar)]
        public Task<IActionResult> Cancelar(Guid id, [FromBody] CancelarCobrancaChatRequest? body) =>
            Executar(() => _service.CancelarAsync(Loja(), id, body, Ator(), HttpContext.RequestAborted), _logger);

        [HttpPost("cobrancas/{id:guid}/reemitir")]
        [RequirePermission(PermissoesCobranca.Modulo, PermissoesCobranca.Cobrar)]
        [EnableRateLimiting(CobrancaChatDependencias.PoliticaEscrita)]
        public Task<IActionResult> Reemitir(Guid id, [FromBody] ReemitirCobrancaChatRequest? body) =>
            Executar(() => _service.ReemitirAsync(Loja(), id, body, Ator(), HttpContext.RequestAborted), _logger, StatusCodes.Status201Created);

        /// <summary>Direto so para quem tem Financeiro:cancelar; os demais recebem 403 PAG_EXIGE_APROVACAO e usam /solicitar-aprovacao.</summary>
        [HttpPost("cobrancas/{id:guid}/reembolsar")]
        [RequirePermission(PermissoesCobranca.Modulo, PermissoesCobranca.Cobrar)]
        public Task<IActionResult> Reembolsar(Guid id, [FromBody] ReembolsarCobrancaChatRequest? body) =>
            Executar(() => _service.ReembolsarAsync(Loja(), id, body, Ator(), HttpContext.RequestAborted), _logger);

        [HttpPost("cobrancas/{id:guid}/solicitar-aprovacao")]
        [RequirePermission(PermissoesCobranca.Modulo, PermissoesCobranca.Cobrar)]
        public Task<IActionResult> SolicitarAprovacao(Guid id, [FromBody] SolicitarAprovacaoRequest? body) =>
            Executar(() => _service.SolicitarAprovacaoAsync(Loja(), id, body, Ator(), HttpContext.RequestAborted), _logger, StatusCodes.Status201Created);

        [HttpGet("cobrancas/aprovacoes")]
        [RequirePermission(PermissoesCobranca.Modulo, PermissoesCobranca.Aprovar)]
        public Task<IActionResult> Aprovacoes([FromQuery] string? status) =>
            Executar(() => _service.AprovacoesAsync(Loja(), status ?? StatusAprovacao.Pendente, HttpContext.RequestAborted), _logger);

        [HttpPost("cobrancas/aprovacoes/{id:guid}/aprovar")]
        [RequirePermission(PermissoesCobranca.Modulo, PermissoesCobranca.Aprovar)]
        public Task<IActionResult> Aprovar(Guid id, [FromBody] DecidirAprovacaoRequest? body) =>
            Executar(() => _service.AprovarAsync(Loja(), id, body, Ator(), HttpContext.RequestAborted), _logger);

        [HttpPost("cobrancas/aprovacoes/{id:guid}/rejeitar")]
        [RequirePermission(PermissoesCobranca.Modulo, PermissoesCobranca.Aprovar)]
        public Task<IActionResult> Rejeitar(Guid id, [FromBody] DecidirAprovacaoRequest? body) =>
            Executar(() => _service.RejeitarAsync(Loja(), id, body, Ator(), HttpContext.RequestAborted), _logger);

        [HttpGet("cobrancas/config")]
        [RequirePermission("WhatsApp", "visualizar")]
        public Task<IActionResult> Config() => Executar(() => _service.ConfigAsync(Loja(), Ator(), HttpContext.RequestAborted), _logger);

        [HttpPut("cobrancas/config")]
        [RequirePermission("Financeiro", "configurar")]
        public Task<IActionResult> SalvarConfig([FromBody] CobrancaChatConfigRequest? body) =>
            Executar(() => _service.SalvarConfigAsync(Loja(), Ator(), body, HttpContext.RequestAborted), _logger);

        private Guid Loja() => HttpContext.GetEstabelecimentoId() ?? throw PagamentoException.SemPermissao("Selecione uma loja.");

        private AtorCobranca Ator()
        {
            var id = HttpContext.GetUserId() ?? throw new PagamentoException(PagamentoCodigos.NaoAutenticado, 401, "Nao autenticado.");
            var admin = HttpContext.IsSuperAdmin();
            var nome = HttpContext.GetUserNome();
            return new AtorCobranca(id, string.IsNullOrWhiteSpace(nome) ? null : nome,
                admin || HttpContext.TemPermissao(PermissoesCobranca.Modulo, PermissoesCobranca.Aprovar),
                admin || HttpContext.TemPermissao("Financeiro", "cancelar"));
        }
    }
}
