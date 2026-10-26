using System;
using System.Collections.Generic;

namespace APIBack.Payments.Assinaturas
{
    /// <summary>Codigos proprios das assinaturas (os de pagamento PAG_* seguem valendo). Tabela em CONTRATOS.md §8.4.</summary>
    public static class AssinaturaCodigos
    {
        public const string JaExiste = "ASS_JA_EXISTE";                         // 409
        public const string SemAssinatura = "ASS_SEM_ASSINATURA";               // 404
        public const string PlanoInvalido = "ASS_PLANO_INVALIDO";               // 422
        public const string PeriodicidadeIndisponivel = "ASS_PERIODICIDADE_INDISPONIVEL"; // 422
        public const string SemMudanca = "ASS_SEM_MUDANCA";                     // 409
        public const string PrevisaoDesatualizada = "ASS_PREVISAO_DESATUALIZADA"; // 409 (details.previsao)
        public const string CobrancaNoCartao = "ASS_COBRANCA_NO_CARTAO";        // 409 (o MP esta cobrando o cartao)
        public const string RenovacaoSemAutorizacao = "ASS_RENOVACAO_SEM_AUTORIZACAO"; // 422
        public const string ProdutoInvalido = "ASS_PRODUTO_INVALIDO";           // 422
        public const string JaDecidida = "ASS_SOLICITACAO_JA_DECIDIDA";         // 409
    }

    public sealed record PlanoDto(string Chave, string Nome, decimal Preco, decimal? PrecoAnual, string? Cor, string? Icone, string? Frase,
        IReadOnlyList<string> Modulos, int MotoboysBase, bool Atual);

    public sealed record ExtraDto(long Id, string Nome, decimal Preco, int Motoboys, IReadOnlyList<string> Modulos, string? Cor);

    public sealed record ComprovanteDto(Guid? IntentId, long? MpPaymentId, string? Metodo, string? CartaoBandeira, string? CartaoFinal,
        DateTimeOffset? AprovadoEm, decimal Valor, string Via);

    public sealed record CobrancaDto(Guid Id, string Tipo, string Descricao, decimal Valor, DateTimeOffset Vencimento, string Status, string? Meio,
        DateTimeOffset? PagoEm, string? PagoVia, decimal ValorPago, decimal ValorExcedente, bool Atrasada, int DiasAtraso, bool PodePagar,
        Guid? IntentId, ComprovanteDto? Comprovante, object? Detalhe, DateTimeOffset CriadoEm);

    public sealed record PlanoResumoDto(string Chave, string Nome, string? Cor, string? Icone);

    public sealed record MudancaAgendadaDto(string Plano, string Nome, string Periodicidade, decimal Preco, DateTimeOffset Em);

    public sealed record CancelamentoDto(DateTimeOffset SolicitadoEm, string? Motivo, DateTimeOffset? AcessoAte);

    public sealed record InadimplenciaDto(DateTimeOffset? Desde, int DiasAtraso, int ToleranciaDias, DateTimeOffset? SuspendeEm, DateTimeOffset? SuspensaEm,
        decimal ValorEmAberto);

    /// <summary>acesso: liberado | tolerancia | suspenso. Financeiro e separado da situacao comercial.</summary>
    public sealed record AssinaturaDto(Guid Id, PlanoResumoDto Plano, string Periodicidade, decimal Preco, string Status, string Financeiro, string Acesso,
        DateTimeOffset? PeriodoInicio, DateTimeOffset? PeriodoFim, DateTimeOffset? ProximoVencimento, CobrancaDto? ProximaCobranca,
        string? Metodo, bool RenovacaoAutomatica, string? CartaoFinal, string? CartaoBandeira, string? PreapprovalStatus,
        MudancaAgendadaDto? MudancaAgendada, CancelamentoDto? Cancelamento, InadimplenciaDto? Inadimplencia, int Versao, DateTimeOffset CriadaEm);

    public sealed record CompraDto(Guid Id, long ProdutoAvulsoId, string Nome, int Quantidade, decimal PrecoUnitario, decimal ValorTotal, int Motoboys,
        string Status, DateTimeOffset? VigenciaFim, Guid? CobrancaId, DateTimeOffset CriadaEm);

    public sealed record PessoaDto(int Id, string? Nome);

    public sealed record SolicitacaoDto(Guid Id, long ProdutoAvulsoId, string ProdutoNome, int Quantidade, decimal ValorEstimado, string? Motivo, string Status,
        PessoaDto Solicitante, PessoaDto? DecididoPor, DateTimeOffset? DecididoEm, string? DecisaoMotivo, Guid? CompraId, DateTimeOffset CriadaEm);

    public sealed record AvisoDto(long Id, string Tipo, string Mensagem, DateTimeOffset CriadoEm, bool Lido);

    public sealed record SituacaoResumoDto(string Situacao, DateTimeOffset? TesteTerminaEm, int? DiasTesteRestantes);

    /// <summary>Public key da conta recebedora da ZIPPY (o Card Brick tokeniza o cartao com ela). Nunca e da loja.</summary>
    public sealed record CheckoutDto(string? PublicKey, bool PixDisponivel, bool CartaoDisponivel, string Ambiente);

    public sealed record PainelAssinaturaDto(AssinaturaDto? Assinatura, SituacaoResumoDto Situacao, IReadOnlyList<PlanoDto> Planos, IReadOnlyList<ExtraDto> Extras,
        IReadOnlyList<CompraDto> Compras, IReadOnlyList<SolicitacaoDto> Solicitacoes, IReadOnlyList<AvisoDto> Avisos, CheckoutDto Checkout,
        bool PodeGerenciar, int ToleranciaDias);

    public sealed class ContratarRequest
    {
        public string? Plano { get; set; }
        public string? Periodicidade { get; set; }
        public string? IdempotencyKey { get; set; }
    }

    public sealed class AlterarRequest
    {
        public string? Plano { get; set; }
        public string? Periodicidade { get; set; }
        /// <summary>false (padrao) = so a previsao financeira, nada muda.</summary>
        public bool Confirmar { get; set; }
        /// <summary>O valorAgora que a tela mostrou: se o calculo mudou (virou o dia), 409 ASS_PREVISAO_DESATUALIZADA com a conta nova.</summary>
        public decimal? ValorPrevisto { get; set; }
    }

    public sealed class CancelarRequest
    {
        public string? Motivo { get; set; }
    }

    public sealed class CartaoCheckoutRequest
    {
        public string? Token { get; set; }
        public string? PaymentMethodId { get; set; }
        public string? IssuerId { get; set; }
        public int Parcelas { get; set; } = 1;
        public APIBack.Payments.MercadoPago.PagadorInfo? Pagador { get; set; }
    }

    public sealed class PagarCobrancaRequest
    {
        /// <summary>pix | cartao</summary>
        public string? Metodo { get; set; }
        public CartaoCheckoutRequest? Cartao { get; set; }
        /// <summary>Cartao com renovacao automatica (preapproval do MP). Exige AutorizoRenovacao = true.</summary>
        public bool RenovacaoAutomatica { get; set; }
        public bool AutorizoRenovacao { get; set; }
    }

    public sealed record PixCheckoutDto(Guid IntentId, string Status, string? CopiaECola, string? QrCodeBase64, string? TicketUrl, DateTime? ExpiraEm, bool Reaproveitado);

    public sealed record CartaoCheckoutDto(Guid? IntentId, string Status, string? StatusPagamento, string? StatusDetail, bool Aprovado);

    public sealed record RecorrenteCheckoutDto(string? PreapprovalStatus, DateTimeOffset? PrimeiraCobrancaEm, string? CartaoFinal);

    public sealed record CheckoutCobrancaDto(Guid CobrancaId, string Metodo, string StatusCobranca, decimal Valor, PixCheckoutDto? Pix,
        CartaoCheckoutDto? Cartao, RecorrenteCheckoutDto? Recorrente);

    public sealed class CompraRequest
    {
        public long ProdutoAvulsoId { get; set; }
        public int Quantidade { get; set; } = 1;
        public string? IdempotencyKey { get; set; }
    }

    public sealed record CompraCriadaDto(CompraDto Compra, CobrancaDto Cobranca, bool Reaproveitada);

    public sealed class SolicitacaoRequest
    {
        public long ProdutoAvulsoId { get; set; }
        public int Quantidade { get; set; } = 1;
        public string? Motivo { get; set; }
    }

    public sealed class DecisaoRequest
    {
        public bool Aprovar { get; set; }
        public string? Motivo { get; set; }
    }

    public sealed record DecisaoDto(SolicitacaoDto Solicitacao, CompraCriadaDto? Compra);

    public sealed record AlteracaoDto(PrevisaoMudanca Previsao, bool Confirmada, CobrancaDto? Cobranca, AssinaturaDto? Assinatura);

    public sealed class BaixaManualRequest
    {
        public string? Motivo { get; set; }
    }
}
