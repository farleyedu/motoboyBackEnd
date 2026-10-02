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
        Rascunho = 6,
        /// <summary>
        /// Pedido que ficou em aberto alem do expediente e foi encerrado pelo sistema (ou pelo
        /// atendente, ao encerrar o expediente). Nao conta como entregue nem cancelado: o motoboy
        /// com quem estava fica gravado em pedido_encerramento. Pode ser reaberto (volta a Pendente).
        /// </summary>
        EncerradoAuto = 7,
        /// <summary>
        /// O atendente enviou o pedido (a rota) ao motoboy e ele ainda nao aceitou nem recusou. Para o
        /// painel e igual a "em rota"; so e interno. Aceitar vira Atribuido/EmRota; recusar volta a Pendente.
        /// </summary>
        AguardandoMotoboy = 8
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
            7 => StatusPedido.EncerradoAuto,
            8 => StatusPedido.AguardandoMotoboy,
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
            StatusPedido.EncerradoAuto => "encerrado_auto",
            StatusPedido.AguardandoMotoboy => "aguardando_motoboy",
            _ => "pendente"
        };

        public static string ToApiKey(int? dbValue) =>
            (FromDbValue(dbValue) ?? StatusPedido.Pendente).ToApiKey();
    }
}
