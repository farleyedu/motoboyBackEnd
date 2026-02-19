using System.Text.Json.Serialization;

namespace APIBack.Automation.Dtos.Estabelecimentos
{
    public class DefinirEstabelecimentoAtivoResponse
    {
        // Novo contrato padronizado com login/refresh.
        [JsonPropertyName("accessToken")]
        public string AccessToken { get; set; } = string.Empty;

        // Compatibilidade de clientes antigos que ainda leem "token".
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

        [JsonPropertyName("estabelecimentoSelecionado")]
        public EstabelecimentoSelecionadoDto EstabelecimentoSelecionado { get; set; } = new();
    }
}
