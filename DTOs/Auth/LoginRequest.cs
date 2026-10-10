using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace APIBack.DTOs.Auth
{
    public class LoginRequest
    {
        [Required]
        [EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Required]
        [MinLength(6)]
        [JsonPropertyName("senha")]
        public string Senha { get; set; } = string.Empty;

        // Apenas o app do motoboy participa da sessão única; o painel mantém seu contrato.
        public string? ClientApp { get; set; }
        public string? ClientInstanceId { get; set; }
        public bool ConfirmSessionReplacement { get; set; }
        public Guid? ExpectedSessionId { get; set; }

        // Compatibilidade: aceita "password" sem quebrar clientes atuais com "senha".
        [JsonPropertyName("password")]
        public string? Password
        {
            get => Senha;
            set
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    Senha = value;
                }
            }
        }
    }
}
