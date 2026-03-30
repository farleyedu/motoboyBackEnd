using System;
using System.Collections.Generic;

namespace APIBack.Automation.Models
{
    public class OficinaAtendimento
    {
        public Guid Id { get; set; }
        public Guid IdEstabelecimento { get; set; }
        public Guid IdConversa { get; set; }
        public Guid IdCliente { get; set; }
        public string TelefoneE164 { get; set; } = string.Empty;
        public string? NomeCliente { get; set; }
        public string PerfilContato { get; set; } = "indefinido";
        public string IntencaoPrincipal { get; set; } = "duvida_geral";
        public string? IntencaoDetalhe { get; set; }
        public string? ResumoAtendimento { get; set; }
        public string Status { get; set; } = "em_triagem";
        public string? EtapaAtual { get; set; }
        public string? UltimaPergunta { get; set; }
        public Dictionary<string, object?> DadosExtras { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public bool ViaNumeroCentral { get; set; }
        public DateTime? DataHandover { get; set; }
        public DateTime? DataConclusao { get; set; }
        public DateTime DataCriacao { get; set; }
        public DateTime DataAtualizacao { get; set; }
    }
}
