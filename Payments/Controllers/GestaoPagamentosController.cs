using System;
using System.Threading.Tasks;
using APIBack.DTOs.Common;
using APIBack.Extensions;
using APIBack.Payments.Gestao;
using APIBack.Payments.MercadoPago;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace APIBack.Payments.Controllers
{
    /// <summary>
    /// So super admin, validado no servidor a cada requisicao (o painel esconder a area nao basta). Sem login: 401; logado sem ser
    /// super admin: 403 PAG_SEM_PERMISSAO. Respostas nunca ficam em cache.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class SomenteSuperAdminAttribute : ActionFilterAttribute
    {
        public override void OnActionExecuting(ActionExecutingContext context)
        {
            var http = context.HttpContext;
            if (http.GetUserId() == null)
            {
                context.Result = new ObjectResult(ApiResponse<object>.Fail("Usuario nao autenticado.", PagamentoCodigos.NaoAutenticado)) { StatusCode = 401 };
                return;
            }
            if (!http.IsSuperAdmin())
            {
                context.Result = new ObjectResult(ApiResponse<object>.Fail("Area exclusiva da Zippy.", PagamentoCodigos.SemPermissao)) { StatusCode = 403 };
                return;
            }
            http.Response.Headers.CacheControl = "no-store";
        }
    }

    /// <summary>Gestao de Pagamentos da Zippy (CONTRATOS.md §8.1). Toda escrita grava pagamento_auditoria no servico.</summary>
    [ApiController]
    [SomenteSuperAdmin]
    [Route("api/gestao/pagamentos")]
    public sealed class GestaoPagamentosController : PagamentoControllerBase
    {
        private readonly IPagamentosGestaoService _gestao;
        private readonly ILogger<GestaoPagamentosController> _logger;

        public GestaoPagamentosController(IPagamentosGestaoService gestao, ILogger<GestaoPagamentosController> logger)
        {
            _gestao = gestao;
            _logger = logger;
        }

        private int Usuario => HttpContext.GetUserId() ?? 0;

        [HttpGet("resumo")]
        public Task<IActionResult> Resumo([FromQuery] string? mes) => Executar(() => _gestao.ResumoAsync(
            DateOnly.TryParseExact($"{mes}-01", "yyyy-MM-dd", out var m) ? m : null, HttpContext.RequestAborted), _logger);

        [HttpGet("configuracao")]
        public Task<IActionResult> Configuracao([FromQuery] string? ambiente) => Executar(() => _gestao.ConfiguracaoAsync(ambiente, HttpContext.RequestAborted), _logger);

        /// <summary>Liga/desliga o ambiente e a conexao das lojas. Ligar exige verificacao aprovada e atual.</summary>
        [HttpPut("configuracao")]
        public Task<IActionResult> AtualizarAmbiente([FromBody] AtualizarAmbienteRequest body) =>
            Executar(() => _gestao.AtualizarAmbienteAsync(body, Usuario, HttpContext.RequestAborted), _logger);

        /// <summary>Escolhe qual ambiente (sandbox ou producao) o servidor usa. Os dois seguem conectados.</summary>
        [HttpPut("ambiente-ativo")]
        public Task<IActionResult> DefinirAmbienteAtivo([FromBody] AmbienteRequest body) =>
            Executar(() => _gestao.DefinirAmbienteAtivoAsync(body?.Ambiente, Usuario, HttpContext.RequestAborted), _logger);

        [HttpPut("aplicacao")]
        public Task<IActionResult> SalvarAplicacao([FromBody] SalvarAplicacaoRequest body) =>
            Executar(() => _gestao.SalvarAplicacaoAsync(body, Usuario, HttpContext.RequestAborted), _logger);

        [HttpPut("conta-zippy")]
        public Task<IActionResult> SalvarContaZippy([FromBody] SalvarContaZippyRequest body) =>
            Executar(() => _gestao.SalvarContaZippyAsync(body, Usuario, HttpContext.RequestAborted), _logger);

        [HttpPost("conta-zippy/testar")]
        public Task<IActionResult> TestarContaZippy([FromBody] AmbienteRequest? body) =>
            Executar(() => _gestao.TestarContaZippyAsync(body?.Ambiente, Usuario, HttpContext.RequestAborted), _logger);

        [HttpPost("verificar")]
        public Task<IActionResult> Verificar([FromBody] AmbienteRequest? body) =>
            Executar(() => _gestao.VerificarAsync(body?.Ambiente, Usuario, HttpContext.RequestAborted), _logger);

        [HttpGet("conexoes")]
        public Task<IActionResult> Conexoes([FromQuery] string? busca, [FromQuery] string? estado, [FromQuery] int pagina = 1) =>
            Executar(() => _gestao.ConexoesAsync(busca, estado, pagina, HttpContext.RequestAborted), _logger);

        [HttpGet("transacoes")]
        public Task<IActionResult> Transacoes([FromQuery] string? conta, [FromQuery] Guid? estabelecimentoId, [FromQuery] string? origem, [FromQuery] string? status,
            [FromQuery] DateTime? de, [FromQuery] DateTime? ate, [FromQuery] int pagina = 1) =>
            Executar(() => _gestao.TransacoesAsync(conta, estabelecimentoId, origem, status, de, ate, pagina, HttpContext.RequestAborted), _logger);

        [HttpGet("assinaturas")]
        public Task<IActionResult> Assinaturas([FromQuery] string? status, [FromQuery] int pagina = 1) =>
            Executar(() => _gestao.AssinaturasAsync(status, pagina, HttpContext.RequestAborted), _logger);

        [HttpGet("reembolsos")]
        public Task<IActionResult> Reembolsos([FromQuery] string? status, [FromQuery] string? conta, [FromQuery] int pagina = 1) =>
            Executar(() => _gestao.ReembolsosAsync(status, conta, pagina, HttpContext.RequestAborted), _logger);

        [HttpGet("webhooks")]
        public Task<IActionResult> Webhooks([FromQuery] string? status, [FromQuery] string? topico, [FromQuery] int pagina = 1) =>
            Executar(() => _gestao.WebhooksAsync(status, topico, pagina, HttpContext.RequestAborted), _logger);

        [HttpPost("webhooks/{id:guid}/reprocessar")]
        public Task<IActionResult> Reprocessar(Guid id) =>
            Executar(async () => new { status = await _gestao.ReprocessarWebhookAsync(id, Usuario, HttpContext.RequestAborted) }, _logger);

        [HttpPost("reconciliar")]
        public Task<IActionResult> Reconciliar() => Executar(() => _gestao.ReconciliarAsync(Usuario, HttpContext.RequestAborted), _logger);

        [HttpGet("disputas")]
        public Task<IActionResult> Disputas([FromQuery] string? status) =>
            Executar(async () => new { itens = await _gestao.DisputasAsync(status, HttpContext.RequestAborted) }, _logger);

        [HttpGet("auditoria")]
        public Task<IActionResult> Auditoria([FromQuery] string? escopo, [FromQuery] string? entidade, [FromQuery] string? entidadeId, [FromQuery] int pagina = 1) =>
            Executar(() => _gestao.AuditoriaAsync(escopo, entidade, entidadeId, pagina, HttpContext.RequestAborted), _logger);

        [HttpGet("politicas")]
        public Task<IActionResult> Politicas() => Executar(() => _gestao.PoliticasAsync(HttpContext.RequestAborted), _logger);

        [HttpPut("politicas")]
        public Task<IActionResult> SalvarPoliticas([FromBody] PoliticasDto body) =>
            Executar(() => _gestao.SalvarPoliticasAsync(body, Usuario, HttpContext.RequestAborted), _logger);
    }
}
