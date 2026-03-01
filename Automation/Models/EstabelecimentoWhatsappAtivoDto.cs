using System;

namespace APIBack.Automation.Models
{
    public class EstabelecimentoWhatsappAtivoDto
    {
        public Guid Id { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string TipoEstabelecimento { get; set; } = string.Empty;
    }
}
