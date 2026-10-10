using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace APIBack.DTOs.Cardapio
{
    public class CardapioCategoriaDto
    {
        public Guid Id { get; set; }
        public Guid EstabelecimentoId { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string Slug { get; set; } = string.Empty;
        public string? Descricao { get; set; }
        public string? ImagemUrl { get; set; }
        public int Ordem { get; set; }
        public bool Ativo { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    public class SalvarCardapioCategoriaRequest
    {
        public string Nome { get; set; } = string.Empty;
        public string? Slug { get; set; }
        public string? Descricao { get; set; }
        public string? ImagemUrl { get; set; }
        public int Ordem { get; set; } = 1;
        public bool Ativo { get; set; } = true;
    }

    public class CardapioGrupoAdicionalItemDto
    {
        public Guid Id { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string? Descricao { get; set; }
        public decimal Preco { get; set; }
        public int Ordem { get; set; }
        public bool Ativo { get; set; }
    }

    public class CardapioGrupoAdicionalDto
    {
        public Guid Id { get; set; }
        public Guid EstabelecimentoId { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string? Descricao { get; set; }
        public int MinSelecionados { get; set; }
        public int MaxSelecionados { get; set; }
        public int Ordem { get; set; }
        public bool Ativo { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public List<CardapioGrupoAdicionalItemDto> Itens { get; set; } = new();
    }

    public class SalvarCardapioGrupoAdicionalItemRequest
    {
        public Guid? Id { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string? Descricao { get; set; }
        public decimal Preco { get; set; }
        public int Ordem { get; set; } = 1;
        public bool Ativo { get; set; } = true;
    }

    public class SalvarCardapioGrupoAdicionalRequest
    {
        public string Nome { get; set; } = string.Empty;
        public string? Descricao { get; set; }
        public int MinSelecionados { get; set; }
        public int MaxSelecionados { get; set; } = 1;
        public int Ordem { get; set; } = 1;
        public bool Ativo { get; set; } = true;
        public List<SalvarCardapioGrupoAdicionalItemRequest> Itens { get; set; } = new();
    }

    public class CardapioProdutoDto
    {
        public Guid Id { get; set; }
        public Guid EstabelecimentoId { get; set; }
        public Guid CategoriaId { get; set; }
        public string CategoriaNome { get; set; } = string.Empty;
        public string Nome { get; set; } = string.Empty;
        public string Slug { get; set; } = string.Empty;
        public string? Descricao { get; set; }
        public string? DescricaoCurta { get; set; }
        public decimal PrecoBase { get; set; }
        public string? ImagemUrl { get; set; }
        public int Ordem { get; set; }
        public bool Ativo { get; set; }
        public bool Destaque { get; set; }
        public bool Disponivel { get; set; }
        public bool PublicoWeb { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public List<Guid> GrupoIds { get; set; } = new();
    }

    public class SalvarCardapioProdutoRequest
    {
        public Guid CategoriaId { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string? Slug { get; set; }
        public string? Descricao { get; set; }
        public string? DescricaoCurta { get; set; }
        public decimal PrecoBase { get; set; }
        public string? ImagemUrl { get; set; }
        public int Ordem { get; set; } = 1;
        public bool Ativo { get; set; } = true;
        public bool Destaque { get; set; }
        public bool Disponivel { get; set; } = true;
        public bool PublicoWeb { get; set; } = true;
        public List<Guid> GrupoIds { get; set; } = new();
    }

    public class CardapioSnapshotDto
    {
        public List<CardapioCategoriaDto> Categorias { get; set; } = new();
        public List<CardapioGrupoAdicionalDto> Grupos { get; set; } = new();
        public List<CardapioProdutoDto> Produtos { get; set; } = new();
    }

    public class AtualizarDisponibilidadeCardapioRequest
    {
        public bool Disponivel { get; set; }
    }

    public class CardapioPublicoEstabelecimentoDto
    {
        public Guid Id { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string Slug { get; set; } = string.Empty;
        public string? UrlLogo { get; set; }
        /// <summary>Aceita pedido AGORA: o interruptor da loja e o horario de atendimento valem juntos.</summary>
        public bool AceitaPedidos { get; set; }
        /// <summary>pausado ou fora_horario quando nao aceita; nulo quando aceita.</summary>
        public string? MotivoFechado { get; set; }
        /// <summary>Quando abre de novo ("hoje as 18:00"), se for por horario.</summary>
        public string? AbreEm { get; set; }
        public decimal PedidoMinimo { get; set; }
        public decimal TaxaEntregaFixa { get; set; }
        public int TempoPreparoMin { get; set; }
    }

    public class CardapioPublicoGrupoItemDto
    {
        public Guid Id { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string? Descricao { get; set; }
        public decimal Preco { get; set; }
        public int Ordem { get; set; }
    }

    public class CardapioPublicoGrupoDto
    {
        public Guid Id { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string? Descricao { get; set; }
        public int MinSelecionados { get; set; }
        public int MaxSelecionados { get; set; }
        public int Ordem { get; set; }
        public List<CardapioPublicoGrupoItemDto> Itens { get; set; } = new();
    }

    public class CardapioPublicoProdutoDto
    {
        public Guid Id { get; set; }
        public Guid CategoriaId { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string Slug { get; set; } = string.Empty;
        public string? Descricao { get; set; }
        public string? DescricaoCurta { get; set; }
        public decimal PrecoBase { get; set; }
        public string? ImagemUrl { get; set; }
        public int Ordem { get; set; }
        public bool Destaque { get; set; }
        public List<CardapioPublicoGrupoDto> Grupos { get; set; } = new();
    }

    public class CardapioPublicoCategoriaDto
    {
        public Guid Id { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string Slug { get; set; } = string.Empty;
        public string? Descricao { get; set; }
        public string? ImagemUrl { get; set; }
        public int Ordem { get; set; }
        public List<CardapioPublicoProdutoDto> Produtos { get; set; } = new();
    }

    public class CardapioPublicoCatalogoDto
    {
        public CardapioPublicoEstabelecimentoDto Estabelecimento { get; set; } = new();
        public List<CardapioPublicoCategoriaDto> Categorias { get; set; } = new();
    }

    public class CardapioPedidoPublicoItemRequest
    {
        public Guid ProdutoId { get; set; }

        [Range(1, 100)]
        public int Quantidade { get; set; } = 1;

        public string? Observacao { get; set; }
        public List<Guid> AdicionalItemIds { get; set; } = new();
    }

    public class CalcularCardapioPedidoPublicoRequest
    {
        public Guid? EstabelecimentoId { get; set; }
        public string? EstabelecimentoSlug { get; set; }
        public string TipoEntrega { get; set; } = "retirada";
        public List<CardapioPedidoPublicoItemRequest> Itens { get; set; } = new();
    }

    public class CriarCardapioPedidoPublicoClienteRequest
    {
        public string Nome { get; set; } = string.Empty;
        public string Telefone { get; set; } = string.Empty;
        public string? Email { get; set; }
    }

    public class CriarCardapioPedidoPublicoEnderecoRequest
    {
        public string? Logradouro { get; set; }
        public string? Numero { get; set; }
        public string? Complemento { get; set; }
        public string? Bairro { get; set; }
        public string? Cidade { get; set; }
        public string? Uf { get; set; }
        public string? Cep { get; set; }
        public string? Referencia { get; set; }
        /// <summary>
        /// Ponto de entrega confirmado pelo cliente no mapa. Quando vem, o servidor nao geocodifica de novo: so confere se o
        /// ponto e plausivel (Brasil e perto da loja).
        /// </summary>
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        /// <summary>geocodificada (o ponto que a busca achou), pino (o cliente arrastou) ou gps (localizacao do aparelho).</summary>
        public string? OrigemPonto { get; set; }
    }

    public class LocalizarCardapioEnderecoRequest
    {
        public Guid? EstabelecimentoId { get; set; }
        public string? EstabelecimentoSlug { get; set; }
        public string? Logradouro { get; set; }
        public string? Numero { get; set; }
        public string? Bairro { get; set; }
        public string? Cidade { get; set; }
        public string? Uf { get; set; }
        public string? Cep { get; set; }
    }

    /// <summary>Onde o endereco foi achado no mapa. Sem Encontrado, o centro (a loja) serve para o cliente colocar o pino.</summary>
    public class CardapioLocalizacaoDto
    {
        public bool Encontrado { get; set; }
        /// <summary>Exata = o servico chegou ao numero informado. Falso = o cliente precisa confirmar ou ajustar o pino.</summary>
        public bool Exata { get; set; }
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        public double? CentroLatitude { get; set; }
        public double? CentroLongitude { get; set; }
    }

    public class CardapioEnderecoDoPontoRequest
    {
        public Guid? EstabelecimentoId { get; set; }
        public string? EstabelecimentoSlug { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
    }

    public class CardapioEnderecoDoPontoDto
    {
        public string? Logradouro { get; set; }
        public string? Numero { get; set; }
        public string? Bairro { get; set; }
        public string? Cidade { get; set; }
        public string? Uf { get; set; }
        public string? Cep { get; set; }
    }

    /// <summary>
    /// Resultado de "esse telefone falou com a loja ha pouco" (Fase 3c): prova minima para preencher
    /// nome/endereco no checkout sem esperar a confirmacao por WhatsApp do pedido.
    /// </summary>
    public class CardapioClienteIdentificadoDto
    {
        /// <summary>Falso = nao preenche nada (sem cadastro, ou cadastro existe mas o numero nao falou com a loja ha pouco).</summary>
        public bool Identificado { get; set; }
        public string? Nome { get; set; }
        public string? Logradouro { get; set; }
        public string? Numero { get; set; }
        public string? Complemento { get; set; }
        public string? Bairro { get; set; }
        public string? Cidade { get; set; }
        public string? Uf { get; set; }
        public string? Cep { get; set; }
    }

    public class CriarCardapioPedidoPublicoRequest : CalcularCardapioPedidoPublicoRequest
    {
        public string? SessaoCliente { get; set; }
        public bool EnderecoPrincipal { get; set; }
        public string? ApelidoEndereco { get; set; }
        public CriarCardapioPedidoPublicoClienteRequest Cliente { get; set; } = new();
        public CriarCardapioPedidoPublicoEnderecoRequest? EnderecoEntrega { get; set; }
        public string? FormaPagamento { get; set; }
        public string? Observacoes { get; set; }
    }

    public class CardapioCotacaoAdicionalDto
    {
        public Guid Id { get; set; }
        public string Nome { get; set; } = string.Empty;
        public decimal Preco { get; set; }
    }

    public class CardapioCotacaoItemDto
    {
        public Guid ProdutoId { get; set; }
        public string ProdutoNome { get; set; } = string.Empty;
        public int Quantidade { get; set; }
        public decimal PrecoUnitario { get; set; }
        public decimal TotalProduto { get; set; }
        public decimal TotalAdicionais { get; set; }
        public decimal TotalItem { get; set; }
        public string? Observacao { get; set; }
        public List<CardapioCotacaoAdicionalDto> AdicionaisSelecionados { get; set; } = new();
    }

    public class CardapioCotacaoDto
    {
        public Guid EstabelecimentoId { get; set; }
        public string EstabelecimentoNome { get; set; } = string.Empty;
        public string TipoEntrega { get; set; } = "retirada";
        public bool AceitaPedidos { get; set; }
        public string? MotivoFechado { get; set; }
        public string? AbreEm { get; set; }
        public decimal PedidoMinimo { get; set; }
        public bool PedidoMinimoAtingido { get; set; }
        public decimal SubtotalProdutos { get; set; }
        public decimal SubtotalAdicionais { get; set; }
        public decimal TaxaEntrega { get; set; }
        public decimal Total { get; set; }
        public int TempoPreparoMin { get; set; }
        public List<CardapioCotacaoItemDto> Itens { get; set; } = new();
    }

    public class CardapioPedidoPublicoCriadoDto
    {
        public Guid Id { get; set; }
        public string Codigo { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string StatusPagamento { get; set; } = string.Empty;
        public string FormaPagamento { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public CardapioCotacaoDto Resumo { get; set; } = new();
        /// <summary>Como o cliente confirma o pedido pelo WhatsApp (mensagem enviada ou codigo).</summary>
        public CardapioConfirmacaoDto Confirmacao { get; set; } = new();
    }
}
