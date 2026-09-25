namespace APIBack.Model.Enum
{
    public enum StatusPedido
    {
        Pendente = 1,
        EmRota = 2,
        Concluido = 3,
        Cancelado = 4,
        Atribuido = 5,
        /// <summary>
        /// Pedido em montagem: nunca aparece no mapa, na fila, nas metricas nem nas listas.
        /// Confirmar o torna Pendente.
        /// </summary>
        Rascunho = 6
    }

    public static class StatusPedidoExtensions
    {
        public static StatusPedido? FromDbValue(int? value) => value switch
        {
            1 => StatusPedido.Pendente,
            2 => StatusPedido.EmRota,
            3 => StatusPedido.Concluido,
            4 => StatusPedido.Cancelado,
            5 => StatusPedido.Atribuido,
            6 => StatusPedido.Rascunho,
            _ => null
        };

        public static string ToApiKey(this StatusPedido status) => status switch
        {
            StatusPedido.Pendente => "pendente",
            StatusPedido.EmRota => "em_rota",
            StatusPedido.Concluido => "concluido",
            StatusPedido.Cancelado => "cancelado",
            StatusPedido.Atribuido => "atribuido",
            StatusPedido.Rascunho => "rascunho",
            _ => "pendente"
        };

        public static string ToApiKey(int? dbValue) =>
            (FromDbValue(dbValue) ?? StatusPedido.Pendente).ToApiKey();
    }
}
