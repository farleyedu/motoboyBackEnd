using APIBack.DTOs.Delivery;
using Dapper;
using APIBack.GestaoLogica.Caixa;
using APIBack.GestaoLogica.Vendas;
using APIBack.Options;
using APIBack.Repository;
using APIBack.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace APIBack.Tests.Integration;

/// <summary>
/// 6C6 contra PostgreSQL de verdade: a posição do rastreio vira presença para o Caixa só nas transições (sem repetir, sem voltar com
/// posição velha, sem decidir com GPS impreciso), o "cheguei" manual vale sem GPS, nada disso confirma acerto, e o app do motoboy lê a
/// conferência dos lotes da 6C5 (conferido, parcial, pendente, corrigido) sem enxergar outra loja.
/// </summary>
public partial class DeliverySyncDatabaseTests
{
    // Loja no Centro de Uberlândia; ~0,001 grau de latitude = ~111 m. Raio do teste: 100 m (delivery_settings da fixture).
    private const double LojaLat = -18.9186, LojaLon = -48.2772;

    [DeliveryDatabaseFact]
    public async Task Gestao6C6_chegada_pelo_gps_destaca_sem_repetir_e_conferencia_aparece_para_o_motoboy()
    {
        await using var db = await VendaDatabase();
        await db.Execute(@"ALTER TABLE estabelecimentos ADD COLUMN latitude double precision, ADD COLUMN longitude double precision;
UPDATE estabelecimentos SET latitude = @lat, longitude = @lon WHERE id = @loja;", new { lat = LojaLat, lon = LojaLon, loja = db.StoreId });
        var livro = Livro(db.Source);
        var chegadas = new ChegadaService(db.Source, Microsoft.Extensions.Options.Options.Create(new DeliveryTrackingOptions()), NullLogger<ChegadaService>.Instance);
        var caixa = new CaixaService(db.Source, livro, chegadas: chegadas);
        var operador = GestaoPapeis.Resolver((m, _) => m is "Caixa", false);
        var gerente = GestaoPapeis.Resolver((_, _) => true, false);
        var hoje = await caixa.Hoje(db.StoreId, default);
        var agora = DateTimeOffset.UtcNow;

        // Pedido 23 (dinheiro 66,90 + Pix 20) e 24 (cartão) entregues pelo motoboy 1: ficam pendentes com ele.
        await new PedidoQueueRepository(db.Source).CompleteDeliveryAsync(db.StoreId, 1, 7, db.SessionId, 4, CompletionRequest(), default);
        await using (var c = await db.Source.OpenConnectionAsync())
        {
            await using var tx = await c.BeginTransactionAsync();
            await VendaDoPedido.PublicarAsync(livro, c, tx, db.StoreId, 24, 7, ModalidadeVenda.Delivery,
                new[] { new DeliveryPaymentPart { Method = "cartao_entrega", Amount = 64, ReceivedConfirmed = true } }, motoboyId: 1);
            await tx.CommitAsync();
        }
        // Última posição recebida pelo rastreio (é ela que diz se o sinal está fresco).
        await db.Execute(@"INSERT INTO motoboy_location_current (session_id, motoboy_id, estabelecimento_id, sample_id, sequence, latitude, longitude,
 accuracy_meters, captured_at_utc, received_at_utc, version) VALUES (@s, 1, @loja, gen_random_uuid(), 9, @lat, @lon, 10, now(), now(), 1)",
            new { s = db.SessionId, loja = db.StoreId, lat = LojaLat, lon = LojaLon });
        async Task<(string? Estado, string? Origem, DateTimeOffset? Desde)> Gravado()
        {
            await using var c = await db.Source.OpenConnectionAsync();
            return await c.QuerySingleOrDefaultAsync<(string?, string?, DateTimeOffset?)>(
                "SELECT estado, origem, desde FROM gestao_caixa_chegadas WHERE estabelecimento_id = @loja AND motoboy_id = 1", new { loja = db.StoreId });
        }

        // Longe → aproximando → GPS impreciso dentro (não decide) → chegada confirmada.
        await chegadas.AoReceberPosicao(db.StoreId, 1, LojaLat + 0.03, LojaLon, 10, agora.AddSeconds(-40), default);
        Assert.Equal(ChegadaRegras.Rota, (await Gravado()).Estado);
        await chegadas.AoReceberPosicao(db.StoreId, 1, LojaLat + 0.003, LojaLon, 15, agora.AddSeconds(-30), default);
        Assert.Equal(ChegadaRegras.Chegando, (await Gravado()).Estado);
        await chegadas.AoReceberPosicao(db.StoreId, 1, LojaLat, LojaLon, 400, agora.AddSeconds(-25), default);
        Assert.Equal(ChegadaRegras.Chegando, (await Gravado()).Estado);
        await chegadas.AoReceberPosicao(db.StoreId, 1, LojaLat + 0.0002, LojaLon, 12, agora.AddSeconds(-20), default);
        var chegou = await Gravado();
        Assert.Equal((ChegadaRegras.Loja, ChegadaRegras.OrigemGps), (chegou.Estado, chegou.Origem));

        // Parado na loja: nova posição igual não regrava (sem aviso repetido); posição mais velha (lote offline) não volta o estado.
        await chegadas.AoReceberPosicao(db.StoreId, 1, LojaLat, LojaLon, 8, agora.AddSeconds(-10), default);
        Assert.Equal(chegou.Desde, (await Gravado()).Desde);
        await chegadas.AoReceberPosicao(db.StoreId, 1, LojaLat + 0.03, LojaLon, 10, agora.AddSeconds(-35), default);
        Assert.Equal(ChegadaRegras.Loja, (await Gravado()).Estado);
        // Posição de outra loja (sem coordenada cadastrada) não cria nada nesta nem naquela.
        await chegadas.AoReceberPosicao(Guid.NewGuid(), 1, LojaLat, LojaLon, 5, agora, default);
        Assert.Equal(1, await db.Scalar<int>("SELECT count(*)::int FROM gestao_caixa_chegadas"));

        // O Caixa destaca, com o sinal; o GPS não acertou nada.
        var mb = Assert.Single(await caixa.MotoboysDoDia(db.StoreId, hoje, default), m => m.MotoboyId == 1);
        Assert.Equal((ChegadaRegras.Loja, ChegadaRegras.SinalOk, ChegadaRegras.OrigemGps, 2), (mb.Chegada, mb.Sinal, mb.ChegadaOrigem, mb.Pendentes.Count));
        Assert.Empty(mb.Lotes);

        // Saiu de novo (GPS bom, longe) e voltou sem GPS: o "cheguei" manual vale; sinal ausente aparece à parte.
        await chegadas.AoReceberPosicao(db.StoreId, 1, LojaLat + 0.03, LojaLon, 10, agora.AddSeconds(-5), default);
        Assert.Equal(ChegadaRegras.Rota, (await Gravado()).Estado);
        await db.Execute("DELETE FROM motoboy_location_current");
        await chegadas.AoChegarManual(db.StoreId, 1, default);
        mb = Assert.Single(await caixa.MotoboysDoDia(db.StoreId, hoje, default), m => m.MotoboyId == 1);
        Assert.Equal((ChegadaRegras.Loja, ChegadaRegras.SinalAusente, ChegadaRegras.OrigemManual), (mb.Chegada, mb.Sinal, mb.ChegadaOrigem));

        // Conferência no app: antes do lote, tudo pendente.
        var antes = await caixa.ConferenciasDoMotoboy(db.StoreId, 1, agora.AddDays(-1), default);
        Assert.Equal(new[] { ("pendente", 23), ("pendente", 24) }, antes.Pedidos.Select(p => (p.Situacao, p.PedidoId)));

        // Lote parcial com diferença: 23 conferido, 24 continua pendente; o app é avisado pela sessão dele, sem valores no aviso.
        var lote = await caixa.Acertar(db.StoreId, 7, operador, new AcertoRequest("6c6-a1", hoje, 1, new[] { new ItemAcertoRequest(23) }, 60m, "faltou troco"), default);
        var depois = await caixa.ConferenciasDoMotoboy(db.StoreId, 1, agora.AddDays(-1), default);
        var p23 = depois.Pedidos.Single(p => p.PedidoId == 23);
        Assert.Equal(("conferido", "L-0001", "parcial", -6.90m, "Marina", "faltou troco"), (p23.Situacao, p23.Lote, p23.LoteSituacao, p23.Diferenca, p23.ConferidoPor, p23.Motivo));
        Assert.Equal("pendente", depois.Pedidos.Single(p => p.PedidoId == 24).Situacao);
        var aviso = await db.Scalar<string>("SELECT target_group || '|' || payload::text FROM delivery_realtime_outbox WHERE event_name = 'rider.conference.updated'");
        Assert.StartsWith($"delivery-session:{db.SessionId:N}|", aviso);
        Assert.DoesNotContain("66.9", aviso);
        // Reenvio do mesmo lote não duplica nem o lote nem o aviso.
        await caixa.Acertar(db.StoreId, 7, operador, new AcertoRequest("6c6-a1", hoje, 1, new[] { new ItemAcertoRequest(23) }, 60m, "faltou troco"), default);
        Assert.Equal(1, await db.Scalar<int>("SELECT count(*)::int FROM delivery_realtime_outbox WHERE event_name = 'rider.conference.updated'"));

        // Correção do gerente: o pedido aparece corrigido, com a diferença final.
        await caixa.CorrigirAcerto(db.StoreId, 7, gerente, lote.Id, new CorrigirAcertoRequest("6c6-c1", 66.90m, "achou a nota na mochila"), default);
        var corrigido = (await caixa.ConferenciasDoMotoboy(db.StoreId, 1, agora.AddDays(-1), default)).Pedidos.Single(p => p.PedidoId == 23);
        Assert.Equal(("corrigido", 0m, (string?)null), (corrigido.Situacao, corrigido.Diferenca, corrigido.Motivo));
        Assert.NotNull(corrigido.CorrigidoEm);

        // Outra loja não enxerga nada deste motoboy.
        Assert.Empty((await caixa.ConferenciasDoMotoboy(Guid.NewGuid(), 1, agora.AddDays(-1), default)).Pedidos);
    }
}
