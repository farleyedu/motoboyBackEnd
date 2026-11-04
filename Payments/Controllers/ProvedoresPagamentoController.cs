using System;
using System.Threading.Tasks;
using APIBack.Extensions;
using APIBack.Payments.Conexao;
using APIBack.Payments.MercadoPago;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace APIBack.Payments.Controllers
{
    [ApiController]
    [Route("api/v2/integracoes/infinitepay")]
    public sealed class InfinitePayIntegracaoController : PagamentoControllerBase
    {
        private readonly IInfinitePayConexaoLojaService _conexao;
        private readonly ILogger<InfinitePayIntegracaoController> _logger;

        public InfinitePayIntegracaoController(IInfinitePayConexaoLojaService conexao, ILogger<InfinitePayIntegracaoController> logger)
        {
            _conexao = conexao;
            _logger = logger;
        }

        [HttpGet]
        public Task<IActionResult> Estado() => Executar(() =>
        {
            if (!ConexaoMercadoPagoAcesso.PodeVer(HttpContext))
                throw PagamentoException.SemPermissao("Sem permissao para ver a configuracao de pagamentos.");
            return _conexao.EstadoAsync(Loja(), HttpContext.RequestAborted);
        }, _logger);

        [HttpPut]
        public Task<IActionResult> Configurar([FromBody] InfinitePayConfiguracaoRequest request) => Executar(() =>
        {
            var usuario = UsuarioGerente();
            return _conexao.ConfigurarAsync(Loja(), usuario, request, HttpContext.RequestAborted);
        }, _logger);

        private int UsuarioGerente()
        {
            var usuario = HttpContext.GetUserId() ?? throw new PagamentoException(PagamentoCodigos.NaoAutenticado, 401, "Usuario nao autenticado.");
            if (!ConexaoMercadoPagoAcesso.PodeGerenciar(HttpContext))
                throw PagamentoException.SemPermissao("So o dono ou o gerente da loja pode mudar a configuracao da InfinitePay.");
            return usuario;
        }

        private Guid Loja() => HttpContext.GetEstabelecimentoId() ?? throw PagamentoException.SemPermissao("Selecione uma loja.");
    }

    [ApiController]
    [Route("api/v2/integracoes/pagamentos/provedores")]
    public sealed class ProvedoresPagamentoController : PagamentoControllerBase
    {
        private readonly IPagamentoProvedorLojaService _provedores;
        private readonly ILogger<ProvedoresPagamentoController> _logger;

        public ProvedoresPagamentoController(IPagamentoProvedorLojaService provedores, ILogger<ProvedoresPagamentoController> logger)
        {
            _provedores = provedores;
            _logger = logger;
        }

        [HttpGet]
        public Task<IActionResult> Obter() => Executar(() =>
        {
            if (!ConexaoMercadoPagoAcesso.PodeVer(HttpContext))
                throw PagamentoException.SemPermissao("Sem permissao para ver os provedores de pagamento.");
            return _provedores.ObterAsync(Loja(), HttpContext.RequestAborted);
        }, _logger);

        [HttpPut("preferencias")]
        public Task<IActionResult> Salvar([FromBody] PreferenciasProvedorRequest request) => Executar(() =>
        {
            var usuario = HttpContext.GetUserId() ?? throw new PagamentoException(PagamentoCodigos.NaoAutenticado, 401, "Usuario nao autenticado.");
            if (!ConexaoMercadoPagoAcesso.PodeGerenciar(HttpContext))
                throw PagamentoException.SemPermissao("So o dono ou o gerente da loja pode mudar os provedores de pagamento.");
            return _provedores.SalvarAsync(Loja(), usuario, request, HttpContext.RequestAborted);
        }, _logger);

        private Guid Loja() => HttpContext.GetEstabelecimentoId() ?? throw PagamentoException.SemPermissao("Selecione uma loja.");
    }
}
