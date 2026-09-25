using System;
using System.Collections.Generic;

namespace APIBack.DTOs.Delivery
{
    public class PedidoResumoDto
    {
        public int Id { get; set; }
        /// <summary>rascunho, pendente, atribuido, em_rota, concluido ou cancelado.</summary>
        public string Status { get; set; } = "pendente";
        public string Origem { get; set; } = "atendente";
        public string? NomeCliente { get; set; }
        public string? TelefoneCliente { get; set; }
        public string? Bairro { get; set; }
        public string? EnderecoEntrega { get; set; }
        public decimal? Total { get; set; }
        public decimal? Subtotal { get; set; }
        public decimal? TaxaEntrega { get; set; }
        public string? FormaPagamento { get; set; }
        public DateTime? CriadoEm { get; set; }
        public int? MotoboyId { get; set; }
        public string? MotoboyNome { get; set; }
        public int QuantidadeItens { get; set; }
        public Guid? ConversaId { get; set; }
    }

    public class PedidoAdicionalDto
    {
        public string Nome { get; set; } = string.Empty;
        public decimal Preco { get; set; }
    }

    public class PedidoItemDto
    {
        public string Nome { get; set; } = string.Empty;
        public int Quantidade { get; set; } = 1;
        public decimal? PrecoUnitario { get; set; }
        public string? Observacao { get; set; }
        public List<PedidoAdicionalDto> Adicionais { get; set; } = new();
        public decimal? Total { get; set; }
    }

    public class PedidoDetalheDto : PedidoResumoDto
    {
        public string? Rua { get; set; }
        public string? Numero { get; set; }
        public string? Cidade { get; set; }
        public string? Estado { get; set; }
        public string? Cep { get; set; }
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        public string? Observacoes { get; set; }
        public decimal? Troco { get; set; }
        public string? StatusPagamento { get; set; }
        public DateTime? PrevisaoEntrega { get; set; }
        public string? CodigoEntrega { get; set; }
        public List<PedidoItemDto> Itens { get; set; } = new();
    }

    public class PedidoListaDto
    {
        public List<PedidoResumoDto> Itens { get; set; } = new();
        public int Total { get; set; }
        public int Page { get; set; }
        public int PageSize { get; set; }
    }

    /// <summary>Filtros brutos da lista (como chegam na query string).</summary>
    public class PedidoFiltroRequest
    {
        /// <summary>Lista separada por virgula: rascunho,pendente,atribuido,em_rota,concluido,cancelado.</summary>
        public string? Status { get; set; }
        /// <summary>Lista separada por virgula: atendente,cardapio_web,ifood,ia_whatsapp,simulador.</summary>
        public string? Origem { get; set; }
        /// <summary>AAAA-MM-DD (inclusive).</summary>
        public string? De { get; set; }
        /// <summary>AAAA-MM-DD (inclusive).</summary>
        public string? Ate { get; set; }
        /// <summary>Numero do pedido, nome, telefone, endereco ou bairro.</summary>
        public string? Busca { get; set; }
        /// <summary>Pedidos da conversa: ligados a ela ou feitos pelo telefone do cliente dela (Fase 5).</summary>
        public string? ConversaId { get; set; }
        public int Page { get; set; } = 1;
        public int PageSize { get; set; } = 30;
    }
}
