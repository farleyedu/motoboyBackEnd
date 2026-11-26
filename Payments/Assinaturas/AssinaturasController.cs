using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.Attributes;
using APIBack.Extensions;
using APIBack.Payments.Controllers;
using APIBack.Payments.MercadoPago;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace APIBack.Payments.Assinaturas
{
    /// <summary>
    /// Assinatura da loja do token (CONTRATOS.md §8.4). Liberadas mesmo com a loja bloqueada pela Gestão (/api/v2/comercial), para regularizar.
    /// Ver e pedir compra: qualquer usuario da loja. Contratar, trocar, cancelar, pagar, comprar e aprovar: Configuracoes:assinatura (gerente/dono).
    /// </summary>
    [ApiController]
    [Route("api/v2/comercial")]
    public sealed class AssinaturasController : PagamentoControllerBase
    {
        private const string Modulo = "Configuracoes";
        private const string Acao = "assinatura";
        private readonly IAssinaturaService _service;
        private readonly ILogger<AssinaturasController> _logger;

        public AssinaturasController(IAssinaturaService service, ILogger<AssinaturasController> logger)
        {
            _service = service;
            _logger = logger;
        }

        private Guid Loja() => HttpContext.GetEstabelecimentoId() ?? throw PagamentoException.SemPermissao("Selecione uma loja.");
        private int? Usuario() => HttpContext.GetUserId();
        private bool PodeGerenciar() => HttpContext.IsSuperAdmin() || HttpContext.TemPermissao(Modulo, Acao);

        [HttpGet("assinatura")]
        public Task<IActionResult> Obter()
        {
            Response.Headers.CacheControl = "no-store";
            return Executar(() => _service.PainelAsync(Loja(), PodeGerenciar(), HttpContext.RequestAborted), _logger);
        }

        [HttpPost("assinaturas")]
        [RequirePermission(Modulo, Acao)]
        public Task<IActionResult> Contratar([FromBody] ContratarRequest body) =>
            Executar(() => _service.ContratarAsync(Loja(), body ?? new ContratarRequest(), Usuario(), HttpContext.RequestAborted), _logger, 201);

        /// <summary>Sem "confirmar": devolve so a previsao financeira (quanto paga agora, a partir de quando, a conta passo a passo).</summary>
        [HttpPost("assinaturas/alterar")]
        [RequirePermission(Modulo, Acao)]
        public Task<IActionResult> Alterar([FromBody] AlterarRequest body) =>
            Executar(() => _service.AlterarAsync(Loja(), body ?? new AlterarRequest(), Usuario(), HttpContext.RequestAborted), _logger);

        [HttpPost("assinaturas/cancelar")]
        [RequirePermission(Modulo, Acao)]
        public Task<IActionResult> Cancelar([FromBody] CancelarRequest body) =>
            Executar(() => _service.CancelarAsync(Loja(), body ?? new CancelarRequest(), Usuario(), HttpContext.RequestAborted), _logger);

        /// <summary>Desfaz o cancelamento antes do fim do periodo.</summary>
        [HttpPost("assinaturas/retomar")]
        [RequirePermission(Modulo, Acao)]
        public Task<IActionResult> Retomar() => Executar(() => _service.RetomarAsync(Loja(), Usuario(), HttpContext.RequestAborted), _logger);

        /// <summary>Desfaz a troca marcada para o próximo vencimento.</summary>
        [HttpPost("assinaturas/desfazer-troca")]
        [RequirePermission(Modulo, Acao)]
        public Task<IActionResult> DesfazerTroca() => Executar(() => _service.DesfazerTrocaAsync(Loja(), Usuario(), HttpContext.RequestAborted), _logger);

        /// <summary>Liga ou troca o cartão com cobrança automática.</summary>
        [HttpPost("assinaturas/cartao")]
        [RequirePermission(Modulo, Acao)]
        public Task<IActionResult> LigarCartao([FromBody] CartaoAutomaticoRequest body) =>
            Executar(() => _service.LigarCartaoAsync(Loja(), body ?? new CartaoAutomaticoRequest(), Usuario(), HttpContext.RequestAborted), _logger);

        [HttpDelete("assinaturas/cartao")]
        [RequirePermission(Modulo, Acao)]
        public Task<IActionResult> DesligarCartao() => Executar(() => _service.DesligarCartaoAsync(Loja(), Usuario(), HttpContext.RequestAborted), _logger);

        [HttpGet("cobrancas")]
        public Task<IActionResult> Cobrancas()
        {
            Response.Headers.CacheControl = "no-store";
            return Executar(() => _service.CobrancasAsync(Loja(), HttpContext.RequestAborted), _logger);
        }

        /// <summary>Checkout da cobranca que ja existe (Pix, cartao avulso ou cartao com renovacao autorizada). Nunca cria divida nova.</summary>
        [HttpPost("cobrancas/{id:guid}/pagar")]
        [RequirePermission(Modulo, Acao)]
        public Task<IActionResult> Pagar(Guid id, [FromBody] PagarCobrancaRequest body) =>
            Executar(() => _service.PagarAsync(Loja(), id, body ?? new PagarCobrancaRequest(), Usuario(), HttpContext.RequestAborted), _logger);

        [HttpPost("compras")]
        [RequirePermission(Modulo, Acao)]
        public Task<IActionResult> Comprar([FromBody] CompraRequest body)
        {
            var corpo = body ?? new CompraRequest();
            corpo.IdempotencyKey ??= Request.Headers["Idempotency-Key"].ToString() is { Length: > 0 } h ? h : null;
            return Executar(() => _service.ComprarAsync(Loja(), corpo, Usuario(), null, HttpContext.RequestAborted), _logger, 201);
        }

        [HttpGet("solicitacoes-compra")]
        public Task<IActionResult> Solicitacoes() => Executar(() => _service.SolicitacoesAsync(Loja(), HttpContext.RequestAborted), _logger);

        [HttpPost("solicitacoes-compra")]
        public Task<IActionResult> Solicitar([FromBody] SolicitacaoRequest body) =>
            Executar(() => _service.SolicitarAsync(Loja(), body ?? new SolicitacaoRequest(),
                Usuario() ?? throw new PagamentoException(PagamentoCodigos.NaoAutenticado, 401, "Entre de novo."), HttpContext.RequestAborted), _logger, 201);

        [HttpPost("solicitacoes-compra/{id:guid}/decisao")]
        [RequirePermission(Modulo, Acao)]
        public Task<IActionResult> Decidir(Guid id, [FromBody] DecisaoRequest body) =>
            Executar(() => _service.DecidirAsync(Loja(), id, body ?? new DecisaoRequest(), Usuario(), HttpContext.RequestAborted), _logger);

        [HttpPost("avisos/lidos")]
        public Task<IActionResult> AvisosLidos() =>
            Executar(async () => { await _service.MarcarAvisosLidosAsync(Loja(), HttpContext.RequestAborted); return new { ok = true }; }, _logger);
    }

    /// <summary>Gestao (super admin): a assinatura de uma loja, as cobrancas e a baixa manual (dinheiro recebido fora do MP).</summary>
    [ApiController]
    public sealed class GestaoAssinaturasController : PagamentoControllerBase
    {
        private readonly IAssinaturaService _service;
        private readonly IGestaoAssinaturaService _gestao;
        private readonly ILogger<GestaoAssinaturasController> _logger;

        public GestaoAssinaturasController(IAssinaturaService service, IGestaoAssinaturaService gestao, ILogger<GestaoAssinaturasController> logger)
        {
            _service = service;
            _gestao = gestao;
            _logger = logger;
        }

        /// <summary>Toda ação daqui exige super admin; quem fez fica registrado.</summary>
        private Task<IActionResult> Gestao<T>(Func<int?, Task<T>> acao) =>
            Executar(() => { ExigirGestao(); return acao(HttpContext.GetUserId()); }, _logger);

        private void ExigirGestao()
        {
            if (!HttpContext.GetUserId().HasValue) throw new PagamentoException(PagamentoCodigos.NaoAutenticado, 401, "Entre de novo.");
            if (!HttpContext.IsSuperAdmin()) throw PagamentoException.SemPermissao("Só a Gestão da Zippy.");
        }

        [HttpGet("api/gestao/estabelecimentos/{id:guid}/assinatura")]
        public Task<IActionResult> Assinatura(Guid id) =>
            Executar(() => { ExigirGestao(); return _service.PainelAsync(id, false, HttpContext.RequestAborted); }, _logger);

        [HttpGet("api/gestao/estabelecimentos/{id:guid}/assinatura/cobrancas")]
        public Task<IActionResult> Cobrancas(Guid id) =>
            Executar(() => { ExigirGestao(); return _service.CobrancasAsync(id, HttpContext.RequestAborted); }, _logger);

        [HttpPost("api/gestao/comercial/cobrancas/{id:guid}/baixa-manual")]
        public Task<IActionResult> BaixaManual(Guid id, [FromBody] BaixaManualRequest? body) =>
            Gestao(u => _service.BaixaManualAsync(id, body ?? new BaixaManualRequest(), u, HttpContext.RequestAborted));

        [HttpGet("api/gestao/assinaturas")]
        public Task<IActionResult> Lista([FromQuery] string? estado, [FromQuery] string? mesVigente, [FromQuery] string? busca)
        {
            Response.Headers.CacheControl = "no-store";
            return Gestao(_ => _gestao.ListarAsync(estado, mesVigente, busca, HttpContext.RequestAborted));
        }

        [HttpGet("api/gestao/assinaturas/excedentes")]
        public Task<IActionResult> Excedentes()
        {
            Response.Headers.CacheControl = "no-store";
            return Gestao(_ => _gestao.ExcedentesAsync(HttpContext.RequestAborted));
        }

        [HttpPost("api/gestao/estabelecimentos/{id:guid}/assinatura")]
        public Task<IActionResult> Criar(Guid id, [FromBody] GestaoCriarAssinaturaRequest body) =>
            Gestao(u => _gestao.CriarAsync(id, body ?? new GestaoCriarAssinaturaRequest(), u, HttpContext.RequestAborted));

        [HttpPost("api/gestao/estabelecimentos/{id:guid}/assinatura/alterar")]
        public Task<IActionResult> Alterar(Guid id, [FromBody] GestaoAlterarAssinaturaRequest body) =>
            Gestao(u => _gestao.AlterarAsync(id, body ?? new GestaoAlterarAssinaturaRequest(), u, HttpContext.RequestAborted));

        [HttpPost("api/gestao/estabelecimentos/{id:guid}/assinatura/cancelar")]
        public Task<IActionResult> Cancelar(Guid id, [FromBody] GestaoCancelarAssinaturaRequest? body) =>
            Gestao(u => _gestao.CancelarAsync(id, body ?? new GestaoCancelarAssinaturaRequest(), u, HttpContext.RequestAborted));

        [HttpPost("api/gestao/estabelecimentos/{id:guid}/assinatura/retomar")]
        public Task<IActionResult> Retomar(Guid id, [FromBody] GestaoMotivoRequest? body) =>
            Gestao(u => _gestao.RetomarAsync(id, body ?? new GestaoMotivoRequest(), u, HttpContext.RequestAborted));

        [HttpPost("api/gestao/estabelecimentos/{id:guid}/extras")]
        public Task<IActionResult> Extra(Guid id, [FromBody] GestaoExtraRequest body) =>
            Gestao(u => _gestao.ExtraAsync(id, body ?? new GestaoExtraRequest(), u, HttpContext.RequestAborted));

        [HttpPost("api/gestao/comercial/cobrancas/{id:guid}/cancelar")]
        public Task<IActionResult> CancelarCobranca(Guid id, [FromBody] GestaoMotivoRequest? body) =>
            Gestao(u => _gestao.CancelarCobrancaAsync(id, body ?? new GestaoMotivoRequest(), u, HttpContext.RequestAborted));

        [HttpPost("api/gestao/comercial/cobrancas/{id:guid}/vencimento")]
        public Task<IActionResult> Vencimento(Guid id, [FromBody] GestaoVencimentoRequest body) =>
            Gestao(u => _gestao.VencimentoAsync(id, body ?? new GestaoVencimentoRequest(), u, HttpContext.RequestAborted));

        [HttpPost("api/gestao/comercial/cobrancas/{id:guid}/excedente-devolvido")]
        public Task<IActionResult> ExcedenteDevolvido(Guid id, [FromBody] GestaoMotivoRequest? body) =>
            Gestao(u => _gestao.ExcedenteDevolvidoAsync(id, body ?? new GestaoMotivoRequest(), u, HttpContext.RequestAborted));
    }
}
