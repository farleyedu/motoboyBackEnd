using System;
using System.Threading.Tasks;
using APIBack.Extensions;
using APIBack.Payments.Conexao;
using APIBack.Payments.MercadoPago;
using APIBack.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace APIBack.Payments.Controllers
{
    /// <summary>Quem mexe na conexao MP da loja. Fica fora do catalogo de modulos: Integracoes vale antes de contratar modulos premium.</summary>
    public static class ConexaoMercadoPagoAcesso
    {
        /// <summary>Conectar, desconectar e mudar meios: dono, gerente da empresa ou gerente da loja (ou super admin).</summary>
        public static bool PodeGerenciar(HttpContext http) =>
            http.IsSuperAdmin() || RoleCatalog.CanViewUsers(http.GetTipoAcessoEmpresa(), http.GetTipoAcesso(), false);

        /// <summary>Ver estado e transacoes: quem gerencia ou quem tem Financeiro:visualizar.</summary>
        public static bool PodeVer(HttpContext http) => PodeGerenciar(http) || http.TemPermissao("Financeiro", "visualizar");
    }

    public sealed class ConectarMercadoPagoRequest
    {
        /// <summary>true = trocar/renovar a conta ja conectada.</summary>
        public bool Reconectar { get; set; }
    }

    /// <summary>Conexao OAuth do Mercado Pago da loja selecionada (CONTRATOS.md §8.2). A loja nunca digita chave tecnica.</summary>
    [ApiController]
    [Route("api/v2/integracoes/mercadopago")]
    public sealed class MercadoPagoIntegracaoController : PagamentoControllerBase
    {
        private readonly IMercadoPagoConexaoLojaService _conexao;
        private readonly ILogger<MercadoPagoIntegracaoController> _logger;

        public MercadoPagoIntegracaoController(IMercadoPagoConexaoLojaService conexao, ILogger<MercadoPagoIntegracaoController> logger)
        {
            _conexao = conexao;
            _logger = logger;
        }

        [HttpGet]
        public Task<IActionResult> Estado() => Ver(loja => _conexao.EstadoAsync(loja, HttpContext.RequestAborted));

        [HttpPost("conectar")]
        public Task<IActionResult> Conectar([FromBody] ConectarMercadoPagoRequest? body) =>
            Gerenciar((loja, usuario) => _conexao.ConectarAsync(loja, usuario, body?.Reconectar == true, HttpContext.RequestAborted));

        [HttpGet("tentativas/{id:guid}")]
        public Task<IActionResult> Tentativa(Guid id) => Gerenciar((loja, _) => _conexao.TentativaAsync(loja, id, HttpContext.RequestAborted));

        [HttpDelete]
        public Task<IActionResult> Desconectar([FromQuery] bool confirmar = false) =>
            Gerenciar((loja, usuario) => _conexao.DesconectarAsync(loja, usuario, confirmar, HttpContext.RequestAborted));

        [HttpPost("verificar")]
        public Task<IActionResult> Verificar() => Gerenciar((loja, usuario) => _conexao.VerificarAsync(loja, usuario, HttpContext.RequestAborted));

        [HttpGet("preferencias")]
        public Task<IActionResult> Preferencias() => Ver(loja => _conexao.PreferenciasAsync(loja, HttpContext.RequestAborted));

        [HttpPut("preferencias")]
        public Task<IActionResult> SalvarPreferencias([FromBody] PreferenciasPagamentoDto body) =>
            Gerenciar((loja, usuario) => _conexao.SalvarPreferenciasAsync(loja, usuario, body, HttpContext.RequestAborted));

        [HttpGet("transacoes")]
        public Task<IActionResult> Transacoes([FromQuery] int pagina = 1) =>
            Ver(async loja => new { itens = await _conexao.TransacoesAsync(loja, pagina, HttpContext.RequestAborted) });

        private Task<IActionResult> Ver<T>(Func<Guid, Task<T>> acao) => Executar(() =>
        {
            if (!ConexaoMercadoPagoAcesso.PodeVer(HttpContext)) throw PagamentoException.SemPermissao("Sem permissao para ver a conexao de pagamentos.");
            return acao(Loja());
        }, _logger);

        private Task<IActionResult> Gerenciar<T>(Func<Guid, int, Task<T>> acao) => Executar(() =>
        {
            var usuario = HttpContext.GetUserId() ?? throw new PagamentoException(PagamentoCodigos.NaoAutenticado, 401, "Usuario nao autenticado.");
            if (!ConexaoMercadoPagoAcesso.PodeGerenciar(HttpContext))
                throw PagamentoException.SemPermissao("Só o dono ou o gerente da loja pode mudar a conexão com o Mercado Pago.");
            return acao(Loja(), usuario);
        }, _logger);

        private Guid Loja() => HttpContext.GetEstabelecimentoId() ?? throw PagamentoException.SemPermissao("Selecione uma loja.");
    }

    /// <summary>
    /// Volta do Mercado Pago depois da autorizacao (anonimo: quem chega e o navegador). Amarrado a tentativa pelo state (uso unico, 10 min,
    /// PKCE). Sempre redireciona para o painel com ?mercadopago=ok|erro&amp;codigo=; nunca mostra token.
    /// </summary>
    [ApiController]
    [AllowAnonymous]
    [Route("api/integracoes/mercadopago/callback")]
    public sealed class MercadoPagoCallbackController : ControllerBase
    {
        private readonly IMercadoPagoConexaoLojaService _conexao;

        public MercadoPagoCallbackController(IMercadoPagoConexaoLojaService conexao) => _conexao = conexao;

        [HttpGet]
        public async Task<IActionResult> Callback([FromQuery] string? code, [FromQuery] string? state, [FromQuery] string? error)
        {
            var destino = await _conexao.ConcluirAsync(code, state, error, HttpContext.RequestAborted);
            Response.Headers.CacheControl = "no-store";
            Response.Headers["Referrer-Policy"] = "no-referrer";
            if (destino.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return Redirect(destino);
            // Sem endereco do painel configurado: pagina minima (o codigo do MP ja foi usado e nao aparece aqui).
            var ok = destino.Contains("mercadopago=ok", StringComparison.Ordinal);
            return Content(ok ? "Mercado Pago conectado. Pode fechar esta janela e voltar ao Zippy."
                : "Nao foi possivel conectar o Mercado Pago. Volte ao Zippy e tente de novo.", "text/plain; charset=utf-8");
        }
    }
}
