using System;
using System.Collections.Generic;

namespace APIBack.Automation.Models
{
    public class ServicoCatalogItem
    {
        public Guid Id { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string? Descricao { get; set; }
        public string Tipo { get; set; } = string.Empty;
        public int DuracaoMinutos { get; set; }
        public long? ValorCentavos { get; set; }
        public long? ValorMinimoCentavos { get; set; }
        public long? ValorMaximoCentavos { get; set; }
        public bool PermiteAgendamento { get; set; }
        public bool DiferePorVeiculo { get; set; }
        public IReadOnlyList<string> PalavrasChave { get; set; } = Array.Empty<string>();
        public IReadOnlyList<ServicoCatalogVehicleItem> Veiculos { get; set; } = Array.Empty<ServicoCatalogVehicleItem>();
    }

    public class ServicoCatalogVehicleItem
    {
        public Guid CarroId { get; set; }
        public string NomeExibicao { get; set; } = string.Empty;
        public bool Compativel { get; set; }
        public long? ValorCentavos { get; set; }
        public long? ValorMinimoCentavos { get; set; }
        public long? ValorMaximoCentavos { get; set; }
        public IReadOnlyList<ServicoCatalogPieceItem> MarcasPeca { get; set; } = Array.Empty<ServicoCatalogPieceItem>();
    }

    public class ServicoCatalogPieceItem
    {
        public string Id { get; set; } = string.Empty;
        public string Nome { get; set; } = string.Empty;
        public long? ValorCentavos { get; set; }
        public long? ValorMinimoCentavos { get; set; }
        public long? ValorMaximoCentavos { get; set; }
    }
}
