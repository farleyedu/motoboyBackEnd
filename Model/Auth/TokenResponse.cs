using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace APIBack.Model.Auth
{
    public class TokenResponse
    {
        [JsonPropertyName("accessToken")]
        public string AccessToken { get; set; } = string.Empty;

        // Compatibilidade para clientes legados que esperam "token".
        [JsonPropertyName("token")]
        public string Token
        {
            get => AccessToken;
            set => AccessToken = value;
        }

        [JsonPropertyName("refreshToken")]
        public string RefreshToken { get; set; } = string.Empty;

        [JsonPropertyName("tokenType")]
        public string TokenType { get; set; } = "Bearer";

        [JsonPropertyName("expiresIn")]
        public int ExpiresIn { get; set; }

        [JsonPropertyName("user")]
        public UserInfo User { get; set; } = null!;
    }

    public class UserInfo
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("nome")]
        public string Nome { get; set; } = string.Empty;

        [JsonPropertyName("email")]
        public string Email { get; set; } = string.Empty;

        [JsonPropertyName("isSuperAdmin")]
        public bool IsSuperAdmin { get; set; }

        [JsonPropertyName("empresaAtual")]
        public EmpresaInfo? EmpresaAtual { get; set; }

        [JsonPropertyName("estabelecimentoAtual")]
        public EstabelecimentoInfo? EstabelecimentoAtual { get; set; }
    }

    public class EstabelecimentoInfo
    {
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("nome")]
        public string Nome { get; set; } = string.Empty;

        [JsonPropertyName("tipo")]
        public string Tipo { get; set; } = string.Empty;

        [JsonPropertyName("modulosAtivos")]
        public List<string> ModulosAtivos { get; set; } = new();

        [JsonPropertyName("tipoAcesso")]
        public string TipoAcesso { get; set; } = string.Empty;
    }
}
