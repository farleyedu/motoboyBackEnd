using System;

namespace APIBack.Automation.Models
{
    public class GarageLead
    {
        public Guid Id { get; set; }
        public Guid IdEstabelecimento { get; set; }
        public Guid IdConversa { get; set; }
        public Guid IdCliente { get; set; }
        public string TelefoneE164 { get; set; } = string.Empty;
        public string? NomeCliente { get; set; }
        public string? Objetivo { get; set; }
        public string? ModeloInteresse { get; set; }
        public string? FaixaInvestimento { get; set; }
        public string? FormaPagamento { get; set; }
        public string? ValorEntradaTexto { get; set; }
        public string? Urgencia { get; set; }
        public string Status { get; set; } = "em_andamento";
        public bool ViaNumeroCentral { get; set; }
        public DateTime? DataConclusao { get; set; }
        public DateTime DataCriacao { get; set; }
        public DateTime DataAtualizacao { get; set; }
    }
}
