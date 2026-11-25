using System.Text.Json;
using APIBack.Assistente;
using APIBack.Atendimento;
using APIBack.Options;
using APIBack.Service;
using APIBack.Social.Nucleo;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;

namespace APIBack.Social.Ia;

/// <summary>Conta em atraso não usa a chave da Zippy (README do Social §2). Interface para os testes trocarem.</summary>
public interface IContaIa
{
    Task<bool> EmAtrasoAsync(Guid loja, CancellationToken ct);
}

public sealed class ContaIaPelaSituacao(ISituacaoComercialService situacoes) : IContaIa
{
    public async Task<bool> EmAtrasoAsync(Guid loja, CancellationToken ct) =>
        (await situacoes.ObterAsync(loja, ct))?.Financeiro is "em_atraso" or "suspensa" or "encerrada";
}

public sealed record ResultadoIa<T>(T Valor, ProvedorEscolhido Provedor, string? Aviso);

public sealed record SaldoDto(int Concedidos, int Comprados, int Usados, int Restam, bool Acima80)
{
    public static SaldoDto De(SaldoCreditos s) => new(s.Concedidos, s.Comprados, s.Usados, s.Restam, s.Acima80);
}

/// <summary>
/// Créditos do mês no banco. O mês e o dia são de calendário em America/Sao_Paulo. Toda função roda dentro da transação de quem
/// chama (<see cref="SocialBanco.NaLoja{T}"/>), para cobrar e registrar junto com o que foi entregue.
/// </summary>
public static class CreditosBanco
{
    private const string Mes = "date_trunc('month', NOW() AT TIME ZONE 'America/Sao_Paulo')::date";

    public static async Task<SaldoCreditos> SaldoAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid loja, int concedidosPadrao,
        CancellationToken ct, bool travar = false)
    {
        await c.ExecuteAsync(new CommandDefinition($"""
            INSERT INTO social_creditos (estabelecimento_id, mes, concedidos) VALUES (@loja, {Mes}, @concedidosPadrao)
            ON CONFLICT (estabelecimento_id, mes) DO NOTHING
            """, new { loja, concedidosPadrao }, tx, cancellationToken: ct));
        return await c.QuerySingleAsync<SaldoCreditos>(new CommandDefinition($"""
            SELECT concedidos AS "Concedidos", comprados AS "Comprados", usados AS "Usados"
              FROM social_creditos WHERE estabelecimento_id = @loja AND mes = {Mes} {(travar ? "FOR UPDATE" : "")}
            """, new { loja }, tx, cancellationToken: ct));
    }

    /// <summary>Desconta com a linha do mês travada. Sem saldo: 402 com o que falta (e nada muda).</summary>
    public static async Task<SaldoCreditos> DebitarAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid loja, int creditos,
        int concedidosPadrao, CancellationToken ct)
    {
        var saldo = await SaldoAsync(c, tx, loja, concedidosPadrao, ct, travar: true);
        if (creditos <= 0) return saldo;
        if (saldo.Restam < creditos) throw IaErros.SemCreditos(creditos, saldo.Restam);
        var novo = saldo with { Usados = saldo.Usados + creditos };
        await c.ExecuteAsync(new CommandDefinition($"""
            UPDATE social_creditos SET usados = usados + @creditos, aviso_80_enviado = aviso_80_enviado OR @acima80
             WHERE estabelecimento_id = @loja AND mes = {Mes}
            """, new { loja, creditos, acima80 = novo.Acima80 }, tx, cancellationToken: ct));
        return novo;
    }

    public static Task RegistrarAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid loja, int? usuario, UsoIa uso, CancellationToken ct) =>
        c.ExecuteAsync(new CommandDefinition("""
            INSERT INTO social_uso_ia (estabelecimento_id, usuario_id, uso, provedor_id, modelo, origem, unidades, unidade, creditos,
                                       custo_estimado, corte_id, detalhe)
            VALUES (@loja, @usuario, @Uso, @Provedor, @Modelo, @Origem, @Unidades, @Unidade, @Creditos, @CustoEstimado, @CorteId,
                    CAST(@detalhe AS jsonb))
            """, new
        {
            loja, usuario, uso.Uso, uso.Provedor, uso.Modelo, uso.Origem, uso.Unidades, uso.Unidade, uso.Creditos,
            uso.CustoEstimado, uso.CorteId, detalhe = JsonSerializer.Serialize(uso.Detalhe ?? new { }),
        }, tx, cancellationToken: ct));

    /// <summary>Créditos gastos hoje na cota da Zippy (teto diário por conta).</summary>
    public static Task<int> CreditosZippyHojeAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid loja, CancellationToken ct) =>
        c.ExecuteScalarAsync<int>(new CommandDefinition("""
            SELECT COALESCE(SUM(creditos), 0)::int FROM social_uso_ia
             WHERE estabelecimento_id = @loja AND origem = 'zippy'
               AND em >= (date_trunc('day', NOW() AT TIME ZONE 'America/Sao_Paulo') AT TIME ZONE 'America/Sao_Paulo')
            """, new { loja }, tx, cancellationToken: ct));
}

/// <summary>
/// O mínimo da camada de IA do Social (README §2 e §9): escolhe provedor e chave por uso, protege a cota da Zippy, segue o
/// <c>ao_falhar</c> quando a chave própria falha (nunca falha calado) e cobra/registra em <c>social_uso_ia</c>.
/// </summary>
public sealed class SocialIa(
    NpgsqlDataSource fonte, IOptions<SocialIaOptions> opcoes, IConfiguration config, ITokenProtector cifra, IContaIa conta,
    LimiteIaZippy limite)
{
    public SocialIaOptions Op => opcoes.Value;

    private sealed class ConfigLinha
    {
        public string Origem { get; set; } = OrigensIa.Zippy;
        public string? ProvedorId { get; set; }
        public long? ChaveId { get; set; }
        public string? Modelo { get; set; }
        public string AoFalhar { get; set; } = AoFalharIa.UsarCota;
    }

    private sealed class ChaveLinha
    {
        public long Id { get; set; }
        public string ProvedorId { get; set; } = "";
        public string ChaveCifrada { get; set; } = "";
        public string? BaseUrl { get; set; }
    }

    private sealed class CatalogoLinha
    {
        public string Id { get; set; } = "";
        public string? BaseUrlPadrao { get; set; }
        public string Modelos { get; set; } = "{}";
        public string Precos { get; set; } = "{}";
        public bool Ativo { get; set; }
    }

    public Task<SaldoCreditos> SaldoAsync(Guid loja, CancellationToken ct) =>
        SocialBanco.NaLoja(fonte, loja, ct, (c, tx) => CreditosBanco.SaldoAsync(c, tx, loja, Op.CreditosMesPadrao, ct));

    /// <summary>Origem que vai valer para o uso agora (para o custo mostrado antes: cota em créditos ou chave própria).</summary>
    public async Task<string> OrigemAsync(Guid loja, string uso, CancellationToken ct) => (await OpcoesAsync(loja, uso, null, ct))[0].Origem;

    /// <summary>
    /// Provedores na ordem em que serão tentados: chave própria (se a loja escolheu) e, se o <c>ao_falhar</c> deixar, a cota da Zippy.
    /// <paramref name="provedorFixo"/>: o pedido só pode ir a esse provedor (a voz clonada mora num provedor só).
    /// </summary>
    public async Task<IReadOnlyList<ProvedorEscolhido>> OpcoesAsync(Guid loja, string uso, string? provedorFixo, CancellationToken ct)
    {
        var (cfg, chave, catalogo) = await SocialBanco.NaLoja(fonte, loja, ct, async (c, tx) =>
        {
            var cfg = await c.QueryFirstOrDefaultAsync<ConfigLinha>(new CommandDefinition("""
                SELECT origem AS "Origem", provedor_id AS "ProvedorId", chave_id AS "ChaveId", modelo AS "Modelo", ao_falhar AS "AoFalhar"
                  FROM social_config_ia WHERE estabelecimento_id = @loja AND uso = @uso
                """, new { loja, uso }, tx, cancellationToken: ct));
            ChaveLinha? chave = null;
            if (cfg?.Origem == OrigensIa.Propria)
            {
                chave = await c.QueryFirstOrDefaultAsync<ChaveLinha>(new CommandDefinition("""
                    SELECT id AS "Id", provedor_id AS "ProvedorId", chave_cifrada AS "ChaveCifrada", base_url AS "BaseUrl"
                      FROM social_chaves
                     WHERE estabelecimento_id = @loja AND (id = @chaveId OR (@chaveId IS NULL AND provedor_id = @provedor))
                     LIMIT 1
                    """, new { loja, chaveId = cfg.ChaveId, provedor = cfg.ProvedorId }, tx, cancellationToken: ct));
            }
            var catalogo = (await c.QueryAsync<CatalogoLinha>(new CommandDefinition("""
                SELECT id AS "Id", base_url_padrao AS "BaseUrlPadrao", modelos::text AS "Modelos", precos::text AS "Precos", ativo AS "Ativo"
                  FROM social_provedores
                """, transaction: tx, cancellationToken: ct))).ToDictionary(l => l.Id);
            return (cfg, chave, catalogo);
        });

        var lista = new List<ProvedorEscolhido>();
        var aoFalhar = cfg?.AoFalhar ?? AoFalharIa.UsarCota;
        if (cfg?.Origem == OrigensIa.Propria)
        {
            var provedor = chave?.ProvedorId ?? cfg.ProvedorId;
            var aberta = chave is null ? null : cifra.Revelar(chave.ChaveCifrada);
            var serve = provedorFixo is null || provedorFixo == provedor;
            if (serve && aberta is not null && provedor is not null && catalogo.TryGetValue(provedor, out var cat) && cat.Ativo)
            {
                lista.Add(new ProvedorEscolhido(provedor, OrigensIa.Propria, aberta, chave!.BaseUrl ?? cat.BaseUrlPadrao, cfg.Modelo,
                    Json(cat.Modelos), Json(cat.Precos), aoFalhar, chave.Id));
            }
            else if (serve && aoFalhar != AoFalharIa.UsarCota)
            {
                // Escolheu a própria chave e "parar" se falhar: sem chave utilizável, avisa em vez de gastar a cota.
                throw IaErros.ChavePropriaFalhou(provedor ?? "provedor", TipoFalhaIa.SemChave);
            }
            if (aoFalhar != AoFalharIa.UsarCota && lista.Count > 0) return lista;
        }

        var provZippy = provedorFixo ?? (cfg?.Origem == OrigensIa.Zippy ? cfg.ProvedorId : null) ?? PadraoDo(uso);
        var modeloZippy = cfg?.Origem == OrigensIa.Zippy ? cfg.Modelo : null;
        var chaveZippy = ChaveZippy(provZippy);
        if (chaveZippy is not null && catalogo.TryGetValue(provZippy, out var catZ) && catZ.Ativo)
        {
            var baseUrl = provZippy == ProvedoresSocial.Azure ? Op.AzureEndpoint : catZ.BaseUrlPadrao;
            if (provZippy != ProvedoresSocial.Azure || !string.IsNullOrWhiteSpace(baseUrl))
                lista.Add(new ProvedorEscolhido(provZippy, OrigensIa.Zippy, chaveZippy, baseUrl, modeloZippy, Json(catZ.Modelos),
                    Json(catZ.Precos), aoFalhar));
        }
        if (lista.Count == 0 && Op.FalsoSemChave)
        {
            var modelos = catalogo.TryGetValue(provZippy, out var catF) ? Json(catF.Modelos) : Json("{}");
            lista.Add(new ProvedorEscolhido(ProvedoresSocial.Falso, OrigensIa.Zippy, null, null, null, modelos, Json("{}"), aoFalhar));
        }
        if (lista.Count == 0) throw IaErros.SemProvedor(uso);
        return lista;
    }

    /// <summary>
    /// Chama o provedor seguindo a ordem de <see cref="OpcoesAsync"/>. Antes de usar a cota da Zippy confere conta em atraso, limite por
    /// minuto, saldo para <paramref name="creditosPrevistos"/> e teto diário. Chave própria que falha fica marcada e, com
    /// <c>usar_cota</c>, o pedido vai para a cota com um aviso na resposta.
    /// </summary>
    public async Task<ResultadoIa<T>> ExecutarAsync<T>(Guid loja, string uso, int creditosPrevistos,
        Func<ProvedorEscolhido, CancellationToken, Task<T>> chamar, CancellationToken ct, string? provedorFixo = null)
    {
        var opcoes = await OpcoesAsync(loja, uso, provedorFixo, ct);
        string? aviso = null;
        for (var i = 0; i < opcoes.Count; i++)
        {
            var p = opcoes[i];
            if (p.Origem == OrigensIa.Zippy) await ConferirCotaAsync(loja, creditosPrevistos, ct);
            try
            {
                return new ResultadoIa<T>(await chamar(p, ct), p, aviso);
            }
            catch (FalhaIaException f) when (p.Origem == OrigensIa.Propria)
            {
                await MarcarChaveAsync(loja, p.ChaveId, f.Tipo, ct);
                if (!f.TrocarResolve) throw IaErros.ProvedorFalhou(p.ProvedorId, f.Tipo);
                if (i == opcoes.Count - 1) throw IaErros.ChavePropriaFalhou(p.ProvedorId, f.Tipo);
                aviso = $"Sua chave do {IaErros.NomeProvedor(p.ProvedorId)} falhou ({FalhaIaException.MensagemDe(f.Tipo)}). " +
                        "Desta vez usamos a cota da Zippy.";
            }
            catch (FalhaIaException f)
            {
                throw IaErros.ProvedorFalhou(p.ProvedorId, f.Tipo);
            }
        }
        throw IaErros.SemProvedor(uso);
    }

    /// <summary>Cobra (só na cota da Zippy) e registra o uso. Chamar dentro da transação que grava o resultado.</summary>
    public async Task<SaldoCreditos> CobrarAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid loja, int? usuario, ProvedorEscolhido p,
        UsoIa uso, CancellationToken ct)
    {
        var creditos = p.Origem == OrigensIa.Zippy ? uso.Creditos : 0;
        var saldo = await CreditosBanco.DebitarAsync(c, tx, loja, creditos, Op.CreditosMesPadrao, ct);
        await CreditosBanco.RegistrarAsync(c, tx, loja, usuario, uso with { Origem = p.Origem, Creditos = creditos, Provedor = p.ProvedorId }, ct);
        return saldo;
    }

    private async Task ConferirCotaAsync(Guid loja, int creditosPrevistos, CancellationToken ct)
    {
        if (await conta.EmAtrasoAsync(loja, ct)) throw IaErros.ContaEmAtraso();
        if (!limite.Tentar(loja, Op.LimitePorMinuto)) throw IaErros.LimiteMinuto();
        if (creditosPrevistos <= 0) return;
        await SocialBanco.NaLoja(fonte, loja, ct, async (c, tx) =>
        {
            var saldo = await CreditosBanco.SaldoAsync(c, tx, loja, Op.CreditosMesPadrao, ct);
            if (saldo.Restam < creditosPrevistos) throw IaErros.SemCreditos(creditosPrevistos, saldo.Restam);
            var hoje = await CreditosBanco.CreditosZippyHojeAsync(c, tx, loja, ct);
            if (Op.TetoDiarioCreditos > 0 && hoje + creditosPrevistos > Op.TetoDiarioCreditos) throw IaErros.TetoDiario(Op.TetoDiarioCreditos);
            return 0;
        });
    }

    private Task MarcarChaveAsync(Guid loja, long? chaveId, TipoFalhaIa tipo, CancellationToken ct)
    {
        var status = tipo switch { TipoFalhaIa.ChaveRecusada => "recusada", TipoFalhaIa.SemSaldo => "sem_saldo", _ => null };
        if (chaveId is null || status is null) return Task.CompletedTask;
        return SocialBanco.NaLoja(fonte, loja, ct, (c, tx) => c.ExecuteAsync(new CommandDefinition("""
            UPDATE social_chaves SET status = @status, testada_em = NOW() WHERE id = @chaveId AND estabelecimento_id = @loja
            """, new { status, chaveId, loja }, tx, cancellationToken: ct)));
    }

    private string PadraoDo(string uso) => uso switch
    {
        UsosIa.Voz => Op.ProvedorVoz,
        UsosIa.VozClone => Op.ProvedorVozClone,
        _ => Op.ProvedorTexto,
    };

    private string? ChaveZippy(string provedor)
    {
        if (Op.Chaves.TryGetValue(provedor, out var k) && !string.IsNullOrWhiteSpace(k)) return k;
        return provedor switch
        {
            ProvedoresSocial.Anthropic => AssistenteService.ChaveAnthropic(config),
            ProvedoresSocial.OpenAI => AssistenteService.ChaveOpenAI(config),
            _ => null,
        };
    }

    private static JsonElement Json(string texto)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(texto) ? "{}" : texto);
        return doc.RootElement.Clone();
    }
}
