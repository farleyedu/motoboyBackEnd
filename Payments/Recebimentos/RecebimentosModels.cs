using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using APIBack.Payments.MercadoPago;
using APIBack.Payments.Provedores;

namespace APIBack.Payments.Recebimentos
{
    /// <summary>O que a tela pode afirmar sobre um recebimento. "Pago" so sai de confirmacao do servidor; estorno informado nunca vira devolvido sozinho.</summary>
    public static class SituacaoRecebimento
    {
        public const string Aguardando = "aguardando";
        public const string Pago = "pago";
        public const string NaoVerificado = "nao_verificado";
        public const string EstornoInformado = "estorno_informado";
        public const string Estornado = "estornado";
        public const string Contestado = "contestado";
        public const string Encerrado = "encerrado";
    }

    public static class StatusEstornoInformado
    {
        public const string Pendente = "pendente_confirmacao";
        public const string Confirmado = "confirmado";
        public const string Descartado = "descartado";
    }

    /// <summary>Regras puras da leitura de recebimentos e do estorno informado.</summary>
    public static class RecebimentosRegras
    {
        public const int MaxMotivo = 300;
        public const int MaxReferencia = 120;
        private static readonly Regex ErroConhecido = new("^[A-Za-z0-9_]{1,60}$", RegexOptions.Compiled);

        public static string Situacao(string provedor, string statusIntent, bool confirmadoNoServidor, bool estornoInformadoPendente, int pendenciasAbertas, int avisosSemVerificacao)
        {
            var paga = StatusIntent.FamiliaPaga(statusIntent);
            if (paga && estornoInformadoPendente) return SituacaoRecebimento.EstornoInformado;
            if (statusIntent is StatusIntent.Estornado or StatusIntent.EstornadoParcial) return SituacaoRecebimento.Estornado;
            if (statusIntent == StatusIntent.Contestado) return SituacaoRecebimento.Contestado;
            if (paga) return provedor == ProvedoresPagamento.InfinitePay && !confirmadoNoServidor ? SituacaoRecebimento.NaoVerificado : SituacaoRecebimento.Pago;
            if (pendenciasAbertas > 0 || avisosSemVerificacao > 0) return SituacaoRecebimento.NaoVerificado;
            return StatusIntent.Aberto(statusIntent) ? SituacaoRecebimento.Aguardando : SituacaoRecebimento.Encerrado;
        }

        /// <summary>So os quatro ultimos caracteres. Curto demais nao mostra nada.</summary>
        public static string? Mascarar(string? id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            var t = id.Trim();
            return t.Length <= 4 ? "••••" : "••••" + t[^4..];
        }

        /// <summary>ultimo_erro e resultado guardam codigos curtos (http_503, nao_confirmado). Qualquer outra coisa nao sai da base.</summary>
        public static string? ErroSeguro(string? erro)
        {
            if (string.IsNullOrWhiteSpace(erro)) return null;
            return ErroConhecido.IsMatch(erro.Trim()) ? erro.Trim() : "erro_interno";
        }

        public static void ValidarInforme(decimal valor, string? motivo, string? referencia, string? chave)
        {
            if (string.IsNullOrWhiteSpace(chave) || chave.Length > 160) throw PagamentoException.Invalida("Chave de idempotencia obrigatoria.", "idempotencyKey");
            if (valor <= 0 || decimal.Round(valor, 2) != valor) throw PagamentoException.Regra(PagamentoCodigos.ValorInvalido, "Valor do estorno invalido.");
            var m = motivo?.Trim() ?? "";
            if (m.Length is < 3 or > MaxMotivo) throw PagamentoException.Invalida("Diga o motivo do estorno.", "motivo");
            if (referencia != null && referencia.Trim().Length > MaxReferencia) throw PagamentoException.Invalida("Referencia longa demais.", "referencia");
        }

        public static void ValidarEvidencia(string? evidencia)
        {
            var e = evidencia?.Trim() ?? "";
            if (e.Length is < 3 or > MaxMotivo) throw PagamentoException.Invalida("Informe a evidencia conferida na conta InfinitePay.", "evidencia");
        }

        /// <summary>Quanto fica estornado e o status da intencao depois de confirmar. Nao pode passar do que foi pago.</summary>
        public static (decimal Estornado, string Status) AposConfirmar(decimal valorPago, decimal jaEstornado, decimal valor)
        {
            var total = jaEstornado + valor;
            if (total > valorPago) throw PagamentoException.Regra(PagamentoCodigos.ValorInvalido, "O estorno passa do valor pago.");
            return (total, total == valorPago ? StatusIntent.Estornado : StatusIntent.EstornadoParcial);
        }
    }

    public sealed class FiltroRecebimentos
    {
        /// <summary>Null so na Gestao Zippy. As rotas da loja sempre passam a loja da sessao.</summary>
        public Guid? Loja { get; set; }
        public string? Provedor { get; set; }
        public bool SoAtencao { get; set; }
        public DateTime? De { get; set; }
        public DateTime? Ate { get; set; }
        public int Pagina { get; set; } = 1;
    }

    public sealed class RecebimentoOnlineDto
    {
        public Guid Id { get; set; }
        public Guid EstabelecimentoId { get; set; }
        public string? LojaNome { get; set; }
        public string Provedor { get; set; } = string.Empty;
        public string Origem { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string Situacao { get; set; } = string.Empty;
        public string Descricao { get; set; } = string.Empty;
        /// <summary>Valor original cobrado: e o que vale para a loja.</summary>
        public decimal Valor { get; set; }
        public decimal ValorPago { get; set; }
        /// <summary>Total do comprovante (paid_amount): pode diferir do cobrado, com a taxa repassada ao comprador. So informativo.</summary>
        public decimal? ValorComprovante { get; set; }
        public decimal ValorEstornado { get; set; }
        public string? Metodo { get; set; }
        public string? IdExternoMascarado { get; set; }
        public string? ContaRecebedora { get; set; }
        public int? PedidoId { get; set; }
        /// <summary>A venda do pedido ja esta no livro (uma por pedido, chave venda:pedido:{id}); o Caixa le dali.</summary>
        public bool NoCaixa { get; set; }
        public int PendenciasAbertas { get; set; }
        public int AvisosSemVerificacao { get; set; }
        public decimal? EstornoInformadoValor { get; set; }
        public DateTime? PagoEm { get; set; }
        public DateTime CriadoEm { get; set; }
    }

    public sealed class SomaProvedorDto
    {
        public string Provedor { get; set; } = string.Empty;
        public int Quantidade { get; set; }
        public decimal Pago { get; set; }
        public decimal Estornado { get; set; }
    }

    public sealed class RecebimentosDto
    {
        public List<RecebimentoOnlineDto> Itens { get; set; } = new();
        public int Total { get; set; }
        public List<SomaProvedorDto> Somas { get; set; } = new();
    }

    public sealed class AlertasRecebimentosDto
    {
        /// <summary>Avisos da InfinitePay parados ou que nao puderam ser conferidos em /payment_check.</summary>
        public int AvisosSemVerificacao { get; set; }
        /// <summary>Cobrancas InfinitePay abertas ha mais de 30 min, sem confirmacao.</summary>
        public int CobrancasSemConfirmacao { get; set; }
        public int PendenciasAbertas { get; set; }
        public int EstornosInformados { get; set; }
        public int Total => AvisosSemVerificacao + CobrancasSemConfirmacao + PendenciasAbertas + EstornosInformados;
    }

    public sealed class EventoDiagnosticoDto
    {
        public Guid Id { get; set; }
        public string? Tipo { get; set; }
        public string Status { get; set; } = string.Empty;
        public string? Resultado { get; set; }
        public string? Erro { get; set; }
        public string? TransactionNsuMascarado { get; set; }
        public int Tentativas { get; set; }
        public DateTime RecebidoEm { get; set; }
        public DateTime? ProcessadoEm { get; set; }
    }

    public sealed class PendenciaDto
    {
        public Guid Id { get; set; }
        public Guid EstabelecimentoId { get; set; }
        public string? LojaNome { get; set; }
        public Guid IntentId { get; set; }
        public string? OrderNsu { get; set; }
        public string Tipo { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public DateTime CriadoEm { get; set; }
        public DateTime? ResolvidaEm { get; set; }
    }

    public sealed class EstornoInformadoDto
    {
        public Guid Id { get; set; }
        public Guid EstabelecimentoId { get; set; }
        public string? LojaNome { get; set; }
        public Guid IntentId { get; set; }
        public string? OrderNsu { get; set; }
        public decimal Valor { get; set; }
        public string Motivo { get; set; } = string.Empty;
        public string? ReferenciaExterna { get; set; }
        public string Status { get; set; } = string.Empty;
        public string? Evidencia { get; set; }
        public int InformadoPor { get; set; }
        public DateTime CriadoEm { get; set; }
        public DateTime? ResolvidoEm { get; set; }
    }

    public sealed class HistoricoRecebimentoDto
    {
        public string Acao { get; set; } = string.Empty;
        public string AtorTipo { get; set; } = string.Empty;
        public DateTime CriadoEm { get; set; }
    }

    public sealed class DiagnosticoRecebimentoDto
    {
        public RecebimentoOnlineDto Recebimento { get; set; } = new();
        public string? OrderNsu { get; set; }
        public string? Slug { get; set; }
        public string? TransactionNsuMascarado { get; set; }
        public DateTime? ConfirmadoEm { get; set; }
        public List<EventoDiagnosticoDto> Eventos { get; set; } = new();
        public List<PendenciaDto> Pendencias { get; set; } = new();
        public List<EstornoInformadoDto> Estornos { get; set; } = new();
        public List<HistoricoRecebimentoDto> Historico { get; set; } = new();
    }

    public sealed class InformarEstornoRequest
    {
        public decimal Valor { get; set; }
        public string? Motivo { get; set; }
        /// <summary>Numero do comprovante no app da InfinitePay, se a loja tiver.</summary>
        public string? Referencia { get; set; }
        public string? IdempotencyKey { get; set; }
    }

    public sealed class ConfirmarEstornoRequest
    {
        public string? Evidencia { get; set; }
    }

    public sealed class ResolverPendenciaRequest
    {
        public string? Observacao { get; set; }
    }
}
