using System;
using System.ComponentModel.DataAnnotations;

namespace APIBack.Automation.Dtos.Estabelecimentos
{
    public class DefinirEstabelecimentoAtivoRequest
    {
        [Required]
        public Guid EstabelecimentoId { get; set; }
    }
}
