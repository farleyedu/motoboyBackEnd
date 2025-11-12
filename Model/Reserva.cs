using System;

namespace APIBack.Model
{
    /// <summary>
    /// Representa uma reserva no sistema
    /// </summary>
    public class Reserva
    {
        /// <summary>
        /// ID único da reserva
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// ID do estabelecimento
        /// </summary>
        public Guid IdEstabelecimento { get; set; }

        /// <summary>
        /// Nome completo do cliente que fez a reserva
        /// </summary>
        public string NomeClienteReserva { get; set; }

        /// <summary>
        /// Telefone de contato do cliente
        /// </summary>
        public string Telefone { get; set; }

        /// <summary>
        /// Email do cliente (opcional)
        /// </summary>
        public string Email { get; set; }

        /// <summary>
        /// Quantidade de pessoas na reserva
        /// Mínimo: 10 pessoas
        /// </summary>
        public int QtdPessoas { get; set; }

        /// <summary>
        /// Data da reserva
        /// </summary>
        public DateTime DataReserva { get; set; }

        /// <summary>
        /// Horário de início da reserva (formato: HH:mm:ss)
        /// </summary>
        public string HoraInicio { get; set; }

        /// <summary>
        /// Status da reserva
        /// Valores possíveis: "confirmada", "cancelada", "pendente"
        /// </summary>
        public string Status { get; set; }

        /// <summary>
        /// Observações adicionais sobre a reserva
        /// </summary>
        public string Observacoes { get; set; }

        /// <summary>
        /// Código único da reserva para identificação
        /// </summary>
        public string CodigoReserva { get; set; }

        /// <summary>
        /// Indica se há aniversariante nesta reserva
        /// Aniversariante ganha brinde: brownie ou drink por conta da casa
        /// Valor padrão: false
        /// </summary>
        public bool? IsAniversariante { get; set; }

        /// <summary>
        /// Data de criação da reserva
        /// </summary>
        public DateTime DataCriacao { get; set; }

        /// <summary>
        /// Data da última atualização
        /// </summary>
        public DateTime? DataAtualizacao { get; set; }

        // Relacionamentos (se aplicável)
        // public virtual Estabelecimento Estabelecimento { get; set; }
        // public virtual Cliente Cliente { get; set; }
    }
}