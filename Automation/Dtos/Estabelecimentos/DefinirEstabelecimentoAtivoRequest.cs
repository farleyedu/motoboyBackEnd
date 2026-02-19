using System;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace APIBack.Automation.Dtos.Estabelecimentos
{
    public class DefinirEstabelecimentoAtivoRequest
    {
        [Required]
        [JsonPropertyName("estabelecimentoId")]
        public Guid EstabelecimentoId { get; set; }

        // Compatibilidade legada: aceita "idEstabelecimento".
        [JsonPropertyName("idEstabelecimento")]
        public Guid IdEstabelecimento
        {
            get => EstabelecimentoId;
            set
            {
                if (value != Guid.Empty)
                {
                    EstabelecimentoId = value;
                }
            }
        }
    }
}
