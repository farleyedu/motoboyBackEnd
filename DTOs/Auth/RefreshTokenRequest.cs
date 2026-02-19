using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace APIBack.DTOs.Auth
{
    public class RefreshTokenRequest
    {
        [Required]
        [JsonPropertyName("refreshToken")]
        public string RefreshToken { get; set; } = string.Empty;

        // Compatibilidade legada: alguns clientes enviam "token".
        [JsonPropertyName("token")]
        public string? Token
        {
            get => RefreshToken;
            set
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    RefreshToken = value;
                }
            }
        }
    }
}
