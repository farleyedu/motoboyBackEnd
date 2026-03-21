using System;
using System.Collections.Generic;

namespace APIBack.Automation.Dtos
{
    public class NauticaLeadListItemDto
    {
        public Guid Id { get; set; }
        public string Telefone { get; set; } = string.Empty;
        public string? NomeEmpresa { get; set; }
        public string? Cnpj { get; set; }
        public string? Segmento { get; set; }
        public string Status { get; set; } = string.Empty;
        public DateTime Data { get; set; }
    }

    public class NauticaLeadDetailDto : NauticaLeadListItemDto
    {
        public Guid IdConversa { get; set; }
        public Guid IdCliente { get; set; }
        public bool ViaNumeroCentral { get; set; }
        public bool? TemLojaFisica { get; set; }
        public bool? ConsegueMinimo { get; set; }
        public string? CidadeEstado { get; set; }
        public string? HistoricoNautica { get; set; }
        public string? DesafioLoja { get; set; }
        public string? PublicoAlvo { get; set; }
        public DateTime? DataConclusao { get; set; }
        public DateTime DataCriacao { get; set; }
        public DateTime DataAtualizacao { get; set; }
    }

    public class NauticaLeadStatusCountDto
    {
        public string Status { get; set; } = string.Empty;
        public int Total { get; set; }
    }

    public class NauticaLeadListResponseDto
    {
        public int Pagina { get; set; }
        public int TamanhoPagina { get; set; }
        public int Total { get; set; }
        public Dictionary<string, int> TotaisPorStatus { get; set; } = new();
        public IReadOnlyList<NauticaLeadListItemDto> Itens { get; set; } = new List<NauticaLeadListItemDto>();
    }

    public class UpdateNauticaLeadStatusRequest
    {
        public string Status { get; set; } = string.Empty;
    }
}
