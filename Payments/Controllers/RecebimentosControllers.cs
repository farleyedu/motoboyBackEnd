using System;
using System.Threading.Tasks;
using APIBack.Attributes;
using APIBack.Extensions;
using APIBack.Payments.MercadoPago;
using APIBack.Payments.Recebimentos;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace APIBack.Payments.Controllers
{
    /// <summary>Recebimentos online da loja (CONTRATOS.md §15). A loja sai sempre da sessao; nada vem do cliente. Permissoes do Financeiro.</summary>
    [ApiController]
    [Route("api/v2/pagamentos/recebimentos")]
    public sealed class RecebimentosLojaController : PagamentoControllerBase
    {
        private readonly IRecebimentosOnlineService _recebimentos;
        private readonly ILogger<RecebimentosLojaController> _logger;

        public RecebimentosLojaController(IRecebimentosOnlineService recebimentos, ILogger<RecebimentosLojaController> logger)
        {
            _recebimentos = recebimentos;
            _logger = logger;
        }

        private Guid Loja() => HttpContext.GetEstabelecimentoId() ?? throw PagamentoException.SemPermissao("Selecione uma loja.");
        private int Usuario() => HttpContext.GetUserId() ?? throw PagamentoException.SemPermissao("Usuario nao autenticado.");

        [HttpGet]
        [RequirePermission("Financeiro", "visualizar")]
        public Task<IActionResult> Listar([FromQuery] string? provedor, [FromQuery] bool soAtencao, [FromQuery] DateTime? de, [FromQuery] DateTime? ate, [FromQuery] int pagina = 1) =>
            Executar(() => _recebimentos.ListarAsync(new FiltroRecebimentos { Loja = Loja(), Provedor = provedor, SoAtencao = soAtencao, De = de, Ate = ate, Pagina = pagina },
                HttpContext.RequestAborted), _logger);

        [HttpGet("alertas")]
        [RequirePermission("Financeiro", "visualizar")]
        public Task<IActionResult> Alertas() => Executar(() => _recebimentos.AlertasAsync(Loja(), HttpContext.RequestAborted), _logger);

        [HttpGet("{intentId:guid}/diagnostico")]
        [RequirePermission("Financeiro", "visualizar")]
        public Task<IActionResult> Diagnostico(Guid intentId) => Executar(() => _recebimentos.DiagnosticoAsync(intentId, Loja(), HttpContext.RequestAborted), _logger);

        /// <summary>Registra o que a loja diz ter feito na conta InfinitePay. Nao devolve dinheiro nem muda a cobranca.</summary>
        [HttpPost("{intentId:guid}/estorno-informado")]
        [RequirePermission("Financeiro", "cancelar")]
        public Task<IActionResult> InformarEstorno(Guid intentId, [FromBody] InformarEstornoRequest body) =>
            Executar(() => _recebimentos.InformarEstornoAsync(intentId, Loja(), body, Usuario(), HttpContext.RequestAborted), _logger, StatusCodes.Status201Created);

        [HttpPost("estornos-informados/{id:guid}/descartar")]
        [RequirePermission("Financeiro", "cancelar")]
        public Task<IActionResult> DescartarEstorno(Guid id) => Executar(() => _recebimentos.DescartarEstornoAsync(id, Loja(), Usuario(), HttpContext.RequestAborted), _logger);
    }

    /// <summary>Mesma leitura na Gestao Zippy (so super admin), com todas as lojas. So aqui um estorno informado vira confirmado, com evidencia.</summary>
    [ApiController]
    [SomenteSuperAdmin]
    [Route("api/gestao/pagamentos/recebimentos")]
    public sealed class RecebimentosGestaoController : PagamentoControllerBase
    {
        private readonly IRecebimentosOnlineService _recebimentos;
        private readonly ILogger<RecebimentosGestaoController> _logger;

        public RecebimentosGestaoController(IRecebimentosOnlineService recebimentos, ILogger<RecebimentosGestaoController> logger)
        {
            _recebimentos = recebimentos;
            _logger = logger;
        }

        private int Usuario => HttpContext.GetUserId() ?? 0;

        [HttpGet]
        public Task<IActionResult> Listar([FromQuery] Guid? estabelecimentoId, [FromQuery] string? provedor, [FromQuery] bool soAtencao, [FromQuery] DateTime? de, [FromQuery] DateTime? ate,
            [FromQuery] int pagina = 1) => Executar(() => _recebimentos.ListarAsync(
                new FiltroRecebimentos { Loja = estabelecimentoId, Provedor = provedor, SoAtencao = soAtencao, De = de, Ate = ate, Pagina = pagina }, HttpContext.RequestAborted), _logger);

        [HttpGet("alertas")]
        public Task<IActionResult> Alertas() => Executar(() => _recebimentos.AlertasAsync(null, HttpContext.RequestAborted), _logger);

        [HttpGet("{intentId:guid}/diagnostico")]
        public Task<IActionResult> Diagnostico(Guid intentId) => Executar(() => _recebimentos.DiagnosticoAsync(intentId, null, HttpContext.RequestAborted), _logger);

        [HttpGet("pendencias")]
        public Task<IActionResult> Pendencias([FromQuery] string? status, [FromQuery] int pagina = 1) =>
            Executar(() => _recebimentos.PendenciasAsync(null, status, pagina, HttpContext.RequestAborted), _logger);

        [HttpPost("pendencias/{id:guid}/resolver")]
        public Task<IActionResult> ResolverPendencia(Guid id, [FromBody] ResolverPendenciaRequest body) => Executar(async () =>
        {
            await _recebimentos.ResolverPendenciaAsync(id, body, Usuario, HttpContext.RequestAborted);
            return new { resolvida = true };
        }, _logger);

        [HttpPost("pendencias/{id:guid}/reverificar")]
        public Task<IActionResult> Reverificar(Guid id) =>
            Executar(async () => new { voltouParaFila = await _recebimentos.ReverificarPendenciaAsync(id, Usuario, HttpContext.RequestAborted) }, _logger);

        [HttpGet("estornos-informados")]
        public Task<IActionResult> EstornosInformados([FromQuery] string? status, [FromQuery] int pagina = 1) =>
            Executar(() => _recebimentos.EstornosInformadosAsync(null, status, pagina, HttpContext.RequestAborted), _logger);

        [HttpPost("estornos-informados/{id:guid}/confirmar")]
        public Task<IActionResult> ConfirmarEstorno(Guid id, [FromBody] ConfirmarEstornoRequest body) =>
            Executar(() => _recebimentos.ConfirmarEstornoAsync(id, body, Usuario, HttpContext.RequestAborted), _logger);

        [HttpPost("estornos-informados/{id:guid}/descartar")]
        public Task<IActionResult> DescartarEstorno(Guid id) => Executar(() => _recebimentos.DescartarEstornoAsync(id, null, Usuario, HttpContext.RequestAborted), _logger);
    }
}
