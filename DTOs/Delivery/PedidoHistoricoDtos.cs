using System;
using System.Collections.Generic;

namespace APIBack.DTOs.Delivery
{
    /// <summary>
    /// Um marco na vida do pedido. `Tipo` e uma chave estavel para o painel escolher
    /// texto e icone: criado, atribuido, entrega_iniciada, coletado, chegou, entregue,
    /// nao_entregue, recusado, removido_da_fila, cancelado, transferencia_solicitada,
    /// transferencia_concluida, transferencia_rejeitada.
    /// </summary>
    public sealed class PedidoHistoricoEventoDto
    {
        public string Tipo { get; set; } = string.Empty;
        public DateTimeOffset OcorridoEmUtc { get; set; }
        public int? MotoboyId { get; set; }
        public string? MotoboyNome { get; set; }
        /// <summary>Motivo informado (falha, recusa, cancelamento, transferencia).</summary>
        public string? Motivo { get; set; }
        /// <summary>Quem agiu: motoboy, atendente ou sistema.</summary>
        public string? Ator { get; set; }
        /// <summary>Complemento curto, por exemplo "De Joao para Pedro".</summary>
        public string? Detalhe { get; set; }
    }

    public sealed class PedidoHistoricoDto
    {
        public int PedidoId { get; set; }
        public List<PedidoHistoricoEventoDto> Eventos { get; set; } = new();
    }
}
