using System;
using System.Threading.Tasks;
using APIBack.Model.Enum;
using APIBack.Service;
using Dapper;
using Npgsql;

namespace APIBack.Repository;

/// <summary>
/// Entrega manual (planos Partida e Rota, sem app do motoboy): a loja marca "saiu para entrega" e "entregue".
/// Pedido do iFood segue os comandos oficiais (GetPedidoAsync com trava recusa) e pedido que esta com um motoboy segue
/// a fila dele. O aviso "saiu da loja" ao cliente sai pelo TrackingNoticeService, sem link de rastreio.
/// </summary>
public sealed partial class PedidoQueueRepository
{
    public async Task<string> MarcarSaiuManualAsync(Guid estabelecimentoId, int actorUserId, int pedidoId, string? entregadorNome)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var pedido = await GetPedidoAsync(connection, transaction, estabelecimentoId, pedidoId, forUpdate: true)
            ?? throw new DeliveryDomainException(404, "PEDIDO_NOT_FOUND", "Pedido nao encontrado neste estabelecimento.");
        var status = StatusPedidoExtensions.FromDbValue(pedido.StatusPedido) ?? StatusPedido.Pendente;
        if (status == StatusPedido.Rascunho)
            throw new DeliveryDomainException(409, "PEDIDO_RASCUNHO", "Confirme o pedido antes de marcar a saída.");
        if (status != StatusPedido.Pendente)
            throw new DeliveryDomainException(409, "PEDIDO_NAO_PENDENTE", "Só sai para entrega um pedido pendente.");
        await GarantirSemMotoboyAsync(connection, transaction, estabelecimentoId, pedido);

        var saidaNow = await PedidoColumnTypes.LocalNowSqlAsync(connection, transaction, "horario_saida");
        await connection.ExecuteAsync($@"
UPDATE pedido
   SET status_pedido = @EmRota, horario_saida = {saidaNow},
       entrega_manual_saiu_utc = NOW(), entregador_nome = @Entregador
 WHERE id = @PedidoId;",
            new { EmRota = (int)StatusPedido.EmRota, PedidoId = pedidoId, Entregador = Normalizar(entregadorNome) }, transaction);
        await PublishOrderEventAsync(connection, transaction, estabelecimentoId, actorUserId, pedidoId, "manual_dispatch");
        await transaction.CommitAsync();
        return StatusPedido.EmRota.ToApiKey();
    }

    /// <summary>Vale para pedido que saiu (em rota manual) e para o pendente entregue direto (retirada no balcao).</summary>
    public async Task<string> MarcarEntregueManualAsync(Guid estabelecimentoId, int actorUserId, int pedidoId)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var pedido = await GetPedidoAsync(connection, transaction, estabelecimentoId, pedidoId, forUpdate: true)
            ?? throw new DeliveryDomainException(404, "PEDIDO_NOT_FOUND", "Pedido nao encontrado neste estabelecimento.");
        var status = StatusPedidoExtensions.FromDbValue(pedido.StatusPedido) ?? StatusPedido.Pendente;
        if (status == StatusPedido.Concluido) return status.ToApiKey();
        if (status is not (StatusPedido.Pendente or StatusPedido.EmRota))
            throw new DeliveryDomainException(409, "PEDIDO_NAO_ENTREGAVEL", "Este pedido não pode ser marcado como entregue.");
        await GarantirSemMotoboyAsync(connection, transaction, estabelecimentoId, pedido);

        var entregaNow = await PedidoColumnTypes.LocalNowSqlAsync(connection, transaction, "horario_entrega");
        await connection.ExecuteAsync($@"
UPDATE pedido
   SET status_pedido = @Concluido, horario_entrega = {entregaNow}, entrega_manual_entregue_utc = NOW()
 WHERE id = @PedidoId;",
            new { Concluido = (int)StatusPedido.Concluido, PedidoId = pedidoId }, transaction);
        await PublishOrderEventAsync(connection, transaction, estabelecimentoId, actorUserId, pedidoId, "manual_delivered");
        await transaction.CommitAsync();
        return StatusPedido.Concluido.ToApiKey();
    }

    private static async Task GarantirSemMotoboyAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid estabelecimentoId, PedidoRow pedido)
    {
        var parada = await GetActiveStopForPedidoAsync(connection, transaction, estabelecimentoId, pedido.Id, forUpdate: false);
        if (parada != null || pedido.MotoboyResponsavel.HasValue)
            throw new DeliveryDomainException(409, "PEDIDO_COM_MOTOBOY",
                "Este pedido está com um motoboy: a saída e a entrega seguem pelo app dele.");
    }

    private static string? Normalizar(string? nome)
    {
        var valor = nome?.Trim();
        if (string.IsNullOrEmpty(valor)) return null;
        return valor.Length > 80 ? valor[..80] : valor;
    }
}
