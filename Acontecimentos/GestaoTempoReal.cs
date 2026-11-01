using System.Text.Json;
using System.Threading.Channels;
using APIBack.Hubs;
using Dapper;
using Microsoft.AspNetCore.SignalR;
using Npgsql;

namespace APIBack.Acontecimentos;

/// <summary>
/// Tempo real da Gestão (6C4): só invalidação. O Painel recebe { lojaId, recurso, id, versao } e relê pela rota de sempre; o aviso
/// nunca carrega valor, nunca sai antes do commit e vai para um grupo por loja e por área (financeiro, caixa, estoque), que o
/// DeliveryHub só dá a quem tem acesso àquela área (GestaoPapeis).
/// <para>Transação própria do livro: o serviço chama os assinantes depois do commit, em processo. Transação de outro módulo
/// (venda na conclusão do pedido, caixa, estoque, equipe): o livro faz <c>pg_notify</c> dentro dela; o PostgreSQL só entrega
/// se ela fizer commit (rollback descarta) e o <see cref="GestaoTempoRealOuvinte"/> repassa aos mesmos assinantes.</para>
/// </summary>
public static class GestaoTempoReal
{
    public const string Evento = "gestao.invalidar";
    public const string Canal = "gestao_tempo_real";
    public const string Financeiro = "financeiro", Caixa = "caixa", Estoque = "estoque";
    public static readonly string[] Recursos = { Financeiro, Caixa, Estoque };

    public static string Grupo(Guid loja, string recurso) => $"gestao:{loja:N}:{recurso}";

    /// <summary>Que telas um acontecimento muda. Sobrar invalidação só custa uma releitura; faltar deixa a tela velha.</summary>
    public static IReadOnlyList<string> RecursosDe(AcontecimentoDto a)
    {
        var dinheiro = a.Lancamentos.Count > 0 || a.Liquidacoes.Count > 0;
        var r = new List<string>(3);
        if (dinheiro || a.Modulo is ModuloGestao.Financeiro or ModuloGestao.Despesas or ModuloGestao.Compras or ModuloGestao.Equipe)
            r.Add(Financeiro);
        if (dinheiro || a.Modulo is ModuloGestao.Caixa or ModuloGestao.Pedidos) r.Add(Caixa);
        if (a.Movimentos.Count > 0 || a.Modulo is ModuloGestao.Estoque or ModuloGestao.Compras) r.Add(Estoque);
        return r;
    }

    internal sealed record Aviso(Guid L, Guid Id, string[]? R);

    /// <summary>Acontecimento gravado na transação de outro módulo: sai só no commit dela.</summary>
    public static Task AvisarAcontecimentoNoCommit(NpgsqlConnection c, NpgsqlTransaction tx, Guid loja, Guid acontecimentoId) =>
        Notificar(c, tx, new Aviso(loja, acontecimentoId, null));

    /// <summary>Mudança fora do livro (ex.: pedido de correção do caixa): sai só no commit, já com as áreas.</summary>
    public static Task AvisarNoCommit(NpgsqlConnection c, NpgsqlTransaction tx, Guid loja, Guid id, params string[] recursos) =>
        Notificar(c, tx, new Aviso(loja, id, recursos));

    private static Task Notificar(NpgsqlConnection c, NpgsqlTransaction tx, Aviso aviso) =>
        c.ExecuteAsync("SELECT pg_notify(@canal, @payload);", new { canal = Canal, payload = JsonSerializer.Serialize(aviso) }, tx);

    internal static Aviso? Ler(string payload)
    {
        try { return JsonSerializer.Deserialize<Aviso>(payload) is { L: var l, Id: var id } a && l != Guid.Empty && id != Guid.Empty ? a : null; }
        catch (JsonException) { return null; }
    }
}

public interface IGestaoTempoRealPublisher
{
    Task Invalidar(Guid loja, IReadOnlyList<string> recursos, Guid id, long versao, CancellationToken ct);
}

/// <summary>Melhor esforço, como o ChatRealtimePublisher: falha aqui nunca desfaz nada; a tela relê ao reconectar.</summary>
public sealed class GestaoTempoRealPublisher(IHubContext<DeliveryHub> hub, ILogger<GestaoTempoRealPublisher> logger) : IGestaoTempoRealPublisher
{
    public async Task Invalidar(Guid loja, IReadOnlyList<string> recursos, Guid id, long versao, CancellationToken ct)
    {
        foreach (var recurso in recursos.Distinct())
        {
            if (!GestaoTempoReal.Recursos.Contains(recurso)) continue;
            try
            {
                await hub.Clients.Group(GestaoTempoReal.Grupo(loja, recurso))
                    .SendAsync(GestaoTempoReal.Evento, new { lojaId = loja, recurso, id, versao }, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "[gestao.rt] ev=erro recurso={Recurso} id={Id} loja={Loja}", recurso, id, loja);
            }
        }
    }
}

/// <summary>Assinante do livro que empurra a invalidação para o Painel.</summary>
public sealed class GestaoTempoRealAssinante(IGestaoTempoRealPublisher publisher) : IAcontecimentoAssinante
{
    public Task AoRegistrar(AcontecimentoRegistrado evento, CancellationToken ct)
    {
        var recursos = GestaoTempoReal.RecursosDe(evento.Acontecimento);
        return recursos.Count == 0 ? Task.CompletedTask
            : publisher.Invalidar(evento.EstabelecimentoId, recursos, evento.Acontecimento.Id,
                evento.Acontecimento.RegistradoEm.ToUnixTimeMilliseconds(), ct);
    }
}

/// <summary>
/// Escuta o canal <see cref="GestaoTempoReal.Canal"/> numa conexão própria e repassa cada aviso já confirmado pelo commit.
/// Acontecimento: relê do livro (pela loja do aviso) e chama os assinantes. Outro aviso: vai direto ao Painel.
/// Se a conexão cair, reconecta; o que for gravado nesse intervalo não é avisado (a tela relê ao reconectar o SignalR).
/// </summary>
public sealed class GestaoTempoRealOuvinte(NpgsqlDataSource dataSource, IServiceScopeFactory scopes, ILogger<GestaoTempoRealOuvinte> logger)
    : BackgroundService
{
    private readonly Channel<string> _fila = Channel.CreateBounded<string>(new BoundedChannelOptions(2000)
    { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(Escutar(stoppingToken), Repassar(stoppingToken));

    private async Task Escutar(CancellationToken ct)
    {
        var espera = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var c = await dataSource.OpenConnectionAsync(ct);
                c.Notification += (_, e) => _fila.Writer.TryWrite(e.Payload);
                await c.ExecuteAsync($"LISTEN {GestaoTempoReal.Canal};");
                logger.LogInformation("[gestao.rt] ev=escutando");
                espera = TimeSpan.FromSeconds(1);
                while (!ct.IsCancellationRequested)
                {
                    // Sem aviso em 30 s: um SELECT 1 descobre conexão morta (proxy que derruba ociosa).
                    if (!await c.WaitAsync(TimeSpan.FromSeconds(30), ct)) await c.ExecuteAsync("SELECT 1;");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[gestao.rt] ev=conexao_caiu proxima_em={Espera}s", espera.TotalSeconds);
                try { await Task.Delay(espera, ct); } catch (OperationCanceledException) { break; }
                espera = TimeSpan.FromSeconds(Math.Min(espera.TotalSeconds * 2, 30));
            }
        }
    }

    private async Task Repassar(CancellationToken ct)
    {
        try
        {
            await foreach (var payload in _fila.Reader.ReadAllAsync(ct))
            {
                try { await Processar(payload, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "[gestao.rt] ev=erro_repasse");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    internal async Task Processar(string payload, CancellationToken ct)
    {
        if (GestaoTempoReal.Ler(payload) is not { } aviso) return;
        using var scope = scopes.CreateScope();
        var sp = scope.ServiceProvider;
        if (aviso.R is { } recursos)
        {
            await sp.GetRequiredService<IGestaoTempoRealPublisher>().Invalidar(aviso.L, recursos, aviso.Id,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ct);
            return;
        }
        // Relido pela loja do aviso: um id de outra loja não é encontrado e não vira aviso.
        if (await sp.GetRequiredService<IAcontecimentosService>().Obter(aviso.L, aviso.Id, ct) is not { } dto) return;
        foreach (var assinante in sp.GetServices<IAcontecimentoAssinante>())
        {
            try { await assinante.AoRegistrar(new AcontecimentoRegistrado(aviso.L, dto), ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Assinante {Assinante} falhou no acontecimento {Id}.", assinante.GetType().Name, dto.Id);
            }
        }
    }
}
