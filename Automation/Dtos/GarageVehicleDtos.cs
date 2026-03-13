using System;
using System.Collections.Generic;

namespace APIBack.Automation.Dtos
{
    public class GarageVehicleDto
    {
        public Guid Id { get; set; }
        public string Slug { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Brand { get; set; } = string.Empty;
        public string Model { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public int Year { get; set; }
        public int ModelYear { get; set; }
        public decimal Price { get; set; }
        public decimal? OldPrice { get; set; }
        public int Km { get; set; }
        public string Fuel { get; set; } = string.Empty;
        public string Transmission { get; set; } = string.Empty;
        public string Color { get; set; } = string.Empty;
        public string City { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public int Doors { get; set; }
        public int Seats { get; set; }
        public string Status { get; set; } = string.Empty;
        public string Condition { get; set; } = string.Empty;
        public bool Featured { get; set; }
        public string? SpotlightLabel { get; set; }
        public string StockCode { get; set; } = string.Empty;
        public string? DriveType { get; set; }
        public string Description { get; set; } = string.Empty;
        public List<string> Optionals { get; set; } = new();
        public List<string> Gallery { get; set; } = new();
        public List<GarageVehicleConditionItemDto> ConditionItems { get; set; } = new();
    }

    public class GarageVehicleConditionItemDto
    {
        public string Label { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string Note { get; set; } = string.Empty;
    }

    public class GarageVehicleListResponseDto
    {
        public IReadOnlyList<GarageVehicleDto> Items { get; set; } = Array.Empty<GarageVehicleDto>();
        public int Total { get; set; }
        public int Page { get; set; }
        public int PageSize { get; set; }
    }

    public class GarageVehicleShowcaseResponseDto
    {
        public IReadOnlyList<GarageVehicleDto> Items { get; set; } = Array.Empty<GarageVehicleDto>();
        public IReadOnlyList<GarageVehicleDto> Destaques { get; set; } = Array.Empty<GarageVehicleDto>();
    }

    public class GarageVehicleMetricsDto
    {
        public int Total { get; set; }
        public int Disponiveis { get; set; }
        public int EmDestaque { get; set; }
        public decimal TicketMedio { get; set; }
    }

    public class CreateGarageVehicleRequest
    {
        public Guid IdEstabelecimento { get; set; }
        public string? Titulo { get; set; }
        public string? Marca { get; set; }
        public string? Modelo { get; set; }
        public int? AnoFabricacao { get; set; }
        public int? AnoModelo { get; set; }
        public string? Cor { get; set; }
        public string? Placa { get; set; }
        public string? Cidade { get; set; }
        public string? Carroceria { get; set; }
        public string? Categoria { get; set; }
        public string? TipoVeiculo { get; set; }
        public string? Status { get; set; }
        public decimal? Preco { get; set; }
        public decimal? PrecoAnterior { get; set; }
        public int? Km { get; set; }
        public string? Combustivel { get; set; }
        public string? Cambio { get; set; }
        public string? Tracao { get; set; }
        public int? Portas { get; set; }
        public int? Assentos { get; set; }
        public bool Destaque { get; set; }
        public string? LabelDestaque { get; set; }
        public string? Descricao { get; set; }
        public string? CodigoEstoque { get; set; }
        public List<string> Opcionais { get; set; } = new();
        public List<GarageVehicleConditionRequestDto> Condicoes { get; set; } = new();
        public List<string> Fotos { get; set; } = new();
    }

    public class UpsertGarageVehicleRequest
    {
        public Guid IdEstabelecimento { get; set; }
        public string? Slug { get; set; }
        public string Titulo { get; set; } = string.Empty;
        public string Marca { get; set; } = string.Empty;
        public string Modelo { get; set; } = string.Empty;
        public int AnoFabricacao { get; set; }
        public int AnoModelo { get; set; }
        public string Cor { get; set; } = string.Empty;
        public string? Placa { get; set; }
        public string Cidade { get; set; } = string.Empty;
        public string Carroceria { get; set; } = string.Empty;
        public string Categoria { get; set; } = string.Empty;
        public string TipoVeiculo { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public decimal Preco { get; set; }
        public decimal? PrecoAnterior { get; set; }
        public int Km { get; set; }
        public string Combustivel { get; set; } = string.Empty;
        public string Cambio { get; set; } = string.Empty;
        public string? Tracao { get; set; }
        public int Portas { get; set; }
        public int Assentos { get; set; }
        public bool Destaque { get; set; }
        public string? LabelDestaque { get; set; }
        public string Descricao { get; set; } = string.Empty;
        public string CodigoEstoque { get; set; } = string.Empty;
        public List<string> Opcionais { get; set; } = new();
        public List<GarageVehicleConditionRequestDto> Condicoes { get; set; } = new();
        public List<string> Fotos { get; set; } = new();
    }

    public class GarageVehicleConditionRequestDto
    {
        public string Item { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string Nota { get; set; } = string.Empty;
    }

    public class UpdateGarageVehicleStatusRequest
    {
        public string Status { get; set; } = string.Empty;
    }

    public class UpdateGarageVehicleHighlightRequest
    {
        public bool Destaque { get; set; }
        public string? Label { get; set; }
    }
}
