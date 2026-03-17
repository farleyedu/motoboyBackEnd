using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace APIBack.DTOs.Auth
{
    public class MeResponse
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
        public MeEmpresaAtualResponse? EmpresaAtual { get; set; }

        [JsonPropertyName("estabelecimentoAtual")]
        public MeEstabelecimentoAtualResponse? EstabelecimentoAtual { get; set; }

        [JsonPropertyName("permissoes")]
        public Dictionary<string, List<string>> Permissoes { get; set; } = new();
    }

    public class MeEstabelecimentoAtualResponse
    {
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("nome")]
        public string Nome { get; set; } = string.Empty;

        [JsonPropertyName("tipo")]
        public string Tipo { get; set; } = string.Empty;

        [JsonPropertyName("tipoAcesso")]
        public string? TipoAcesso { get; set; }
    }

    public class MeEmpresaAtualResponse
    {
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("nome")]
        public string Nome { get; set; } = string.Empty;

        [JsonPropertyName("tipoAcesso")]
        public string? TipoAcesso { get; set; }
    }
}
