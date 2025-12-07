using System;
using System.ComponentModel.DataAnnotations;

namespace APIBack.DTOs.Auth
{
    public class SelecionarEstabelecimentoRequest
    {
        [Required]
        public Guid EstabelecimentoId { get; set; }
    }
}

