using System;
using System.Collections.Generic;

namespace APIBack.DTOs.Clientes
{
    /// <summary>Cliente do estabelecimento (cadastro proprio, separado da conversa do WhatsApp).</summary>
    public sealed class ClienteDto
    {
        public Guid Id { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string? Telefone { get; set; }
        public string? Email { get; set; }
        public string? Observacoes { get; set; }
        public string? Cep { get; set; }
        public string? Logradouro { get; set; }
        public string? Numero { get; set; }
        public string? Complemento { get; set; }
        public string? Bairro { get; set; }
        public string? Cidade { get; set; }
        public string? Uf { get; set; }
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        public bool Ativo { get; set; }
        /// <summary>Cliente de teste: pode ser simulado e nunca recebe WhatsApp de verdade.</summary>
        public bool Simulado { get; set; }
        public DateTime CriadoEm { get; set; }
        public DateTime AtualizadoEm { get; set; }
    }

    /// <summary>Corpo de criacao e de edicao (a edicao substitui os campos).</summary>
    public sealed class ClienteRequest
    {
        public string? Nome { get; set; }
        public string? Telefone { get; set; }
        public string? Email { get; set; }
        public string? Observacoes { get; set; }
        public string? Cep { get; set; }
        public string? Logradouro { get; set; }
        public string? Numero { get; set; }
        public string? Complemento { get; set; }
        public string? Bairro { get; set; }
        public string? Cidade { get; set; }
        public string? Uf { get; set; }
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        public bool Simulado { get; set; }
    }

    /// <summary>Corpo de "o cliente de teste manda uma mensagem" (simulador).</summary>
    public sealed class SimulatorClienteMessageRequest
    {
        public string? Texto { get; set; }
    }

    public sealed class ClienteFiltroRequest
    {
        /// <summary>Busca por nome, telefone (so digitos), e-mail ou bairro.</summary>
        public string? Q { get; set; }
        public bool IncluirInativos { get; set; }
        public int Page { get; set; } = 1;
        public int PageSize { get; set; } = 25;
    }

    public sealed class ClienteListaDto
    {
        public IReadOnlyList<ClienteDto> Itens { get; set; } = Array.Empty<ClienteDto>();
        public int Total { get; set; }
        public int Page { get; set; }
        public int PageSize { get; set; }
    }
}
