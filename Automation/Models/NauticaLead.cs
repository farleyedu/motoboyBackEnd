using System;

namespace APIBack.Automation.Models
{
    public class NauticaLead
    {
        public Guid Id { get; set; }
        public Guid IdEstabelecimento { get; set; }
        public Guid IdConversa { get; set; }
        public Guid IdCliente { get; set; }
        public string TelefoneE164 { get; set; } = string.Empty;
        public string? NomeCliente { get; set; }
        public bool? TemLojaFisica { get; set; }
        public string? Cnpj { get; set; }
        public string? Segmento { get; set; }
        public bool? ConsegueMinimo { get; set; }
        public string? NomeEmpresa { get; set; }
        public string? CidadeEstado { get; set; }
        public string? HistoricoNautica { get; set; }
        public string? DesafioLoja { get; set; }
        public string? PublicoAlvo { get; set; }
        public string Status { get; set; } = "incompleto";
        public bool ViaNumeroCentral { get; set; }
        public DateTime? DataConclusao { get; set; }
        public DateTime DataCriacao { get; set; }
        public DateTime DataAtualizacao { get; set; }
    }
}
