using System;

namespace APIBack.Automation.Models
{
    public class EstabelecimentoPublicoResumo
    {
        public Guid Id { get; set; }
        public string NomeFantasia { get; set; } = string.Empty;
        public string Slug { get; set; } = string.Empty;
    }
}
