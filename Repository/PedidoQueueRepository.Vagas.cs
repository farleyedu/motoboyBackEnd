using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.Service;
using APIBack.Turnos;
using Dapper;
using Npgsql;

namespace APIBack.Repository;

/// <summary>
/// Créditos de motoboy do turno de entrega (regra em <see cref="TurnoEntregaRegras"/>): reservado quando o motoboy
/// recebe o pedido, gasto quando ele sai para entregar. A reserva é lida das paradas ativas, então volta sozinha quando o
/// pedido sai dele antes disso. Chamar sempre depois das travas de fila e de loja: o turno trava a loja por último.
/// </summary>
public sealed partial class PedidoQueueRepository
{
    /// <summary>Antes de entregar um pedido a um motoboy: reservados + gastos nunca passam do total.</summary>
    private static async Task ExigirCreditoParaReceberAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid estabelecimentoId, int motoboyId, int pedidoId)
    {
        if (!await TurnoEntregaStore.SchemaProntoAsync(connection, transaction)) return;
        if (await PedidoIsentoDeCreditoAsync(connection, transaction, pedidoId)) return;
        var situacao = await TurnoEntregaStore.SincronizarAsync(connection, transaction, estabelecimentoId, TimeProvider.System);
        if (situacao == null) return;
        var canonico = await TurnoEntregaStore.CanonicoAsync(connection, transaction, motoboyId);
        if (situacao.PodeReceber(canonico)) return;
        var reservados = situacao.Reservados.Count > 0 ? $" e {situacao.Reservados.Count} reservado(s)" : string.Empty;
        throw ErrosOperacionais.Excecao(ErrosOperacionais.VagasMotoboyEsgotadas,
            $"Créditos de motoboy deste turno de entrega esgotados ({situacao.Gastos.Count} usado(s){reservados} de {situacao.Total}). " +
            "Escolha um motoboy que já está neste turno ou compre mais motoboys.",
            dados: TurnoEntregaService.Dados(situacao));
    }

    /// <summary>No aceite o pedido já está reservado; só barra se os créditos acabaram de verdade nesse meio tempo.</summary>
    private static async Task ExigirCreditoParaAceitarAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid estabelecimentoId, int motoboyId, IEnumerable<int> pedidoIds)
    {
        if (!await TurnoEntregaStore.SchemaProntoAsync(connection, transaction)) return;
        if (await PrimeiroPedidoQueContaAsync(connection, transaction, pedidoIds) == null) return;
        var situacao = await TurnoEntregaStore.SincronizarAsync(connection, transaction, estabelecimentoId, TimeProvider.System);
        if (situacao is not { Esgotado: true }) return;
        if (situacao.Gastos.Contains(await TurnoEntregaStore.CanonicoAsync(connection, transaction, motoboyId))) return;
        throw ErrosOperacionais.Excecao(ErrosOperacionais.VagasMotoboyEsgotadas,
            TurnoEntregaService.MensagemDeEntrada(situacao), dados: TurnoEntregaService.Dados(situacao));
    }

    /// <summary>
    /// O motoboy saiu para entregar: gasta o crédito dele no turno (uma vez). Nunca recusa, para não interromper ninguém.
    /// <paramref name="emRotaDesde"/>: entrega concluída sem coleta marcada que começou num turno anterior não gasta no novo.
    /// </summary>
    private static async Task GastarCreditoAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid estabelecimentoId, int motoboyId, IEnumerable<int> pedidoIds, DateTimeOffset? emRotaDesde = null)
    {
        if (!await TurnoEntregaStore.SchemaProntoAsync(connection, transaction)) return;
        if (await PrimeiroPedidoQueContaAsync(connection, transaction, pedidoIds) is not { } pedidoId) return;
        var situacao = await TurnoEntregaStore.SincronizarAsync(connection, transaction, estabelecimentoId, TimeProvider.System);
        if (situacao == null) return;
        var inicioDoAtual = situacao.Turno?.IniciadoEm ?? situacao.FechadaDesde;
        if (emRotaDesde.HasValue && inicioDoAtual.HasValue && emRotaDesde.Value < inicioDoAtual.Value) return;
        var canonico = await TurnoEntregaStore.CanonicoAsync(connection, transaction, motoboyId);
        await TurnoEntregaStore.GravarGastoAsync(connection, transaction, estabelecimentoId, situacao, canonico, pedidoId);
    }

    /// <summary>Depois de confirmar um gasto: quem sobrou sem crédito sai do ar na hora (o serviço periódico cobre o resto).</summary>
    private Task TirarSemCreditoAsync(Guid estabelecimentoId) =>
        _varredura?.TirarSemCreditoAsync(estabelecimentoId) ?? Task.CompletedTask;

    private static async Task<int?> PrimeiroPedidoQueContaAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, IEnumerable<int> pedidoIds)
    {
        foreach (var pedidoId in pedidoIds)
            if (!await PedidoIsentoDeCreditoAsync(connection, transaction, pedidoId)) return pedidoId;
        return null;
    }

    /// <summary>Pedido do simulador ou de teste não usa crédito.</summary>
    private static Task<bool> PedidoIsentoDeCreditoAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, int pedidoId) =>
        // to_jsonb: a coluna origem pode não existir num banco anterior ao núcleo de pedidos.
        connection.ExecuteScalarAsync<bool>(
            "SELECT COALESCE(to_jsonb(p)->>'origem' IN ('simulador','teste'),FALSE) FROM pedido p WHERE p.id=@PedidoId;",
            new { PedidoId = pedidoId }, transaction);
}
