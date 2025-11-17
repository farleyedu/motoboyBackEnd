using System;

namespace APIBack.Model
{
    /// <summary>
    /// Representa uma reserva no sistema.
    /// </summary>
    public class Reserva
    {
        /// <summary>
        /// ID unico da reserva.
        /// </summary>
        public long? Id { get; set; }

        /// <summary>
        /// ID do estabelecimento.
        /// </summary>
        public Guid IdEstabelecimento { get; set; }

        /// <summary>
        /// ID do cliente (quando houver vinculo).
        /// </summary>
        public Guid? IdCliente { get; set; }

        /// <summary>
        /// ID do profissional associado (barbearia).
        /// </summary>
        public long? IdProfissional { get; set; }

        /// <summary>
        /// ID do servico associado (barbearia).
        /// </summary>
        public long? IdServico { get; set; }

        /// <summary>
        /// Nome completo do cliente que fez a reserva.
        /// </summary>
        public string NomeClienteReserva { get; set; } = string.Empty;

        /// <summary>
        /// Alias utilizado pelo Dapper para mapear o campo nome_cliente_reserva.
        /// </summary>
        public string NomeCliente
        {
            get => NomeClienteReserva;
            set => NomeClienteReserva = value ?? string.Empty;
        }

        /// <summary>
        /// Telefone de contato do cliente.
        /// </summary>
        public string? Telefone { get; set; }

        /// <summary>
        /// Email do cliente (opcional).
        /// </summary>
        public string? Email { get; set; }

        /// <summary>
        /// Quantidade de pessoas na reserva.
        /// </summary>
        public int? QtdPessoas { get; set; }

        /// <summary>
        /// Data da reserva.
        /// </summary>
        public DateTime DataReserva { get; set; }

        /// <summary>
        /// Horario de inicio da reserva.
        /// </summary>
        public TimeSpan HoraInicio { get; set; }

        /// <summary>
        /// Horario final da reserva (quando aplicavel).
        /// </summary>
        public TimeSpan? HoraFim { get; set; }

        /// <summary>
        /// Status atual da reserva.
        /// </summary>
        public ReservaStatus Status { get; set; } = ReservaStatus.Pendente;

        /// <summary>
        /// Observacoes adicionais sobre a reserva.
        /// </summary>
        public string? Observacoes { get; set; }

        /// <summary>
        /// Codigo unico da reserva para identificacao.
        /// </summary>
        public string? CodigoReserva { get; set; }

        /// <summary>
        /// Alias para CodigoReserva utilizado em partes do codigo legado.
        /// </summary>
        public string Codigo
        {
            get => CodigoReserva ?? string.Empty;
            set => CodigoReserva = value;
        }

        /// <summary>
        /// Indica se ha aniversariante na reserva.
        /// </summary>
        public bool? IsAniversariante { get; set; }

        /// <summary>
        /// Data de criacao da reserva.
        /// </summary>
        public DateTime DataCriacao { get; set; }

        /// <summary>
        /// Data da ultima atualizacao da reserva.
        /// </summary>
        public DateTime? DataAtualizacao { get; set; }
    }
}