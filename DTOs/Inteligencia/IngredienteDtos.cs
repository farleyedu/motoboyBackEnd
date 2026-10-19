using System;
using System.Collections.Generic;

namespace APIBack.DTOs.Inteligencia
{
    /// <summary>Preço informado pelo dono: "compro [quantidade] [unidade] por [valor]".</summary>
    public sealed class CustoRequest
    {
        public decimal QuantidadeCompra { get; set; }
        /// <summary>g, kg, ml, l ou un; precisa combinar com a unidade base do ingrediente.</summary>
        public string? UnidadeCompra { get; set; }
        public decimal ValorCompra { get; set; }
        /// <summary>Valor zero só vale como "é gratuito" e exige o motivo.</summary>
        public bool Gratuito { get; set; }
        public string? Motivo { get; set; }
        public string? ChaveIdempotencia { get; set; }
    }

    public sealed class IngredienteRequest
    {
        public string? Nome { get; set; }
        /// <summary>g, ml ou un. Não pode mudar depois de criado.</summary>
        public string? Unidade { get; set; }
        public string? Categoria { get; set; }
        public string? Observacao { get; set; }
        public string? FotoUrl { get; set; }
        public string? FotoCredito { get; set; }
        /// <summary>Versão lida pela tela; obrigatória na edição para não sobrescrever em silêncio.</summary>
        public int? Versao { get; set; }
        public string? ChaveIdempotencia { get; set; }
        /// <summary>Opcional: o ingrediente pode nascer sem custo ("Sem custo").</summary>
        public CustoRequest? Custo { get; set; }
    }

    public sealed class IngredienteFiltro
    {
        public string? Q { get; set; }
        /// <summary>todos (ativos), sem_custo, preco_alterado ou inativos.</summary>
        public string? Filtro { get; set; }
        public int Page { get; set; } = 1;
        public int PageSize { get; set; } = 30;
    }

    public sealed class IngredienteCustoDto
    {
        public Guid Id { get; set; }
        public decimal QuantidadeCompra { get; set; }
        public string UnidadeCompra { get; set; } = string.Empty;
        public decimal ValorCompra { get; set; }
        /// <summary>Custo por g, ml ou unidade.</summary>
        public decimal CustoBase { get; set; }
        public bool Gratuito { get; set; }
        public string Origem { get; set; } = "informado";
        public string? Motivo { get; set; }
        public Guid? DocumentoId { get; set; }
        public DateTimeOffset VigenciaInicio { get; set; }
        public string? CriadoPorNome { get; set; }
    }

    public sealed class VariacaoDto
    {
        public decimal Anterior { get; set; }
        public decimal Atual { get; set; }
        public decimal Diferenca { get; set; }
        /// <summary>Nulo quando o preço anterior era zero (só a diferença nominal faz sentido).</summary>
        public decimal? Percentual { get; set; }
        public DateTimeOffset AnteriorDesde { get; set; }
    }

    public sealed class IngredienteDto
    {
        public Guid Id { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string Unidade { get; set; } = "g";
        public string? Categoria { get; set; }
        public string? Observacao { get; set; }
        public string? FotoUrl { get; set; }
        public string? FotoCredito { get; set; }
        public bool Ativo { get; set; }
        public int Versao { get; set; }
        public DateTimeOffset CriadoEm { get; set; }
        public DateTimeOffset AtualizadoEm { get; set; }
        /// <summary>Nulo = custo desconhecido (a tela mostra "Sem custo", nunca zero).</summary>
        public IngredienteCustoDto? CustoAtual { get; set; }
        /// <summary>Só existe quando há dois custos diferentes.</summary>
        public VariacaoDto? Variacao { get; set; }
        /// <summary>O custo mudou há pouco tempo (filtro "Preço alterado").</summary>
        public bool PrecoAlterado { get; set; }
    }

    public sealed class IngredienteContagensDto
    {
        public int Todos { get; set; }
        public int SemCusto { get; set; }
        public int PrecoAlterado { get; set; }
        public int Inativos { get; set; }
    }

    public sealed class IngredienteListaDto
    {
        public List<IngredienteDto> Itens { get; set; } = new();
        public int Total { get; set; }
        public int Page { get; set; }
        public int PageSize { get; set; }
        public IngredienteContagensDto Contagens { get; set; } = new();
    }

    public sealed class IngredienteSalvoDto
    {
        public IngredienteDto Ingrediente { get; set; } = new();
        /// <summary>Nomes parecidos já cadastrados. Só avisa: ingredientes diferentes podem ter o mesmo nome comercial.</summary>
        public List<string> Avisos { get; set; } = new();
    }

    public sealed class IngredienteUsosDto
    {
        /// <summary>Falso até as receitas existirem; a tela mostra isso em vez de uma lista vazia enganosa.</summary>
        public bool ReceitasDisponiveis { get; set; }
        public List<string> Itens { get; set; } = new();
    }

    public sealed class IngredienteCustosDto
    {
        public List<IngredienteCustoDto> Itens { get; set; } = new();
        public int Total { get; set; }
        public int Page { get; set; }
        public int PageSize { get; set; }
    }
}
