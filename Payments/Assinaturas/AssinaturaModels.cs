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
        public const string Devendo = "ASS_DEVENDO";                            // 409
        public const string AnualSoGestao = "ASS_ANUAL_SO_GESTAO";              // 409
        public const string ExtraSemPlano = "ASS_EXTRA_SEM_PLANO";              // 409
        public const string ModuloDevendo = "ASS_MODULO_DEVENDO";               // 409
        public const string CanceladaRetome = "ASS_CANCELADA_RETOME";           // 409
        public const string SemCobranca = "ASS_SEM_COBRANCA";                   // so em acoes.pagar
        public const string TentativaExpirada = "ASS_TENTATIVA_EXPIRADA";       // 409
    }

    public sealed record PlanoDto(string Chave, string Nome, decimal Preco, decimal? PrecoAnual, string? Cor, string? Icone, string? Frase,
        IReadOnlyList<string> Modulos, int MotoboysBase, bool Atual);

    public sealed record ExtraDto(long Id, string Nome, decimal Preco, int Motoboys, IReadOnlyList<string> Modulos, string? Cor);

    public sealed record ComprovanteDto(Guid? IntentId, long? MpPaymentId, string? Metodo, string? CartaoBandeira, string? CartaoFinal,
        DateTimeOffset? AprovadoEm, decimal Valor, string Via);

    public sealed record CobrancaDto(Guid Id, string Tipo, string Descricao, decimal Valor, DateTimeOffset Vencimento, string Status, string? Meio,
        DateTimeOffset? PagoEm, string? PagoVia, decimal ValorPago, decimal ValorExcedente, bool Atrasada, int DiasAtraso, bool PodePagar,
        Guid? IntentId, ComprovanteDto? Comprovante, object? Detalhe, DateTimeOffset CriadoEm,
        /// <summary>Tentativa de upgrade ou extra: depois disto, sem pagamento, ela some.</summary>
        DateTimeOffset? ExpiraEm = null,
        /// <summary>Está contando como dívida agora (mensalidade vencida, ou a dívida somada na volta).</summary>
        bool ContaComoDivida = false);

    public sealed record PlanoResumoDto(string Chave, string Nome, string? Cor, string? Icone);

    public sealed record MudancaAgendadaDto(string Plano, string Nome, string Periodicidade, decimal Preco, DateTimeOffset Em);

    public sealed record CancelamentoDto(DateTimeOffset SolicitadoEm, string? Motivo, DateTimeOffset? AcessoAte);

    public sealed record InadimplenciaDto(DateTimeOffset? Desde, int DiasAtraso, int ToleranciaDias, DateTimeOffset? SuspendeEm, DateTimeOffset? SuspensaEm,
        decimal ValorEmAberto);

    /// <summary>acesso: liberado | tolerancia (devendo). Nunca suspenso: so a Gestao bloqueia. Financeiro e separado da situacao comercial.</summary>
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
    public sealed record CheckoutDto(string? PublicKey, bool PixDisponivel, bool CartaoDisponivel, string Ambiente, bool LinkDisponivel = false);

    /// <summary>Cartão com cobrança automática (preapproval do MP).</summary>
    public sealed record CartaoAssinaturaDto(bool Automatico, string? Bandeira, string? Final);

    /// <summary>Bloqueio manual da Gestão: o único bloqueio que existe.</summary>
    public sealed record BloqueioDto(DateTimeOffset Em, string? Motivo, PessoaDto? Por);

    /// <summary>"Assinatura" é nula quando a loja não tem assinatura viva (inclusive a encerrada: o histórico está nas cobranças).
    /// ToleranciaDias é sempre 0: não existe prazo para suspender.</summary>
    public sealed record PainelAssinaturaDto(AssinaturaDto? Assinatura, SituacaoResumoDto Situacao, IReadOnlyList<PlanoDto> Planos, IReadOnlyList<ExtraDto> Extras,
        IReadOnlyList<CompraDto> Compras, IReadOnlyList<SolicitacaoDto> Solicitacoes, IReadOnlyList<AvisoDto> Avisos, CheckoutDto Checkout,
        bool PodeGerenciar, int ToleranciaDias, string Estado, AcoesAssinaturaDto Acoes, string MesVigente, DividaDto? Divida, CartaoAssinaturaDto Cartao,
        BloqueioDto? Bloqueio);

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

    /// <summary>POST assinaturas/cartao: liga ou troca o cartão automático.</summary>
    public sealed class CartaoAutomaticoRequest
    {
        public CartaoCheckoutRequest? Cartao { get; set; }
        public bool AutorizoRenovacao { get; set; }
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
        /// <summary>pix | cartao | link (link hospedado da InfinitePay, Pix ou cartao)</summary>
        public string? Metodo { get; set; }
        public CartaoCheckoutRequest? Cartao { get; set; }
        /// <summary>Cartao com renovacao automatica (preapproval do MP). Exige AutorizoRenovacao = true.</summary>
        public bool RenovacaoAutomatica { get; set; }
        public bool AutorizoRenovacao { get; set; }
    }

    public sealed record PixCheckoutDto(Guid IntentId, string Status, string? CopiaECola, string? QrCodeBase64, string? TicketUrl, DateTime? ExpiraEm, bool Reaproveitado);

    public sealed record CartaoCheckoutDto(Guid? IntentId, string Status, string? StatusPagamento, string? StatusDetail, bool Aprovado);

    public sealed record RecorrenteCheckoutDto(string? PreapprovalStatus, DateTimeOffset? PrimeiraCobrancaEm, string? CartaoFinal);

    /// <summary>Link hospedado da InfinitePay (Pix ou cartao na pagina deles). Pago so depois que o servidor confirma; voltar do link nao prova nada.</summary>
    public sealed record LinkCheckoutDto(Guid IntentId, string Status, string Url, string[] MetodosEfetivos, DateTime? ExpiraEm, bool Reaproveitado);

    public sealed record CheckoutCobrancaDto(Guid CobrancaId, string Metodo, string StatusCobranca, decimal Valor, PixCheckoutDto? Pix,
        CartaoCheckoutDto? Cartao, RecorrenteCheckoutDto? Recorrente, LinkCheckoutDto? Link = null);

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

    // ---------- Gestão (motivo sempre opcional) ----------

    public sealed class BaixaManualRequest
    {
        public string? Motivo { get; set; }
    }

    public sealed class GestaoMotivoRequest
    {
        public string? Motivo { get; set; }
    }

    public sealed class GestaoCriarAssinaturaRequest
    {
        public string? Plano { get; set; }
        public string? Periodicidade { get; set; }
        public bool Cortesia { get; set; }
        public string? Motivo { get; set; }
    }

    public sealed class GestaoAlterarAssinaturaRequest
    {
        public string? Plano { get; set; }
        public string? Periodicidade { get; set; }
        public bool Cortesia { get; set; }
        /// <summary>agora | proximo_ciclo</summary>
        public string? Quando { get; set; }
        public bool Confirmar { get; set; }
        public string? Motivo { get; set; }
    }

    public sealed class GestaoCancelarAssinaturaRequest
    {
        /// <summary>agora | fim_do_periodo</summary>
        public string? Quando { get; set; }
        public string? Motivo { get; set; }
    }

    public sealed class GestaoExtraRequest
    {
        public long? ProdutoAvulsoId { get; set; }
        public int? Motoboys { get; set; }
        public int Dias { get; set; }
        public bool Cortesia { get; set; }
        public string? Motivo { get; set; }
    }

    public sealed class GestaoVencimentoRequest
    {
        /// <summary>AAAA-MM-DD</summary>
        public string? Vencimento { get; set; }
        public string? Motivo { get; set; }
    }

    public sealed record GestaoExtraCriadoDto(CompraDto? Compra, CobrancaDto? Cobranca, long? PacoteId);

    public sealed record PlanoDaListaDto(string Chave, string Nome);

    public sealed record DividaResumoDto(decimal Valor, DateTimeOffset Desde);

    public sealed record TrocaDaListaDto(string Nome, DateTimeOffset? Em);

    public sealed record GestaoAssinaturaLinhaDto(Guid EstabelecimentoId, string Loja, string Estado, PlanoDaListaDto? Plano, string? Periodicidade, decimal? Preco,
        DateTimeOffset? ProximoVencimento, string MesVigente, DividaResumoDto? Divida, bool Bloqueada, TrocaDaListaDto? TrocaMarcada, DateTimeOffset? CanceladaAte);

    public sealed record ExcedenteDto(Guid CobrancaId, Guid EstabelecimentoId, string Loja, string Descricao, decimal Valor, DateTimeOffset Quando);
}
