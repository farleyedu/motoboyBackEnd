using System;
using System.Collections.Generic;

namespace APIBack.Model.Cardapio
{
    public class CardapioCategoria
    {
        public Guid Id { get; set; }
        public Guid IdEstabelecimento { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string Slug { get; set; } = string.Empty;
        public string? Emoji { get; set; }
        public string? Descricao { get; set; }
        public string? ImagemUrl { get; set; }
        public int Ordem { get; set; }
        public bool Ativo { get; set; } = true;
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime? DeletedAt { get; set; }
    }

    public class CardapioGrupoAdicional
    {
        public Guid Id { get; set; }
        public Guid IdEstabelecimento { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string Tipo { get; set; } = "adicional_global";
        public string? Descricao { get; set; }
        public int MinSelecionados { get; set; }
        public int MaxSelecionados { get; set; }
        public int Ordem { get; set; }
        public bool Ativo { get; set; } = true;
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime? DeletedAt { get; set; }
        public List<CardapioGrupoAdicionalItem> Itens { get; set; } = new();
    }

    public class CardapioGrupoAdicionalItem
    {
        public Guid Id { get; set; }
        public Guid IdGrupo { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string? Descricao { get; set; }
        public decimal Preco { get; set; }
        public int Ordem { get; set; }
        public bool Ativo { get; set; } = true;
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    public class CardapioProduto
    {
        public Guid Id { get; set; }
        public Guid IdEstabelecimento { get; set; }
        public Guid CategoriaId { get; set; }
        public string CategoriaNome { get; set; } = string.Empty;
        public string Nome { get; set; } = string.Empty;
        public string Slug { get; set; } = string.Empty;
        public string? Emoji { get; set; }
        public string? Descricao { get; set; }
        public string? DescricaoCurta { get; set; }
        public decimal PrecoBase { get; set; }
        public decimal? PrecoDe { get; set; }
        public string? BadgeDesconto { get; set; }
        public bool IsClub { get; set; }
        public string? ImagemUrl { get; set; }
        public bool EcoFriendly { get; set; }
        public string? ExtrasTitulo { get; set; }
        public string? ExtrasSubtitulo { get; set; }
        public int Ordem { get; set; }
        public bool Ativo { get; set; } = true;
        public bool Destaque { get; set; }
        public bool Disponivel { get; set; } = true;
        public bool PublicoWeb { get; set; } = true;
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime? DeletedAt { get; set; }
        public List<Guid> GrupoIds { get; set; } = new();
        public List<CardapioGrupoAdicional> Grupos { get; set; } = new();
    }

    public class CardapioEstabelecimentoPublico
    {
        public Guid Id { get; set; }
        public string NomeFantasia { get; set; } = string.Empty;
        public string Slug { get; set; } = string.Empty;
        public string? UrlLogo { get; set; }
        public string? NomePublico { get; set; }
        public string? Emoji { get; set; }
        public string? BannerUrl { get; set; }
        public decimal? Rating { get; set; }
        public int? ReviewCount { get; set; }
        public string? DeliveryTimeLabel { get; set; }
        public decimal? DeliveryFeeValue { get; set; }
        public string? DeliveryFeeLabel { get; set; }
        public decimal ServiceFeeValue { get; set; }
        public bool AceitaEntrega { get; set; } = true;
        public bool AceitaRetirada { get; set; } = true;
        public bool Publicado { get; set; }
        public bool AceitaPedidos { get; set; }
        public decimal PedidoMinimo { get; set; }
        public decimal TaxaEntregaFixa { get; set; }
        public int TempoPreparoMin { get; set; }
        public string[]? ModulosAtivosRaw { get; set; }
    }

    public class CardapioPedidoPublico
    {
        public Guid Id { get; set; }
        public Guid IdEstabelecimento { get; set; }
        public string Codigo { get; set; } = string.Empty;
        public string Status { get; set; } = "pendente";
        public string TipoEntrega { get; set; } = "retirada";
        public string NomeCliente { get; set; } = string.Empty;
        public string TelefoneCliente { get; set; } = string.Empty;
        public string? EmailCliente { get; set; }
        public string? FormaPagamento { get; set; }
        public string? Observacoes { get; set; }
        public decimal SubtotalProdutos { get; set; }
        public decimal SubtotalAdicionais { get; set; }
        public decimal TaxaEntrega { get; set; }
        public decimal Total { get; set; }
        public string ItensJson { get; set; } = "[]";
        public string? EnderecoEntregaJson { get; set; }
        public string StatusPagamento { get; set; } = "pendente";
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    public class CardapioWebConfig
    {
        public Guid IdEstabelecimento { get; set; }
        public string? NomePublico { get; set; }
        public string? Emoji { get; set; }
        public string? LogoUrl { get; set; }
        public string? BannerUrl { get; set; }
        public decimal? Rating { get; set; }
        public int? ReviewCount { get; set; }
        public string? DeliveryTimeLabel { get; set; }
        public decimal DeliveryFeeValue { get; set; }
        public string? DeliveryFeeLabel { get; set; }
        public decimal ServiceFeeValue { get; set; }
        public bool AceitaEntrega { get; set; } = true;
        public bool AceitaRetirada { get; set; } = true;
        public bool Publicado { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }
}
