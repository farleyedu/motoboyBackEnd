using APIBack.Acontecimentos;
using APIBack.Options;
using APIBack.Service;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;

namespace APIBack.GestaoLogica.Caixa;

/// <summary>Presença de um motoboy para o Caixa (ver <see cref="ChegadaRegras"/>).</summary>
public sealed record ChegadaDoMotoboy(string? Estado, DateTimeOffset? Desde, string? Origem, string Sinal, DateTimeOffset? PosicaoEm);

public interface IChegadaService
{
    /// <summary>Posição já aceita e fresca do rastreio. Nunca lança: a posição não pode cair por causa do Caixa.</summary>
    Task AoReceberPosicao(Guid loja, int motoboyId, double latitude, double longitude, double? precisaoM, DateTimeOffset capturadaEm,
        CancellationToken ct);

    /// <summary>"Cheguei à loja" no app: vale sem GPS. Nunca lança.</summary>
    Task AoChegarManual(Guid loja, int motoboyId, CancellationToken ct);

    Task<IReadOnlyDictionary<int, ChegadaDoMotoboy>> Ler(NpgsqlConnection c, Guid loja, int[] motoboys, CancellationToken ct);
}

/// <summary>
/// 6C6: grava a presença só quando o estado muda e avisa o Caixa pelo tempo real da 6C4 (grupo caixa da loja, depois do commit). O raio é
/// o mesmo do retorno à loja (delivery_settings.store_return_radius_m). A rota e o retorno continuam com o PedidoQueueRepository.
/// </summary>
public sealed class ChegadaService(NpgsqlDataSource dataSource, IOptions<DeliveryTrackingOptions> options, ILogger<ChegadaService> logger,
    TimeProvider? relogio = null) : IChegadaService
{
    private readonly TimeProvider _relogio = relogio ?? TimeProvider.System;
    private static volatile bool _tabelaPronta;

    private sealed class Contexto
    {
        public double? Lat { get; set; }
        public double? Lon { get; set; }
        public string? Raio { get; set; }
        public string? Estado { get; set; }
        public DateTime? PosicaoEm { get; set; }
        public DateTime? AtualizadoEm { get; set; }
    }

    private sealed class Linha
    {
        public int MotoboyId { get; set; }
        public string? Estado { get; set; }
        public string? Origem { get; set; }
        public DateTime? Desde { get; set; }
        public DateTime? AtualizadoEm { get; set; }
        public DateTime? PosicaoEm { get; set; }
    }

    public async Task AoReceberPosicao(Guid loja, int motoboyId, double latitude, double longitude, double? precisaoM, DateTimeOffset capturadaEm,
        CancellationToken ct)
    {
        try
        {
            await using var c = await dataSource.OpenConnectionAsync(ct);
            if (!await TabelaPronta(c)) return;
            // Uma consulta no caminho quente: loja, raio configurado e o estado atual. to_jsonb tolera banco sem a coluna do raio.
            var x = await c.QuerySingleOrDefaultAsync<Contexto>(@"
SELECT e.latitude::double precision AS Lat, e.longitude::double precision AS Lon, to_jsonb(ds)->>'store_return_radius_m' AS Raio,
       ch.estado AS Estado, ch.posicao_em AS PosicaoEm, ch.atualizado_em AS AtualizadoEm
  FROM estabelecimentos e
  LEFT JOIN delivery_settings ds ON ds.estabelecimento_id = e.id
  LEFT JOIN gestao_caixa_chegadas ch ON ch.estabelecimento_id = e.id AND ch.motoboy_id = @motoboyId
 WHERE e.id = @loja", new { loja, motoboyId });
            if (x is not { Lat: { } lat, Lon: { } lon }) return;
            // Posição capturada antes da que já decidiu (lote offline, reenvio): não volta o estado.
            if (x.PosicaoEm is { } antes && Utc(antes) >= capturadaEm) return;
            var raio = int.TryParse(x.Raio, out var r) && r > 0 ? r : ReturnToStoreRules.DefaultRadiusMeters;
            var distancia = OrderCoreRules.DistanceKm(latitude, longitude, lat, lon) * 1000d;
            // Presença de outro expediente não segura a histerese nem impede gravar a de hoje.
            var anterior = x.AtualizadoEm is { } em && Utc(em) >= _relogio.GetUtcNow() - ChegadaRegras.Validade ? x.Estado : null;
            var novo = ChegadaRegras.Classificar(distancia, precisaoM, raio, anterior);
            if (novo is null || novo == anterior) return;
            await Gravar(c, loja, motoboyId, novo, ChegadaRegras.OrigemGps, capturadaEm, (int)Math.Round(distancia), precisaoM, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "[caixa.chegada] ev=erro_posicao motoboy={Motoboy} loja={Loja}", motoboyId, loja);
        }
    }

    public async Task AoChegarManual(Guid loja, int motoboyId, CancellationToken ct)
    {
        try
        {
            await using var c = await dataSource.OpenConnectionAsync(ct);
            if (!await TabelaPronta(c)) return;
            await Gravar(c, loja, motoboyId, ChegadaRegras.Loja, ChegadaRegras.OrigemManual, null, null, null, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "[caixa.chegada] ev=erro_manual motoboy={Motoboy} loja={Loja}", motoboyId, loja);
        }
    }

    /// <summary>
    /// Grava a transição. A condição do UPSERT repete a regra de ordem (outra posição pode ter gravado no meio) e não reescreve o mesmo
    /// estado: só a linha que mudou de fato gera aviso. O "cheguei" manual mantém a posição que já decidiu.
    /// </summary>
    private async Task Gravar(NpgsqlConnection c, Guid loja, int motoboyId, string estado, string origem, DateTimeOffset? posicaoEm, int? distancia,
        double? precisao, CancellationToken ct)
    {
        await using var tx = await c.BeginTransactionAsync(ct);
        var mudou = await c.ExecuteAsync(@"
INSERT INTO gestao_caixa_chegadas (estabelecimento_id, motoboy_id, estado, origem, desde, posicao_em, distancia_m, precisao_m, atualizado_em)
VALUES (@loja, @motoboyId, @estado, @origem, @agora, @posicaoEm, @distancia, @precisao, @agora)
ON CONFLICT (estabelecimento_id, motoboy_id) DO UPDATE SET
       estado = EXCLUDED.estado, origem = EXCLUDED.origem, desde = EXCLUDED.desde,
       posicao_em = coalesce(EXCLUDED.posicao_em, gestao_caixa_chegadas.posicao_em),
       distancia_m = coalesce(EXCLUDED.distancia_m, gestao_caixa_chegadas.distancia_m),
       precisao_m = coalesce(EXCLUDED.precisao_m, gestao_caixa_chegadas.precisao_m), atualizado_em = EXCLUDED.atualizado_em
 WHERE (gestao_caixa_chegadas.estado <> EXCLUDED.estado OR gestao_caixa_chegadas.atualizado_em < @validade)
   AND (EXCLUDED.posicao_em IS NULL OR gestao_caixa_chegadas.posicao_em IS NULL OR gestao_caixa_chegadas.posicao_em < EXCLUDED.posicao_em)",
            new { loja, motoboyId, estado, origem, agora = _relogio.GetUtcNow(), posicaoEm, distancia, precisao,
                validade = _relogio.GetUtcNow() - ChegadaRegras.Validade }, tx);
        if (mudou > 0) await GestaoTempoReal.AvisarNoCommit(c, tx, loja, Guid.NewGuid(), GestaoTempoReal.Caixa);
        await tx.CommitAsync(ct);
        if (mudou > 0) logger.LogInformation("[caixa.chegada] ev=mudou estado={Estado} origem={Origem} motoboy={Motoboy} loja={Loja}", estado, origem,
            motoboyId, loja);
    }

    public async Task<IReadOnlyDictionary<int, ChegadaDoMotoboy>> Ler(NpgsqlConnection c, Guid loja, int[] motoboys, CancellationToken ct)
    {
        if (motoboys.Length == 0 || !await TabelaPronta(c)) return new Dictionary<int, ChegadaDoMotoboy>();
        // Sinal pela última posição desta loja (qualquer sessão): é ela que diz se o GPS ainda chega, não a hora da transição.
        var linhas = await c.QueryAsync<Linha>(@"
SELECT m.id AS MotoboyId, ch.estado AS Estado, ch.origem AS Origem, ch.desde AS Desde, ch.atualizado_em AS AtualizadoEm,
       (SELECT max(lc.captured_at_utc) FROM motoboy_location_current lc WHERE lc.motoboy_id = m.id AND lc.estabelecimento_id = @loja) AS PosicaoEm
  FROM unnest(@motoboys) AS m(id)
  LEFT JOIN gestao_caixa_chegadas ch ON ch.estabelecimento_id = @loja AND ch.motoboy_id = m.id", new { loja, motoboys });
        var agora = _relogio.GetUtcNow();
        return linhas.ToDictionary(l => l.MotoboyId, l =>
        {
            var posicao = l.PosicaoEm is { } p ? Utc(p) : (DateTimeOffset?)null;
            var sinal = ChegadaRegras.Sinal(posicao, agora, options.Value.LocationFreshnessSeconds);
            var atualizado = l.AtualizadoEm is { } a ? Utc(a) : (DateTimeOffset?)null;
            var estado = ChegadaRegras.Estado(l.Estado, atualizado, sinal, agora);
            var gravadoVale = estado == l.Estado && l.Estado is not null;
            return new ChegadaDoMotoboy(estado, gravadoVale && l.Desde is { } d ? Utc(d) : null, gravadoVale ? l.Origem : null, sinal, posicao);
        });
    }

    private static async Task<bool> TabelaPronta(NpgsqlConnection c)
    {
        if (_tabelaPronta) return true;
        _tabelaPronta = await c.ExecuteScalarAsync<bool>("SELECT to_regclass('gestao_caixa_chegadas') IS NOT NULL");
        return _tabelaPronta;
    }

    private static DateTimeOffset Utc(DateTime d) => new(DateTime.SpecifyKind(d, DateTimeKind.Utc));
}
