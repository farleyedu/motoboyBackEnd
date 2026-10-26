using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using APIBack.Attributes;
using APIBack.DTOs.Common;
using APIBack.Extensions;
using APIBack.Payments.MercadoPago;
using APIBack.Service;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace APIBack.Payments.Controllers
{
    /// <summary>Traduz PagamentoException para { success, error, code, details } com o status HTTP do codigo.</summary>
    public abstract class PagamentoControllerBase : ControllerBase
    {
        protected async Task<IActionResult> Executar<T>(Func<Task<T>> operacao, ILogger logger, int sucesso = StatusCodes.Status200OK)
        {
            try
            {
                return StatusCode(sucesso, ApiResponse<T>.Ok(await operacao()));
            }
            catch (PagamentoException ex)
            {
                if (ex.Http >= 500) logger.LogWarning("[pag.api] ev=erro rota={Rota} codigo={Codigo}", Request.Path.Value, ex.Codigo);
                return StatusCode(ex.Http, ApiResponse<object>.Fail(ex.Message, ex.Codigo, ex.Detalhes));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[pag.api] ev=erro_inesperado rota={Rota}", Request.Path.Value);
                return StatusCode(500, ApiResponse<object>.Fail("Nao foi possivel concluir a operacao de pagamento."));
            }
        }
    }

    /// <summary>Webhook do Mercado Pago. So responde 2xx depois de gravar o evento; 401 assinatura, 400 corpo, 503 sem segredo configurado.</summary>
    [ApiController]
    [AllowAnonymous]
    [Route("api/webhooks/mercadopago")]
    public sealed class MercadoPagoWebhookController : ControllerBase
    {
        private const int CorpoMaximo = 64 * 1024;
        private readonly IMercadoPagoWebhookService _webhook;

        public MercadoPagoWebhookController(IMercadoPagoWebhookService webhook) => _webhook = webhook;

        [HttpPost]
        [RequestSizeLimit(CorpoMaximo)]
        public async Task<IActionResult> Receber()
        {
            string corpo;
            using (var leitor = new StreamReader(Request.Body, Encoding.UTF8))
                corpo = await leitor.ReadToEndAsync(HttpContext.RequestAborted);

            var r = await _webhook.ReceberAsync(new MpWebhookEntrada
            {
                Assinatura = Request.Headers["x-signature"].ToString(),
                RequestId = Request.Headers["x-request-id"].ToString(),
                QueryDataId = Request.Query["data.id"].ToString(),
                QueryTipo = Request.Query["type"].FirstOrDefault() ?? Request.Query["topic"].FirstOrDefault(),
                CorpoBruto = corpo
            }, HttpContext.RequestAborted);

            return r.Desfecho switch
            {
                WebhookDesfecho.Aceito => Ok(new { recebido = true, evento = r.EventoId, status = r.StatusProcessamento }),
                WebhookDesfecho.Duplicado => Ok(new { recebido = true, duplicado = true }),
                WebhookDesfecho.NaoAutorizado => StatusCode(401, ApiResponse<object>.Fail("Assinatura invalida.", PagamentoCodigos.WebhookAssinaturaInvalida)),
                WebhookDesfecho.NaoConfigurado => StatusCode(503, ApiResponse<object>.Fail("Webhook nao configurado.", PagamentoCodigos.NaoConfigurado)),
                _ => BadRequest(ApiResponse<object>.Fail("Aviso invalido.", PagamentoCodigos.RequisicaoInvalida))
            };
        }
    }

    public sealed class PagamentoResumoDto
    {
        public Guid Id { get; set; }
        public string Origem { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public int Versao { get; set; }
        public decimal Valor { get; set; }
        public decimal ValorPago { get; set; }
        public decimal ValorEstornado { get; set; }
        public string Descricao { get; set; } = string.Empty;
        public int? PedidoId { get; set; }
        public Guid? PrePedidoId { get; set; }
        public Guid? ConversaId { get; set; }
        public string? Metodo { get; set; }
        public string? StatusPagamento { get; set; }
        public string? StatusDetalhe { get; set; }
        public string? CartaoBandeira { get; set; }
        public string? CartaoFinal { get; set; }
        public DateTime? ExpiraEm { get; set; }
        public DateTime? PagoEm { get; set; }
        public DateTime CriadoEm { get; set; }
        public DateTime AtualizadoEm { get; set; }
    }

    public sealed class EstornarRequest
    {
        public decimal? Valor { get; set; }
        public string? Motivo { get; set; }
        public string? IdempotencyKey { get; set; }
    }

    /// <summary>Consulta e estorno de pagamentos da loja do usuario (substitui o antigo /api/checkout do Asaas).</summary>
    [ApiController]
    [Route("api/v2/pagamentos")]
    public sealed class PagamentosController : PagamentoControllerBase
    {
        private readonly NpgsqlDataSource _db;
        private readonly IPagamentoEstornoService _estornos;
        private readonly ILogger<PagamentosController> _logger;

        public PagamentosController(NpgsqlDataSource db, IPagamentoEstornoService estornos, ILogger<PagamentosController> logger)
        {
            _db = db;
            _estornos = estornos;
            _logger = logger;
        }

        private const string Sql = @"
SELECT i.id, i.origem, i.status, i.versao, i.valor, i.valor_pago, i.valor_estornado, i.descricao, i.pedido_id, i.pre_pedido_id, i.conversa_id,
       p.metodo, p.status AS status_pagamento, p.mp_status_detail AS status_detalhe, p.cartao_bandeira, p.cartao_final,
       COALESCE(p.expira_em, i.expira_em) AS expira_em, i.pago_em, i.created_at AS criado_em, i.updated_at AS atualizado_em
  FROM pagamento_intent i
  LEFT JOIN LATERAL (SELECT * FROM pagamento x WHERE x.id_intent = i.id ORDER BY x.created_at DESC LIMIT 1) p ON TRUE
 WHERE i.id_estabelecimento = @Loja";

        [HttpGet("{intentId:guid}")]
        [RequirePermission("Financeiro", "visualizar")]
        public Task<IActionResult> Obter(Guid intentId) => Executar(async () =>
        {
            var loja = Loja();
            await using var cx = await _db.OpenConnectionAsync(HttpContext.RequestAborted);
            return await cx.QueryFirstOrDefaultAsync<PagamentoResumoDto>(Sql + " AND i.id = @Id", new { Loja = loja, Id = intentId })
                   ?? throw PagamentoException.NaoEncontrado();
        }, _logger);

        [HttpGet]
        [RequirePermission("Financeiro", "visualizar")]
        public Task<IActionResult> Listar([FromQuery] int? pedidoId, [FromQuery] Guid? prePedidoId, [FromQuery] Guid? conversaId) => Executar(async () =>
        {
            if (pedidoId == null && prePedidoId == null && conversaId == null)
                throw PagamentoException.Invalida("Informe pedidoId, prePedidoId ou conversaId.");
            await using var cx = await _db.OpenConnectionAsync(HttpContext.RequestAborted);
            return (await cx.QueryAsync<PagamentoResumoDto>(Sql + @"
   AND (@PedidoId::int IS NULL OR i.pedido_id = @PedidoId) AND (@Pre::uuid IS NULL OR i.pre_pedido_id = @Pre)
   AND (@Conversa::uuid IS NULL OR i.conversa_id = @Conversa)
 ORDER BY i.created_at DESC LIMIT 50", new { Loja = Loja(), PedidoId = pedidoId, Pre = prePedidoId, Conversa = conversaId })).ToList();
        }, _logger);

        [HttpPost("{intentId:guid}/estornos")]
        [RequirePermission("Financeiro", "cancelar")]
        public Task<IActionResult> Estornar(Guid intentId, [FromBody] EstornarRequest? body) => Executar(() => _estornos.EstornarAsync(new EstornoRequest
        {
            IntentId = intentId, IdEstabelecimento = Loja(), Valor = body?.Valor, Motivo = body?.Motivo?.Trim(), UsuarioId = HttpContext.GetUserId(),
            IdempotencyKey = string.IsNullOrWhiteSpace(body?.IdempotencyKey) ? Request.Headers["Idempotency-Key"].ToString() : body!.IdempotencyKey!
        }, HttpContext.RequestAborted), _logger);

        private Guid Loja() => HttpContext.GetEstabelecimentoId() ?? throw PagamentoException.SemPermissao("Selecione uma loja.");
    }

    public sealed class PublicPixPedidoRequest
    {
        /// <summary>Codigo do pedido mostrado ao cliente (segundo fator junto com o id).</summary>
        public string? Codigo { get; set; }
    }

    public sealed class PublicCartaoPedidoRequest
    {
        public string? Codigo { get; set; }
        public string? Token { get; set; }
        public string? PaymentMethodId { get; set; }
        public string? IssuerId { get; set; }
        public int Parcelas { get; set; } = 1;
        public PagadorInfo? Pagador { get; set; }
    }

    public sealed class PublicPixDto
    {
        public string Status { get; set; } = string.Empty;
        public int Versao { get; set; }
        public decimal Valor { get; set; }
        public string Descricao { get; set; } = string.Empty;
        public string? CopiaECola { get; set; }
        public string? QrCodeBase64 { get; set; }
        public DateTime? ExpiraEm { get; set; }
        public string? LojaNome { get; set; }
    }

    /// <summary>
    /// Rotas publicas (sem login) do pagamento: cardapio web (Pix e cartao do pre-pedido) e a pagina Pix aberta pelo link. O valor
    /// SEMPRE vem do servidor (total do pre-pedido / da intencao); o corpo nunca carrega valor. Sem dados de cliente nas respostas.
    /// </summary>
    [ApiController]
    [AllowAnonymous]
    [EnableRateLimiting(PagamentosRegistro.PoliticaPublica)]
    [Route("api/public")]
    public sealed class PublicPagamentosController : PagamentoControllerBase
    {
        private readonly NpgsqlDataSource _db;
        private readonly IPixChargeService _pix;
        private readonly ICardPaymentService _cartao;
        private readonly ILogger<PublicPagamentosController> _logger;

        public PublicPagamentosController(NpgsqlDataSource db, IPixChargeService pix, ICardPaymentService cartao, ILogger<PublicPagamentosController> logger)
        {
            _db = db;
            _pix = pix;
            _cartao = cartao;
            _logger = logger;
        }

        [HttpGet("cardapio/{estabelecimentoId:guid}/pagamentos/config")]
        public Task<IActionResult> Configuracao(Guid estabelecimentoId) => Executar(() => _cartao.ConfiguracaoAsync(estabelecimentoId, HttpContext.RequestAborted), _logger);

        // Cardapio web: as regras do pedido (so pedido online, status que aceita pagamento, Pix novo depois de vencido, cancelar o Pix
        // antes do cartao, vinculo com o pre-pedido) ficam em ICardapioPagamentoService (sessao 3). O valor e sempre o do pre-pedido.
        [HttpPost("cardapio/pedidos/{prePedidoId:guid}/pagamentos/pix")]
        public Task<IActionResult> PixDoPedido(Guid prePedidoId, [FromBody] PublicPixPedidoRequest? body,
            [FromServices] APIBack.Service.Interface.ICardapioPagamentoService cardapio) =>
            Executar(() => cardapio.CriarPixAsync(prePedidoId, body?.Codigo, HttpContext.RequestAborted), _logger);

        [HttpPost("cardapio/pedidos/{prePedidoId:guid}/pagamentos/cartao")]
        public Task<IActionResult> CartaoDoPedido(Guid prePedidoId, [FromBody] PublicCartaoPedidoRequest? body,
            [FromServices] APIBack.Service.Interface.ICardapioPagamentoService cardapio) => Executar(async () =>
        {
            var r = await cardapio.PagarComCartaoAsync(prePedidoId, body ?? new PublicCartaoPedidoRequest(), HttpContext.RequestAborted);
            return new { r.IntentId, r.Status, r.StatusPagamento, r.StatusDetail, r.Aprovado, r.Versao, r.Valor };
        }, _logger);

        [HttpGet("cardapio/pedidos/{prePedidoId:guid}/pagamento")]
        public Task<IActionResult> StatusDoPedido(Guid prePedidoId, [FromQuery] string? codigo,
            [FromServices] APIBack.Service.Interface.ICardapioPagamentoService cardapio) =>
            Executar(() => cardapio.ObterPagamentoPublicoAsync(prePedidoId, codigo, HttpContext.RequestAborted), _logger);

        /// <summary>Pagina Pix publica do link (token opaco). So valor, descricao, nome da loja, QR e status.</summary>
        [HttpGet("pagamentos/pix/{token}")]
        public Task<IActionResult> PaginaPix(string token) => Executar(async () =>
        {
            if (!TokenPublico.FormatoValido(token)) throw PagamentoException.NaoEncontrado("Link de pagamento invalido.");
            await using var cx = await _db.OpenConnectionAsync(HttpContext.RequestAborted);
            var achado = await cx.QueryFirstOrDefaultAsync<(Guid Id, Guid Loja, string? Nome)?>(@"
SELECT i.id, i.id_estabelecimento, COALESCE(NULLIF(w.nome_publico, ''), e.nome_fantasia)
  FROM pagamento_intent i JOIN estabelecimentos e ON e.id = i.id_estabelecimento
  LEFT JOIN cardapio_web_config w ON w.id_estabelecimento = i.id_estabelecimento
 WHERE i.token_publico_hash = @H", new { H = TokenPublico.Hash(token) });
            if (achado == null) throw PagamentoException.NaoEncontrado("Link de pagamento invalido.");
            return Publico(await _pix.ObterAsync(achado.Value.Id, achado.Value.Loja, HttpContext.RequestAborted), achado.Value.Nome);
        }, _logger);

        private static PublicPixDto Publico(PixChargeResult r, string? loja) => new()
        {
            Status = r.Status, Versao = r.Versao, Valor = r.Valor, Descricao = r.Descricao, CopiaECola = r.CopiaECola, QrCodeBase64 = r.QrCodeBase64,
            ExpiraEm = r.ExpiraEm, LojaNome = loja
        };
    }
}
