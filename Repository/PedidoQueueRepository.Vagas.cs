using System;
using System.Threading.Tasks;
using APIBack.Service;
using Dapper;
using Npgsql;

namespace APIBack.Repository;

public sealed partial class PedidoQueueRepository
{
    private sealed class VagaConfiguracaoRow
    {
        public int MotoboysContratados { get; set; }
        public string? JanelaAtual { get; set; }
        public string? JanelaPendente { get; set; }
        public DateTimeOffset? VigoraEm { get; set; }
    }

    /// <summary>
    /// Chamar após EnsureStoreOperationalAsync, que trava a linha da loja FOR UPDATE.
    /// Assim aceites de motoboys diferentes não ultrapassam o limite em concorrência.
    /// </summary>
    private static async Task GarantirVagaAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid estabelecimentoId, int motoboyId, int pedidoId, bool ocupar)
    {
        if (await PedidoIsentoDeVagaAsync(connection, transaction, pedidoId)) return;
        var config = await connection.QuerySingleAsync<VagaConfiguracaoRow>(@"
SELECT " + MotoboyLimites.SqlContratados + @" AS MotoboysContratados,
       d.order_window::text AS JanelaAtual,
       d.order_window_pending::text AS JanelaPendente,
       d.order_window_effective_from_utc AS VigoraEm
  FROM estabelecimentos e
  LEFT JOIN delivery_settings d ON d.estabelecimento_id=e.id
 WHERE e.id=@EstabelecimentoId;",
            new { EstabelecimentoId = estabelecimentoId }, transaction);
        var agora = DateTimeOffset.UtcNow;
        var janela = config.JanelaPendente != null && config.VigoraEm <= agora
            ? config.JanelaPendente : config.JanelaAtual;
        var periodo = DiaOperacionalRules.InicioAtual(OrderWindowRules.Parse(janela), agora);
        var canonico = await connection.ExecuteScalarAsync<int>(@"
SELECT COALESCE(canonical_motoboy_id,id) FROM motoboy WHERE id=@MotoboyId;",
            new { MotoboyId = motoboyId }, transaction);
        var jaOcupa = await connection.ExecuteScalarAsync<bool>(@"
SELECT EXISTS (SELECT 1 FROM motoboy_vaga_periodo
 WHERE estabelecimento_id=@EstabelecimentoId AND periodo_inicio_utc=@Periodo AND motoboy_id=@Canonico);",
            new { EstabelecimentoId = estabelecimentoId, Periodo = periodo, Canonico = canonico }, transaction);
        if (jaOcupa) return;
        var usadas = await connection.ExecuteScalarAsync<int>(@"
SELECT COUNT(*) FROM motoboy_vaga_periodo
 WHERE estabelecimento_id=@EstabelecimentoId AND periodo_inicio_utc=@Periodo;",
            new { EstabelecimentoId = estabelecimentoId, Periodo = periodo }, transaction);
        var maximo = MotoboyLimites.VagasPorDia(config.MotoboysContratados);
        if (usadas >= maximo)
            throw new DeliveryDomainException(409, "VAGAS_MOTOBOY_ESGOTADAS",
                $"Vagas de motoboy esgotadas neste dia operacional ({usadas} de {maximo}). Peça mais motoboys à Zippy.");
        if (!ocupar) return;
        await connection.ExecuteAsync(@"
INSERT INTO motoboy_vaga_periodo(estabelecimento_id,periodo_inicio_utc,motoboy_id,pedido_id)
VALUES(@EstabelecimentoId,@Periodo,@Canonico,@PedidoId)
ON CONFLICT (estabelecimento_id,periodo_inicio_utc,motoboy_id) DO NOTHING;",
            new { EstabelecimentoId = estabelecimentoId, Periodo = periodo, Canonico = canonico, PedidoId = pedidoId }, transaction);
    }

    private static async Task<bool> PedidoIsentoDeVagaAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, int pedidoId)
    {
        if (!await PedidoColumnTypes.HasCoreSchemaAsync(connection, transaction)) return false;
        return await connection.ExecuteScalarAsync<bool>(
            "SELECT COALESCE(origem IN ('simulador','teste'),FALSE) FROM pedido WHERE id=@PedidoId;",
            new { PedidoId = pedidoId }, transaction);
    }
}
