using System;
using System.ComponentModel.DataAnnotations;

namespace APIBack.DTOs.Motoboy
{
    public sealed class MotoboyCadastroRequest
    {
        [Required, MinLength(2), MaxLength(160)]
        public string Nome { get; set; } = string.Empty;

        [Required, EmailAddress, MaxLength(200)]
        public string Email { get; set; } = string.Empty;

        [MaxLength(30)]
        public string? Telefone { get; set; }

        [Required, MinLength(6), MaxLength(100)]
        public string Senha { get; set; } = string.Empty;
    }

    public sealed class MotoboyCadastroResponse
    {
        public int UserId { get; set; }
        public int MotoboyId { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
    }

    public sealed class MotoboyEstabelecimentoDisponivelDto
    {
        public Guid Id { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string? Cidade { get; set; }
        public string? Uf { get; set; }
        public string? TipoEstabelecimento { get; set; }
        public string[] ModulosAtivos { get; set; } = Array.Empty<string>();
    }

    public sealed class MotoboyLinkRequestDto
    {
        public Guid Id { get; set; }
        public int MotoboyId { get; set; }
        public Guid EstabelecimentoId { get; set; }
        public string EstabelecimentoNome { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string Origem { get; set; } = "motoboy";
        public DateTimeOffset RequestedAtUtc { get; set; }
        public DateTimeOffset? ReviewedAtUtc { get; set; }
        public string? RejectionReason { get; set; }
        public string? MotoboyNome { get; set; }
        public string? MotoboyEmail { get; set; }
        public string? MotoboyTelefone { get; set; }
    }

    public sealed class SolicitarVinculoMotoboyRequest
    {
        [Required]
        public Guid EstabelecimentoId { get; set; }
    }

    public sealed class RecusarVinculoMotoboyRequest
    {
        [MaxLength(300)]
        public string? Motivo { get; set; }
    }

    public sealed class ConvidarMotoboyRequest
    {
        [Required]
        public int MotoboyId { get; set; }
    }

    public sealed class MotoboyConviteCandidatoDto
    {
        public int MotoboyId { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string? Email { get; set; }
        public string? Telefone { get; set; }
        public string? Avatar { get; set; }
    }
}
