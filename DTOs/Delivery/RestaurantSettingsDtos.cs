using System;

namespace APIBack.DTOs.Delivery
{
    /// <summary>
    /// Dados do restaurante que a operacao de delivery configura: onde ele fica, se aceita pedidos e
    /// as regras de entrega. Sao colunas da tabela do estabelecimento (as mesmas que a Gestao edita).
    /// </summary>
    public sealed class RestaurantSettingsDto
    {
        public Guid EstabelecimentoId { get; set; }
        /// <summary>So leitura: o nome e editado na Gestao.</summary>
        public string NomeFantasia { get; set; } = string.Empty;
        public string? Telefone { get; set; }

        public string? Logradouro { get; set; }
        public string? Numero { get; set; }
        public string? Complemento { get; set; }
        public string? Bairro { get; set; }
        public string? Cidade { get; set; }
        public string? Uf { get; set; }
        public string? Cep { get; set; }
        /// <summary>Posicao da loja no mapa: origem das rotas e centro do mapa do painel.</summary>
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }

        public bool AceitaPedidos { get; set; } = true;
        /// <summary>Raio de entrega em km.</summary>
        public decimal? RaioEntregaKm { get; set; }
        public decimal? PedidoMinimo { get; set; }
        public decimal? TaxaEntregaFixa { get; set; }
        public decimal? TaxaEntregaPorKm { get; set; }
        /// <summary>Tempo medio de preparo, em minutos.</summary>
        public int? TempoPreparoMin { get; set; }
    }

    /// <summary>Substitui todos os campos acima (menos id e nome): o formulario sempre manda o conjunto.</summary>
    public sealed class UpdateRestaurantSettingsRequest
    {
        public string? Telefone { get; set; }
        public string? Logradouro { get; set; }
        public string? Numero { get; set; }
        public string? Complemento { get; set; }
        public string? Bairro { get; set; }
        public string? Cidade { get; set; }
        public string? Uf { get; set; }
        public string? Cep { get; set; }
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        public bool AceitaPedidos { get; set; } = true;
        public decimal? RaioEntregaKm { get; set; }
        public decimal? PedidoMinimo { get; set; }
        public decimal? TaxaEntregaFixa { get; set; }
        public decimal? TaxaEntregaPorKm { get; set; }
        public int? TempoPreparoMin { get; set; }
    }
}
