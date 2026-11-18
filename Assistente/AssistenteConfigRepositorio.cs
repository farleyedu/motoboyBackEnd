using System.Text.Json;
using Dapper;
using Npgsql;

namespace APIBack.Assistente;

public interface IAssistenteConfigRepositorio
{
    /// <summary>Configuração salva, ou <see cref="ConfigIA.Padrao"/> se nunca foi salva (ou a tabela ainda não existe).</summary>
    Task<ConfigIASalva> Obter(CancellationToken ct);
    Task Salvar(ConfigIA config, int usuario, CancellationToken ct);
}

/// <summary>
/// Uma linha só (id = 1) em <c>assistente_config</c> e uma linha de histórico por salvamento. A cadeia, os provedores, o modo e a
/// política de pagos ficam nas colunas da migração 20261205_07; linha antiga sem cadeia vira cadeia de 1 ou 2 itens ao ler.
/// </summary>
public sealed class AssistenteConfigRepositorio(NpgsqlDataSource source, ILogger<AssistenteConfigRepositorio> logger) : IAssistenteConfigRepositorio
{
    private sealed class Linha
    {
        public bool Ligado { get; set; }
        public string? Provedor { get; set; }
        public string? Modelo { get; set; }
        public string Esforco { get; set; } = "";
        public int MaxTokens { get; set; }
        public int LimiteDiario { get; set; }
        public string? ReservaProvedor { get; set; }
        public string? ReservaModelo { get; set; }
        public string? Precos { get; set; }
        public string? Cadeia { get; set; }
        public string? Provedores { get; set; }
        public string? Modo { get; set; }
        public string? Pagos { get; set; }
        public string? AtualizadoPor { get; set; }
        public DateTime AtualizadoEm { get; set; }
    }

    public async Task<ConfigIASalva> Obter(CancellationToken ct)
    {
        try
        {
            await using var c = await source.OpenConnectionAsync(ct);
            var l = await c.QuerySingleOrDefaultAsync<Linha>(new CommandDefinition(
                """
                SELECT a.ligado, a.provedor, a.modelo, a.esforco, a.max_tokens AS MaxTokens, a.limite_diario AS LimiteDiario,
                       a.reserva_provedor AS ReservaProvedor, a.reserva_modelo AS ReservaModelo, a.precos::text AS Precos,
                       a.cadeia::text AS Cadeia, a.provedores::text AS Provedores, a.modo, a.pagos,
                       COALESCE(u.nome, u.email, a.atualizado_por::text) AS AtualizadoPor, a.atualizado_em AS AtualizadoEm
                FROM assistente_config a LEFT JOIN usuario u ON u.id = a.atualizado_por
                WHERE a.id = 1
                """, cancellationToken: ct));
            if (l is null) return new ConfigIASalva(ConfigIA.Padrao, true, null, null);
            return new ConfigIASalva(DaLinha(l), false, l.AtualizadoPor, new DateTimeOffset(DateTime.SpecifyKind(l.AtualizadoEm, DateTimeKind.Utc)));
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.UndefinedColumn)
        {
            logger.LogWarning("Assistente: configuração da IA sem as migrações 20261205_03/05/07; usando o padrão.");
            return new ConfigIASalva(ConfigIA.Padrao, true, null, null);
        }
    }

    private static ConfigIA DaLinha(Linha l)
    {
        T? Ler<T>(string? json) where T : class => json is null ? null : JsonSerializer.Deserialize<T>(json, JsonAssistente.Opcoes);
        var cfg = new ConfigIA
        {
            Ligado = l.Ligado, Esforco = l.Esforco, MaxTokens = l.MaxTokens, LimiteDiario = l.LimiteDiario,
            Modo = l.Modo ?? ModosCadeia.Ordem, Pagos = l.Pagos ?? PoliticasPagos.PagoSoNoFim,
            Definicoes = Ler<List<DefinicaoProvedor>>(l.Provedores) ?? ConfigIA.ProvedoresPadrao,
            Cadeia = Ler<List<ItemCadeia>>(l.Cadeia) ?? ConfigIA.CadeiaPadrao,
            Precos = Ler<List<PrecoModelo>>(l.Precos) ?? ConfigIA.PrecosPadrao,
        };
        // Linha salva antes da cadeia: principal + reserva viram cadeia.
        if (l.Cadeia is null && !string.IsNullOrWhiteSpace(l.Provedor) && !string.IsNullOrWhiteSpace(l.Modelo))
            cfg = (cfg with { Provedor = l.Provedor, Modelo = l.Modelo, ReservaProvedor = l.ReservaProvedor, ReservaModelo = l.ReservaModelo }).Normalizada();
        return cfg;
    }

    public async Task Salvar(ConfigIA cfg, int usuario, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        var primeiro = cfg.Cadeia[0];
        var p = new
        {
            cfg.Ligado, primeiro.Provedor, primeiro.Modelo, cfg.Esforco, cfg.MaxTokens, cfg.LimiteDiario, cfg.Modo, cfg.Pagos, usuario,
            json = JsonSerializer.Serialize(cfg, JsonAssistente.Opcoes),
            precos = JsonSerializer.Serialize(cfg.Precos, JsonAssistente.Opcoes),
            cadeia = JsonSerializer.Serialize(cfg.Cadeia, JsonAssistente.Opcoes),
            provedores = JsonSerializer.Serialize(cfg.Definicoes, JsonAssistente.Opcoes),
        };
        await c.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO assistente_config (id, ligado, provedor, modelo, esforco, max_tokens, limite_diario, reserva_provedor, reserva_modelo,
                                           precos, cadeia, provedores, modo, pagos, atualizado_por, atualizado_em)
            VALUES (1, @Ligado, @Provedor, @Modelo, @Esforco, @MaxTokens, @LimiteDiario, NULL, NULL,
                    @precos::jsonb, @cadeia::jsonb, @provedores::jsonb, @Modo, @Pagos, @usuario, NOW())
            ON CONFLICT (id) DO UPDATE SET
                ligado = EXCLUDED.ligado, provedor = EXCLUDED.provedor, modelo = EXCLUDED.modelo, esforco = EXCLUDED.esforco,
                max_tokens = EXCLUDED.max_tokens, limite_diario = EXCLUDED.limite_diario, reserva_provedor = NULL, reserva_modelo = NULL,
                precos = EXCLUDED.precos, cadeia = EXCLUDED.cadeia, provedores = EXCLUDED.provedores, modo = EXCLUDED.modo, pagos = EXCLUDED.pagos,
                atualizado_por = EXCLUDED.atualizado_por, atualizado_em = NOW();
            INSERT INTO assistente_config_historico (config, alterado_por) VALUES (@json::jsonb, @usuario);
            """, p, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }
}
