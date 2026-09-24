namespace APIBack.DTOs.Delivery
{
    /// <summary>
    /// Edicao completa de um pedido pelo simulador. Regra unica para todos os campos:
    /// nulo = manter o que esta; texto vazio = limpar. Nao mexe em motoboy, fila nem status:
    /// isso continua nos comandos de fila (atribuir, remover, cancelar, concluir).
    /// </summary>
    public sealed class SimulatorPedidoRequest
    {
        public string? NomeCliente { get; set; }
        public string? TelefoneCliente { get; set; }
        public string? Rua { get; set; }
        public string? Numero { get; set; }
        public string? Complemento { get; set; }
        public string? Bairro { get; set; }
        public string? Cidade { get; set; }
        public string? Estado { get; set; }
        public string? Cep { get; set; }
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        /// <summary>Texto livre ou JSON [{"nome","quantidade","preco"}].</summary>
        public string? Items { get; set; }
        public decimal? Value { get; set; }
        public string? TipoPagamento { get; set; }
        /// <summary>Ex.: Pendente, Pago.</summary>
        public string? StatusPagamento { get; set; }
        /// <summary>Zero limpa o troco.</summary>
        public decimal? Troco { get; set; }
        public string? Observacoes { get; set; }
        public string? CodigoEntrega { get; set; }
        /// <summary>
        /// Previsao de entrega em minutos a partir de AGORA. Negativo = ja vencida (simula
        /// pedido atrasado). Ex.: -30 = venceu ha 30 min; -4320 = ha 3 dias.
        /// </summary>
        public int? PrevisaoEmMinutos { get; set; }
        /// <summary>O pedido foi feito ha N minutos (ajusta data e horario do pedido).</summary>
        public int? PedidoHaMinutos { get; set; }
    }
}
