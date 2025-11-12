using System;
using System.ComponentModel.DataAnnotations;

namespace APIBack.DTOs
{
    // ============================================================================
    // DTO PARA CRIAR NOVA RESERVA
    // ============================================================================

    /// <summary>
    /// DTO para criação de nova reserva
    /// </summary>
    public class CriarReservaDTO
    {
        /// <summary>
        /// Nome completo do cliente
        /// </summary>
        [Required(ErrorMessage = "Nome do cliente é obrigatório")]
        [StringLength(200, ErrorMessage = "Nome deve ter no máximo 200 caracteres")]
        public string NomeClienteReserva { get; set; }

        /// <summary>
        /// Telefone de contato
        /// </summary>
        [Required(ErrorMessage = "Telefone é obrigatório")]
        [Phone(ErrorMessage = "Telefone inválido")]
        public string Telefone { get; set; }

        /// <summary>
        /// Email do cliente (opcional)
        /// </summary>
        [EmailAddress(ErrorMessage = "Email inválido")]
        public string Email { get; set; }

        /// <summary>
        /// Quantidade de pessoas (mínimo 10)
        /// </summary>
        [Required(ErrorMessage = "Quantidade de pessoas é obrigatória")]
        [Range(10, 200, ErrorMessage = "Quantidade deve ser entre 10 e 200 pessoas")]
        public int QtdPessoas { get; set; }

        /// <summary>
        /// Data da reserva
        /// </summary>
        [Required(ErrorMessage = "Data da reserva é obrigatória")]
        public DateTime DataReserva { get; set; }

        /// <summary>
        /// Horário de início (formato: HH:mm)
        /// </summary>
        [Required(ErrorMessage = "Horário é obrigatório")]
        [RegularExpression(@"^([01]?[0-9]|2[0-3]):[0-5][0-9]$", ErrorMessage = "Horário inválido. Use formato HH:mm")]
        public string HoraInicio { get; set; }

        /// <summary>
        /// Observações adicionais
        /// </summary>
        [StringLength(500, ErrorMessage = "Observações devem ter no máximo 500 caracteres")]
        public string Observacoes { get; set; }

        /// <summary>
        /// Indica se há aniversariante (ganha brownie ou drink)
        /// </summary>
        public bool? IsAniversariante { get; set; }

        /// <summary>
        /// ID do estabelecimento
        /// </summary>
        [Required(ErrorMessage = "ID do estabelecimento é obrigatório")]
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
        /// <summary>
        /// ID da reserva
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// Código único da reserva
        /// </summary>
        public string CodigoReserva { get; set; }

        /// <summary>
        /// Nome do cliente
        /// </summary>
        public string NomeClienteReserva { get; set; }

        /// <summary>
        /// Telefone de contato
        /// </summary>
        public string Telefone { get; set; }

        /// <summary>
        /// Email do cliente
        /// </summary>
        public string Email { get; set; }

        /// <summary>
        /// Quantidade de pessoas
        /// </summary>
        public int QtdPessoas { get; set; }

        /// <summary>
        /// Data da reserva (formato: yyyy-MM-dd)
        /// </summary>
        public string DataReserva { get; set; }

        /// <summary>
        /// Horário de início
        /// </summary>
        public string HoraInicio { get; set; }

        /// <summary>
        /// Status: confirmada, cancelada, pendente
        /// </summary>
        public string Status { get; set; }

        /// <summary>
        /// Observações
        /// </summary>
        public string Observacoes { get; set; }

        /// <summary>
        /// Indica se há aniversariante (ganha brownie ou drink)
        /// </summary>
        public bool IsAniversariante { get; set; }

        /// <summary>
        /// Data de criação da reserva
        /// </summary>
        public DateTime DataCriacao { get; set; }

        /// <summary>
        /// Data da última atualização
        /// </summary>
        public DateTime? DataAtualizacao { get; set; }
    }

    // ============================================================================
    // DTO PARA ATUALIZAR RESERVA EXISTENTE
    // ============================================================================

    /// <summary>
    /// DTO para atualização de reserva
    /// Todos os campos são opcionais (atualiza apenas o que for enviado)
    /// </summary>
    public class AtualizarReservaDTO
    {
        /// <summary>
        /// ID da reserva a ser atualizada
        /// </summary>
        [Required(ErrorMessage = "ID da reserva é obrigatório")]
        public int Id { get; set; }

        /// <summary>
        /// Novo nome do cliente (opcional)
        /// </summary>
        [StringLength(200, ErrorMessage = "Nome deve ter no máximo 200 caracteres")]
        public string NomeClienteReserva { get; set; }

        /// <summary>
        /// Novo telefone (opcional)
        /// </summary>
        [Phone(ErrorMessage = "Telefone inválido")]
        public string Telefone { get; set; }

        /// <summary>
        /// Novo email (opcional)
        /// </summary>
        [EmailAddress(ErrorMessage = "Email inválido")]
        public string Email { get; set; }

        /// <summary>
        /// Nova quantidade de pessoas (opcional, mínimo 10)
        /// </summary>
        [Range(10, 200, ErrorMessage = "Quantidade deve ser entre 10 e 200 pessoas")]
        public int? QtdPessoas { get; set; }

        /// <summary>
        /// Nova data da reserva (opcional)
        /// </summary>
        public DateTime? DataReserva { get; set; }

        /// <summary>
        /// Novo horário (opcional)
        /// </summary>
        [RegularExpression(@"^([01]?[0-9]|2[0-3]):[0-5][0-9]$", ErrorMessage = "Horário inválido. Use formato HH:mm")]
        public string HoraInicio { get; set; }

        /// <summary>
        /// Novas observações (opcional)
        /// </summary>
        [StringLength(500, ErrorMessage = "Observações devem ter no máximo 500 caracteres")]
        public string Observacoes { get; set; }

        /// <summary>
        /// Atualizar se há aniversariante (opcional)
        /// </summary>
        public bool? IsAniversariante { get; set; }

        /// <summary>
        /// Novo status (opcional)
        /// </summary>
        [RegularExpression("^(confirmada|cancelada|pendente)$", ErrorMessage = "Status deve ser: confirmada, cancelada ou pendente")]
        public string Status { get; set; }
    }

    // ============================================================================
    // DTO PARA LISTAR RESERVAS (VERSÃO RESUMIDA)
    // ============================================================================

    /// <summary>
    /// DTO resumido para listagem de reservas
    /// </summary>
    public class ReservaListagemDTO
    {
        public int Id { get; set; }
        public string CodigoReserva { get; set; }
        public string NomeClienteReserva { get; set; }
        public string Telefone { get; set; }
        public int QtdPessoas { get; set; }
        public string DataReserva { get; set; }
        public string HoraInicio { get; set; }
        public string Status { get; set; }
        public bool IsAniversariante { get; set; }
    }

    // ============================================================================
    // DTO PARA VALIDAÇÃO DE DISPONIBILIDADE
    // ============================================================================

    /// <summary>
    /// DTO para verificar disponibilidade antes de criar reserva
    /// </summary>
    public class VerificarDisponibilidadeDTO
    {
        [Required(ErrorMessage = "ID do estabelecimento é obrigatório")]
        public Guid IdEstabelecimento { get; set; }

        [Required(ErrorMessage = "Data é obrigatória")]
        public DateTime DataReserva { get; set; }

        [Required(ErrorMessage = "Quantidade de pessoas é obrigatória")]
        [Range(10, 200, ErrorMessage = "Quantidade deve ser entre 10 e 200 pessoas")]
        public int QtdPessoas { get; set; }
    }

    /// <summary>
    /// DTO de resposta para verificação de disponibilidade
    /// </summary>
    public class DisponibilidadeResponseDTO
    {
        public bool Disponivel { get; set; }
        public string Mensagem { get; set; }
        public int? PessoasDisponiveis { get; set; }
        public int? PessoasOcupadas { get; set; }
        public int? CapacidadeTotal { get; set; }
    }
}