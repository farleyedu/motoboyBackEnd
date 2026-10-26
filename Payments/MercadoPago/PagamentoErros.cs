using System;
using System.Collections.Generic;

namespace APIBack.Payments.MercadoPago
{
    /// <summary>Codigos estaveis de pagamento (o front decide texto e acao por eles). Tabela completa em docs/pagamentos/CONTRATOS.md.</summary>
    public static class PagamentoCodigos
    {
        public const string RequisicaoInvalida = "PAG_REQUISICAO_INVALIDA";            // 400
        public const string NaoAutenticado = "PAG_NAO_AUTENTICADO";                     // 401
        public const string WebhookAssinaturaInvalida = "PAG_WEBHOOK_ASSINATURA_INVALIDA"; // 401
        public const string SemPermissao = "PAG_SEM_PERMISSAO";                         // 403
        public const string NaoEncontrado = "PAG_NAO_ENCONTRADO";                       // 404
        public const string IdempotenciaConflito = "PAG_IDEMPOTENCIA_CONFLITO";         // 409
        public const string EstadoInvalido = "PAG_ESTADO_INVALIDO";                     // 409
        public const string JaPago = "PAG_JA_PAGO";                                     // 409
        public const string CobrancaAberta = "PAG_COBRANCA_ABERTA";                     // 409
        public const string ValorInvalido = "PAG_VALOR_INVALIDO";                       // 422
        public const string PixDesativado = "PAG_PIX_DESATIVADO";                       // 422
        public const string CartaoDesativado = "PAG_CARTAO_DESATIVADO";                 // 422
        public const string CartaoNaoPermitidoNoChat = "PAG_CARTAO_NAO_PERMITIDO_NO_CHAT"; // 422
        public const string CartaoRecusado = "PAG_CARTAO_RECUSADO";                     // 422
        public const string ContaLojaNaoConectada = "PAG_CONTA_LOJA_NAO_CONECTADA";     // 422
        public const string ContaLojaReautorizar = "PAG_CONTA_LOJA_REAUTORIZAR";        // 422
        public const string PedidoNaoPagavel = "PAG_PEDIDO_NAO_PAGAVEL";                // 422
        public const string EstornoAcimaDoPago = "PAG_ESTORNO_ACIMA_DO_PAGO";           // 422
        public const string CredencialIncompativel = "PAG_CREDENCIAL_INCOMPATIVEL";     // 500 (bug: nunca deveria acontecer)
        public const string LimiteRequisicoes = "PAG_LIMITE_REQUISICOES";               // 429
        public const string MercadoPagoErro = "PAG_MP_ERRO";                            // 502
        public const string MercadoPagoRespostaInconsistente = "PAG_MP_RESPOSTA_INCONSISTENTE"; // 502
        public const string MercadoPagoIndisponivel = "PAG_MP_INDISPONIVEL";            // 503
        public const string NaoConfigurado = "PAG_NAO_CONFIGURADO";                     // 503
    }

    /// <summary>Erro de pagamento com codigo estavel e status HTTP. Nunca carrega token, segredo ou dado de cartao.</summary>
    public sealed class PagamentoException : Exception
    {
        public PagamentoException(string codigo, int http, string mensagem, IDictionary<string, object?>? detalhes = null, Exception? inner = null)
            : base(mensagem, inner)
        {
            Codigo = codigo;
            Http = http;
            Detalhes = detalhes;
        }

        public string Codigo { get; }
        public int Http { get; }
        public IDictionary<string, object?>? Detalhes { get; }

        public static PagamentoException Invalida(string mensagem, string? campo = null) =>
            new(PagamentoCodigos.RequisicaoInvalida, 400, mensagem, campo == null ? null : new Dictionary<string, object?> { ["campo"] = campo });

        public static PagamentoException NaoEncontrado(string mensagem = "Pagamento nao encontrado.") =>
            new(PagamentoCodigos.NaoEncontrado, 404, mensagem);

        public static PagamentoException SemPermissao(string mensagem = "Sem permissao para este pagamento.") =>
            new(PagamentoCodigos.SemPermissao, 403, mensagem);

        public static PagamentoException Estado(string mensagem, string? statusAtual = null) =>
            new(PagamentoCodigos.EstadoInvalido, 409, mensagem, statusAtual == null ? null : new Dictionary<string, object?> { ["status"] = statusAtual });

        public static PagamentoException Regra(string codigo, string mensagem) => new(codigo, 422, mensagem);

        public static PagamentoException NaoConfigurado(string mensagem) => new(PagamentoCodigos.NaoConfigurado, 503, mensagem);
    }
}
