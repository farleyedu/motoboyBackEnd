using System;

namespace APIBack.Payments.MercadoPago
{
    public static class OrigensPagamento
    {
        public const string Cardapio = "cardapio";
        public const string Chat = "chat";
        public const string Assinatura = "assinatura";
        public const string CompraExtra = "compra_extra";
    }

    public static class MetodosPagamento
    {
        public const string Pix = "pix";
        public const string Cartao = "cartao";
    }

    /// <summary>Estados de pagamento_intent (ver maquina em CONTRATOS.md).</summary>
    public static class StatusIntent
    {
        public const string Criado = "criado";
        public const string AguardandoPagamento = "aguardando_pagamento";
        public const string Processando = "processando";
        public const string Pago = "pago";
        public const string Recusado = "recusado";
        public const string Expirado = "expirado";
        public const string Cancelado = "cancelado";
        public const string EstornadoParcial = "estornado_parcial";
        public const string Estornado = "estornado";
        public const string Contestado = "contestado";

        public static bool Aberto(string s) => s is Criado or AguardandoPagamento or Processando;
        /// <summary>Dinheiro entrou em algum momento: nunca volta para aberto/recusado.</summary>
        public static bool FamiliaPaga(string s) => s is Pago or EstornadoParcial or Estornado or Contestado;
    }

    /// <summary>Estados de pagamento (o que o MP diz, traduzido).</summary>
    public static class StatusPagamento
    {
        public const string Pendente = "pendente";
        public const string EmProcesso = "em_processo";
        public const string Autorizado = "autorizado";
        public const string Aprovado = "aprovado";
        public const string Recusado = "recusado";
        public const string Cancelado = "cancelado";
        public const string Expirado = "expirado";
        public const string EstornadoParcial = "estornado_parcial";
        public const string Estornado = "estornado";
        public const string EmMediacao = "em_mediacao";
        public const string Contestado = "contestado";

        public static bool Aberto(string s) => s is Pendente or EmProcesso or Autorizado;
    }

    /// <summary>Estados de cobranca_chat.</summary>
    public static class StatusCobranca
    {
        public const string Criando = "criando";
        public const string Aguardando = "aguardando";
        public const string Enviado = "enviado";
        public const string Pago = "pago";
        public const string Expirado = "expirado";
        public const string Cancelado = "cancelado";
        public const string ReembolsoSolicitado = "reembolso_solicitado";
        public const string Reembolsado = "reembolsado";
        public const string Recusado = "recusado";

        public static bool Aberta(string s) => s is Criando or Aguardando or Enviado;
    }

    public static class StatusEstorno
    {
        public const string Solicitado = "solicitado";
        public const string Processando = "processando";
        public const string Concluido = "concluido";
        public const string Recusado = "recusado";
        public const string Falhou = "falhou";
    }

    public sealed class PagamentoIntent
    {
        public Guid Id { get; set; }
        public Guid IdEstabelecimento { get; set; }
        public string Ambiente { get; set; } = Ambientes.Sandbox;
        public string Origem { get; set; } = string.Empty;
        public string ContaTipo { get; set; } = string.Empty;
        public Guid? IdContaLoja { get; set; }
        public Guid? IdContaZippy { get; set; }
        public int? PedidoId { get; set; }
        public Guid? PrePedidoId { get; set; }
        public Guid? ConversaId { get; set; }
        public Guid? ClienteId { get; set; }
        public Guid? OrigemRefId { get; set; }
        public decimal Valor { get; set; }
        public string Moeda { get; set; } = "BRL";
        public string Descricao { get; set; } = string.Empty;
        public string Status { get; set; } = StatusIntent.Criado;
        public int Versao { get; set; }
        public string ExternalReference { get; set; } = string.Empty;
        public string IdempotencyKey { get; set; } = string.Empty;
        public decimal ValorPago { get; set; }
        public decimal ValorEstornado { get; set; }
        public string? TokenPublicoHash { get; set; }
        public DateTime? ExpiraEm { get; set; }
        public DateTime? PagoEm { get; set; }
        public int? CriadoPor { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    public sealed class PagamentoRegistro
    {
        public Guid Id { get; set; }
        public Guid IdIntent { get; set; }
        public Guid? IdTentativa { get; set; }
        public Guid IdEstabelecimento { get; set; }
        public string Ambiente { get; set; } = Ambientes.Sandbox;
        public long MpPaymentId { get; set; }
        public long? MpCollectorId { get; set; }
        public string Metodo { get; set; } = MetodosPagamento.Pix;
        public string? PaymentMethodId { get; set; }
        public string? PaymentTypeId { get; set; }
        public string Status { get; set; } = StatusPagamento.Pendente;
        public string MpStatus { get; set; } = string.Empty;
        public string? MpStatusDetail { get; set; }
        public decimal Valor { get; set; }
        public decimal ValorEstornado { get; set; }
        public string Moeda { get; set; } = "BRL";
        public string? PixCopiaCola { get; set; }
        public string? PixQrCodeBase64 { get; set; }
        public string? PixTicketUrl { get; set; }
        public string? CartaoBandeira { get; set; }
        public string? CartaoFinal { get; set; }
        public int? Parcelas { get; set; }
        public bool LiveMode { get; set; }
        public DateTime? ExpiraEm { get; set; }
        public DateTime? AprovadoEm { get; set; }
        public DateTime? MpAtualizadoEm { get; set; }
        public int Versao { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    public sealed class CobrancaChatRegistro
    {
        public Guid Id { get; set; }
        public Guid IdEstabelecimento { get; set; }
        public Guid ConversaId { get; set; }
        public Guid? IdIntent { get; set; }
        public int? PedidoId { get; set; }
        public string Status { get; set; } = StatusCobranca.Criando;
        public int Versao { get; set; }
    }
}
