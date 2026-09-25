using System;
using System.Collections.Generic;

namespace APIBack.DTOs.Cardapio
{
    /// <summary>Campos de atendimento de um produto (tabela cardapio_produto_atendimento).</summary>
    public class ProdutoAtendimentoDto
    {
        /// <summary>Como o cliente chama o produto ("x-bacon", "xis").</summary>
        public List<string> Apelidos { get; set; } = new();
        /// <summary>O que o atendente (ou a IA) precisa saber ("serve 2 pessoas", "sem cebola por padrao").</summary>
        public string? Instrucoes { get; set; }
        /// <summary>Alergenicos e restricoes ("contem gluten e lactose").</summary>
        public string? Restricoes { get; set; }
        /// <summary>Minutos a somar ao preparo quando o produto e pedido.</summary>
        public int? TempoExtraPreparoMin { get; set; }
    }

    public class FichaItemDto
    {
        /// <summary>Id do ITEM do grupo: e ele que o pedido envia em adicionalItemIds.</summary>
        public Guid Id { get; set; }
        public string Nome { get; set; } = string.Empty;
        public decimal Preco { get; set; }
    }

    public class FichaGrupoDto
    {
        public Guid Id { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string Tipo { get; set; } = string.Empty;
        public int Min { get; set; }
        public int Max { get; set; }
        public List<FichaItemDto> Itens { get; set; } = new();
    }

    public class FichaProdutoDto
    {
        public Guid Id { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string? Descricao { get; set; }
        public decimal Preco { get; set; }
        public decimal? PrecoDe { get; set; }
        /// <summary>False: acabou hoje. Continua na ficha, marcado, para o atendente saber.</summary>
        public bool Disponivel { get; set; }
        public List<string> Apelidos { get; set; } = new();
        public string? Instrucoes { get; set; }
        public string? Restricoes { get; set; }
        public int? TempoExtraPreparoMin { get; set; }
        public List<FichaGrupoDto> Grupos { get; set; } = new();
    }

    public class FichaCategoriaDto
    {
        public Guid Id { get; set; }
        public string Nome { get; set; } = string.Empty;
        public List<FichaProdutoDto> Produtos { get; set; } = new();
    }

    /// <summary>Regras do estabelecimento que o atendente enxerga ao montar o pedido.</summary>
    public class FichaEstabelecimentoDto
    {
        public Guid Id { get; set; }
        public string Nome { get; set; } = string.Empty;
        public bool AceitaPedidos { get; set; }
        public decimal PedidoMinimo { get; set; }
        public decimal TaxaEntregaFixa { get; set; }
        public decimal TaxaEntregaPorKm { get; set; }
        public decimal? RaioEntregaKm { get; set; }
        public int? TempoPreparoMin { get; set; }
    }

    /// <summary>
    /// Leitura unica do cardapio para quem monta pedido (atendente hoje, IA depois). Versionada:
    /// a versao e o hash do CONTEUDO, entao so muda quando o cardapio ou as regras mudam.
    /// </summary>
    public class FichaAtendimentoDto
    {
        public string Versao { get; set; } = string.Empty;
        public DateTimeOffset GeradoEm { get; set; }
        public FichaEstabelecimentoDto Estabelecimento { get; set; } = new();
        public List<FichaCategoriaDto> Categorias { get; set; } = new();
    }
}
