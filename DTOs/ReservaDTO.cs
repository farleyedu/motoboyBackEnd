using System;
using System.ComponentModel.DataAnnotations;

namespace APIBack.DTOs
{
    // ============================================================================
    // DTO PARA CRIAR NOVA RESERVA
    // ============================================================================

    /// <summary>
    /// DTO para criacao de nova reserva
    /// </summary>
    public class CriarReservaDTO
    {
        /// <summary>
        /// Nome completo do cliente
        /// </summary>
        [Required(ErrorMessage = "Nome do cliente e obrigatorio")]
        [StringLength(200, ErrorMessage = "Nome deve ter no maximo 200 caracteres")]
        public string NomeClienteReserva { get; set; } = string.Empty;

        /// <summary>
        /// Telefone de contato
        /// </summary>
        [Required(ErrorMessage = "Telefone e obrigatorio")]
        [Phone(ErrorMessage = "Telefone invalido")]
        public string Telefone { get; set; } = string.Empty;

        /// <summary>
        /// Email do cliente (opcional)
        /// </summary>
        [EmailAddress(ErrorMessage = "Email invalido")]
        public string? Email { get; set; }

        /// <summary>
        /// Quantidade de pessoas (minimo 10)
        /// </summary>
        [Required(ErrorMessage = "Quantidade de pessoas e obrigatoria")]
        [Range(10, 200, ErrorMessage = "Quantidade deve ser entre 10 e 200 pessoas")]
        public int QtdPessoas { get; set; }

        /// <summary>
        /// Data da reserva
        /// </summary>
        [Required(ErrorMessage = "Data da reserva e obrigatoria")]
        public DateTime DataReserva { get; set; }

        /// <summary>
        /// Horario de inicio (formato HH:mm)
        /// </summary>
        [Required(ErrorMessage = "Horario e obrigatorio")]
        [RegularExpression(@"^([01]?[0-9]|2[0-3]):[0-5][0-9]$", ErrorMessage = "Horario invalido. Use formato HH:mm")]
        public string HoraInicio { get; set; } = string.Empty;

        /// <summary>
        /// Observacoes adicionais
        /// </summary>
        [StringLength(500, ErrorMessage = "Observacoes devem ter no maximo 500 caracteres")]
        public string? Observacoes { get; set; }

        /// <summary>
        /// Indica se ha aniversariante (ganha brownie ou drink)
        /// </summary>
        public bool? IsAniversariante { get; set; }

        /// <summary>
        /// ID do estabelecimento
        /// </summary>
        [Required(ErrorMessage = "ID do estabelecimento e obrigatorio")]
        public Guid IdEstabelecimento { get; set; }
    }

    // ============================================================================
    // DTO PARA RESPOSTA/LEITURA DE RESERVA
    // ============================================================================

    /// <summary>
    /// DTO para retornar dados de uma reserva
    /// </summary>
    public class ReservaResponseDTO
    {
        public int Id { get; set; }
        public string CodigoReserva { get; set; } = string.Empty;
        public string NomeClienteReserva { get; set; } = string.Empty;
        public string Telefone { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public int QtdPessoas { get; set; }
        public string DataReserva { get; set; } = string.Empty;
        public string HoraInicio { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string Observacoes { get; set; } = string.Empty;
        public bool IsAniversariante { get; set; }
        public DateTime DataCriacao { get; set; }
        public DateTime? DataAtualizacao { get; set; }
    }

    // ============================================================================
    // DTO PARA ATUALIZAR RESERVA EXISTENTE
    // ============================================================================

    /// <summary>
    /// DTO para atualizacao de reserva (campos opcionais)
    /// </summary>
    public class AtualizarReservaDTO
    {
        [Required(ErrorMessage = "ID da reserva e obrigatorio")]
        public int Id { get; set; }

        [StringLength(200, ErrorMessage = "Nome deve ter no maximo 200 caracteres")]
        public string? NomeClienteReserva { get; set; }

        [Phone(ErrorMessage = "Telefone invalido")]
        public string? Telefone { get; set; }

        [EmailAddress(ErrorMessage = "Email invalido")]
        public string? Email { get; set; }

        [Range(10, 200, ErrorMessage = "Quantidade deve ser entre 10 e 200 pessoas")]
        public int? QtdPessoas { get; set; }

        public DateTime? DataReserva { get; set; }

        [RegularExpression(@"^([01]?[0-9]|2[0-3]):[0-5][0-9]$", ErrorMessage = "Horario invalido. Use formato HH:mm")]
        public string? HoraInicio { get; set; }

        [StringLength(500, ErrorMessage = "Observacoes devem ter no maximo 500 caracteres")]
        public string? Observacoes { get; set; }

        public bool? IsAniversariante { get; set; }

        [RegularExpression("^(confirmada|cancelada|pendente)$", ErrorMessage = "Status deve ser: confirmada, cancelada ou pendente")]
        public string? Status { get; set; }
    }

    // ============================================================================
    // DTO PARA LISTAR RESERVAS (VERSAO RESUMIDA)
    // ============================================================================

    public class ReservaListagemDTO
    {
        public int Id { get; set; }
        public string CodigoReserva { get; set; } = string.Empty;
        public string NomeClienteReserva { get; set; } = string.Empty;
        public string Telefone { get; set; } = string.Empty;
        public int QtdPessoas { get; set; }
        public string DataReserva { get; set; } = string.Empty;
        public string HoraInicio { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public bool IsAniversariante { get; set; }
    }

    // ============================================================================
    // DTO PARA VALIDACAO DE DISPONIBILIDADE
    // ============================================================================

    public class VerificarDisponibilidadeDTO
    {
        [Required(ErrorMessage = "ID do estabelecimento e obrigatorio")]
        public Guid IdEstabelecimento { get; set; }

        [Required(ErrorMessage = "Data e obrigatoria")]
        public DateTime DataReserva { get; set; }

        [Required(ErrorMessage = "Quantidade de pessoas e obrigatoria")]
        [Range(10, 200, ErrorMessage = "Quantidade deve ser entre 10 e 200 pessoas")]
        public int QtdPessoas { get; set; }
    }

    public class DisponibilidadeResponseDTO
    {
        public bool Disponivel { get; set; }
        public string Mensagem { get; set; } = string.Empty;
        public int? PessoasDisponiveis { get; set; }
        public int? PessoasOcupadas { get; set; }
        public int? CapacidadeTotal { get; set; }
    }
}