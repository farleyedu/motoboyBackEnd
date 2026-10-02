using System;
using System.Collections.Generic;

namespace APIBack.DTOs.Cardapio
{
    /// <summary>Como o cliente confirma o pedido do cardapio web depois de finalizar.</summary>
    public class CardapioConfirmacaoDto
    {
        /// <summary>
        /// "mensagem_enviada": o WhatsApp do cliente recebeu a nossa mensagem e o pedido ja foi ao restaurante.
        /// "codigo": o cliente precisa mandar o codigo pelo WhatsApp da loja (<see cref="WhatsappUrl"/>).
        /// </summary>
        public string Modo { get; set; } = "codigo";
        public string? Codigo { get; set; }
        public DateTimeOffset? ExpiraEm { get; set; }
        public string? WhatsappUrl { get; set; }
    }

    /// <summary>Acompanhamento publico do pedido (a tela do cardapio consulta por aqui; o id e o segredo).</summary>
    public class CardapioPedidoPublicoStatusDto
    {
        public Guid Id { get; set; }
        public string Codigo { get; set; } = string.Empty;
        /// <summary>aguardando_codigo, aguardando_aceite, aceito, recusado ou expirado.</summary>
        public string Status { get; set; } = string.Empty;
        /// <summary>Preenchido so enquanto o codigo ainda vale.</summary>
        public CardapioConfirmacaoDto? Confirmacao { get; set; }
        public string? MotivoRecusa { get; set; }
        public int? NumeroPedido { get; set; }
        public string TipoEntrega { get; set; } = "retirada";
    }

    /// <summary>Endereco guardado no pre-pedido (com a coordenada achada no servidor).</summary>
    public class CardapioEnderecoArmazenado
    {
        public string? Logradouro { get; set; }
        public string? Numero { get; set; }
        public string? Complemento { get; set; }
        public string? Bairro { get; set; }
        public string? Cidade { get; set; }
        public string? Uf { get; set; }
        public string? Cep { get; set; }
        public string? Referencia { get; set; }
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
    }

    // ---- Fila do atendente ---------------------------------------------------------------------------

    public class CardapioPedidoAguardandoItemDto
    {
        public int Quantidade { get; set; }
        public string Nome { get; set; } = string.Empty;
        public List<string> Adicionais { get; set; } = new();
        public string? Observacao { get; set; }
    }

    /// <summary>Pedido confirmado pelo cliente que espera o restaurante aceitar ou recusar.</summary>
    public class CardapioPedidoAguardandoDto
    {
        public Guid Id { get; set; }
        public string Codigo { get; set; } = string.Empty;
        /// <summary>Quando o cliente confirmou: o painel mostra "ha X min" a partir daqui.</summary>
        public DateTimeOffset ConfirmadoEm { get; set; }
        public string NomeCliente { get; set; } = string.Empty;
        public string? Telefone { get; set; }
        /// <summary>entrega ou retirada.</summary>
        public string TipoEntrega { get; set; } = "retirada";
        public string? Endereco { get; set; }
        public List<CardapioPedidoAguardandoItemDto> Itens { get; set; } = new();
        public string? Observacoes { get; set; }
        public string? FormaPagamento { get; set; }
        public decimal Total { get; set; }
    }

    public class RecusarCardapioPedidoRequest
    {
        public string? Motivo { get; set; }
    }

    public class AceitarCardapioPedidoResultDto
    {
        public Guid Id { get; set; }
        public string Status { get; set; } = "aceito";
        /// <summary>Pedido criado no delivery (entrega); nulo na retirada.</summary>
        public int? PedidoId { get; set; }
        /// <summary>O que o cliente viu ao finalizar.</summary>
        public decimal TotalCliente { get; set; }
        /// <summary>O que o delivery calculou (taxa por zona); difere de <see cref="TotalCliente"/> quando a taxa mudou.</summary>
        public decimal? TotalPedido { get; set; }
        /// <summary>False quando a mensagem de aceito nao saiu (janela fechada ou falha do WhatsApp).</summary>
        public bool ClienteAvisado { get; set; }
        public List<string> Avisos { get; set; } = new();
    }
}
