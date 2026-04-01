using System;
using System.Collections.Generic;

namespace APIBack.DTOs.Cardapio
{
    public class CardapioCategoriaContractDto
    {
        public Guid Id { get; set; }
        public string Slug { get; set; } = string.Empty;
        public string Nome { get; set; } = string.Empty;
        public string? Emoji { get; set; }
        public string? Descricao { get; set; }
        public int Ordem { get; set; }
        public bool Ativo { get; set; }
    }

    public class SalvarCardapioCategoriaContractRequest
    {
        public string? Slug { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string? Emoji { get; set; }
        public string? Descricao { get; set; }
        public int Ordem { get; set; } = 1;
        public bool Ativo { get; set; } = true;
    }

    public class CardapioAdicionalDto
    {
        public Guid Id { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string? Descricao { get; set; }
        public decimal Preco { get; set; }
        public bool Ativo { get; set; }
        public int Ordem { get; set; }
    }

    public class SalvarCardapioAdicionalRequest
    {
        public string Nome { get; set; } = string.Empty;
        public string? Descricao { get; set; }
        public decimal Preco { get; set; }
        public bool Ativo { get; set; } = true;
        public int Ordem { get; set; } = 1;
    }

    public class CardapioProdutoAdicionalLinkDto
    {
        public Guid AdicionalId { get; set; }
        public decimal? PrecoOverride { get; set; }
    }

    public class CardapioExtrasConfigDto
    {
        public string? Titulo { get; set; }
        public string? Subtitulo { get; set; }
    }

    public class CardapioOptionItemDto
    {
        public Guid Id { get; set; }
        public string Label { get; set; } = string.Empty;
    }

    public class CardapioOptionGroupDto
    {
        public Guid Id { get; set; }
        public string Titulo { get; set; } = string.Empty;
        public string? Subtitulo { get; set; }
        public bool Required { get; set; }
        public string Type { get; set; } = "checkbox";
        public List<CardapioOptionItemDto> Items { get; set; } = new();
    }

    public class CardapioProdutoContractDto
    {
        public Guid Id { get; set; }
        public Guid CategoriaId { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string? Descricao { get; set; }
        public decimal Preco { get; set; }
        public decimal? PrecoDe { get; set; }
        public string? BadgeDesconto { get; set; }
        public bool IsClub { get; set; }
        public string? Emoji { get; set; }
        public string? ImageUrl { get; set; }
        public bool EcoFriendly { get; set; }
        public bool Ativo { get; set; }
        public int Ordem { get; set; }
        public bool Disponivel { get; set; }
        public bool PublicadoWeb { get; set; }
        public List<CardapioProdutoAdicionalLinkDto> Adicionais { get; set; } = new();
        public CardapioExtrasConfigDto ExtrasConfig { get; set; } = new();
        public List<CardapioOptionGroupDto> OptionGroups { get; set; } = new();
    }

    public class SalvarCardapioProdutoContractRequest
    {
        public Guid CategoriaId { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string? Descricao { get; set; }
        public decimal Preco { get; set; }
        public decimal? PrecoDe { get; set; }
        public string? BadgeDesconto { get; set; }
        public bool IsClub { get; set; }
        public string? Emoji { get; set; }
        public string? ImageUrl { get; set; }
        public bool EcoFriendly { get; set; }
        public bool Ativo { get; set; } = true;
        public int Ordem { get; set; } = 1;
        public bool Disponivel { get; set; } = true;
        public bool PublicadoWeb { get; set; } = true;
        public List<CardapioProdutoAdicionalLinkDto> Adicionais { get; set; } = new();
        public CardapioExtrasConfigDto? ExtrasConfig { get; set; }
        public List<CardapioOptionGroupDto>? OptionGroups { get; set; }
    }

    public class CardapioWebConfigDto
    {
        public Guid EstabelecimentoId { get; set; }
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
        public bool AceitaEntrega { get; set; }
        public bool AceitaRetirada { get; set; }
        public bool Publicado { get; set; }
    }

    public class SalvarCardapioWebConfigRequest
    {
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
    }

    public class AtualizarPublicacaoCardapioWebRequest
    {
        public bool Publicado { get; set; }
    }

    public class CardapioPublicoAdicionalDto
    {
        public string Id { get; set; } = string.Empty;
        public Guid AdicionalId { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string? Descricao { get; set; }
        public decimal Preco { get; set; }
    }

    public class CardapioPublicoCategoriaContractDto
    {
        public Guid Id { get; set; }
        public string Slug { get; set; } = string.Empty;
        public string Nome { get; set; } = string.Empty;
        public string? Emoji { get; set; }
        public string? Descricao { get; set; }
        public int Ordem { get; set; }
        public bool Ativo { get; set; }
    }

    public class CardapioPublicoProdutoContractDto
    {
        public Guid Id { get; set; }
        public Guid CategoriaId { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string? Descricao { get; set; }
        public decimal Preco { get; set; }
        public decimal? PrecoDe { get; set; }
        public string? BadgeDesconto { get; set; }
        public bool IsClub { get; set; }
        public string? Emoji { get; set; }
        public string? ImageUrl { get; set; }
        public bool EcoFriendly { get; set; }
        public bool Ativo { get; set; }
        public int Ordem { get; set; }
        public List<CardapioPublicoAdicionalDto> Adicionais { get; set; } = new();
        public CardapioExtrasConfigDto ExtrasConfig { get; set; } = new();
        public List<CardapioOptionGroupDto> OptionGroups { get; set; } = new();
    }

    public class CardapioPublicoSnapshotDto
    {
        public Guid EstabelecimentoId { get; set; }
        public string NomePublico { get; set; } = string.Empty;
        public string? Emoji { get; set; }
        public string? LogoUrl { get; set; }
        public string? BannerUrl { get; set; }
        public decimal? Rating { get; set; }
        public int? ReviewCount { get; set; }
        public string? DeliveryTimeLabel { get; set; }
        public decimal DeliveryFeeValue { get; set; }
        public string? DeliveryFeeLabel { get; set; }
        public decimal ServiceFeeValue { get; set; }
        public bool AceitaEntrega { get; set; }
        public bool AceitaRetirada { get; set; }
        public List<CardapioPublicoCategoriaContractDto> Categorias { get; set; } = new();
        public List<CardapioPublicoProdutoContractDto> Produtos { get; set; } = new();
    }
}
