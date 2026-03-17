using System;
using System.Text.Json.Serialization;

namespace APIBack.Model.Auth
{
    public class EmpresaInfo
    {
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("nome")]
        public string Nome { get; set; } = string.Empty;

        [JsonPropertyName("tipoAcesso")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? TipoAcesso { get; set; }
    }
}
