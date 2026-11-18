using System.Text.Json.Nodes;
using APIBack.Mesas.Nucleo;
using APIBack.Mesas.Sessao;
using Dapper;
using Npgsql;
using R = APIBack.Mesas.Pedidos.PedidosRegras;

namespace APIBack.Mesas.Pedidos;

/// <summary>O que o sistema faz sozinho (papel S): escalar aprovação parada, fechar entrega presumida depois do "Peguei" e avisar atraso.</summary>
public sealed record TiqueResultado(int Escalados, int Presumidos, int Atrasados);

public sealed class PedidosSistema(NpgsqlDataSource source, IMesasTempoReal tempoReal, ICozinhaTempoReal cozinhaRt)
{
    public async Task<TiqueResultado> TicarAsync(CancellationToken ct)
    {
        IReadOnlyList<Guid> lojas;
        await using (var c = await source.OpenConnectionAsync(ct))
            lojas = (await c.QueryAsync<Guid>(new CommandDefinition("""
                SELECT estabelecimento_id FROM mesa_pedido WHERE estado = 'aguardando_aprovacao' AND escalado_em IS NULL
                UNION SELECT estabelecimento_id FROM mesa_item WHERE estado = 'pronto' AND pegou_em IS NOT NULL
                UNION SELECT estabelecimento_id FROM mesa_item WHERE estado IN ('em_fila', 'em_preparo') AND atraso_avisado_em IS NULL
                """, cancellationToken: ct))).ToList();
        var total = new TiqueResultado(0, 0, 0);
        foreach (var loja in lojas)
        {
            var r = await LojaAsync(loja, ct);
            total = new(total.Escalados + r.Escalados, total.Presumidos + r.Presumidos, total.Atrasados + r.Atrasados);
        }
        return total;
    }

    public async Task<TiqueResultado> LojaAsync(Guid loja, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        var agora = await c.ExecuteScalarAsync<DateTime>(new CommandDefinition("SELECT NOW()", transaction: tx, cancellationToken: ct));
        var op = new MesasOperacao(c, tx, new MesasContexto(loja, 0, "sistema"), "Sistema", Guid.NewGuid(), "sistema", agora, ct);
        var cfg = await op.Config();
        var esc = await Escalar(op, cfg);
        var pres = await Presumir(op, cfg);
        var atr = await Atrasos(op, cfg);
        await tx.CommitAsync(ct);
        foreach (var aviso in op.Avisos) await aviso(tempoReal);
        return new TiqueResultado(esc, pres, atr);
    }

    /// <summary>Aprovação parada há mais de <c>aprovacao.escalonarMin</c> (0 desliga) sobe para <c>escalonarPara</c> ou o gerente de salão com ponto.</summary>
    private static async Task<int> Escalar(MesasOperacao op, JsonObject cfg)
    {
        var min = await op.ConfigInt("aprovacao.escalonarMin", 4);
        if (min <= 0) return 0;
        var pedidos = (await op.C.QueryAsync<(Guid Id, Guid MesaId, Guid SessaoId)>(op.Sql("""
            SELECT id, mesa_id, sessao_id FROM mesa_pedido
             WHERE estabelecimento_id = @loja AND estado = 'aguardando_aprovacao' AND escalado_em IS NULL AND criado_em < @agora - make_interval(mins => @min)
             ORDER BY id FOR UPDATE SKIP LOCKED
            """, new { loja = op.Ctx.Loja, agora = op.Agora, min }))).ToList();
        if (pedidos.Count == 0) return 0;
        var para = await PedidosService.QuemAprova(op);
        var nome = para is Guid p ? await op.C.QueryFirstOrDefaultAsync<string?>(op.Sql("SELECT nome::text FROM gestao_pessoas WHERE id = @p", new { p })) : null;
        var loja = op.Ctx.Loja;
        foreach (var x in pedidos)
        {
            await op.C.ExecuteAsync(op.Sql("UPDATE mesa_pedido SET escalado_para = @para, escalado_em = @agora, atualizado_em = @agora WHERE id = @id",
                new { para, agora = op.Agora, id = x.Id }));
            await op.Evento("escalarAprovacao", "pedido", x.Id, "Pedido parado esperando aprovação",
                depois: "Encaminhado para " + (nome is null ? "o gerente" : PedidosService.Primeiro(nome)), mesaId: x.MesaId, sessaoId: x.SessaoId);
            op.Avisar(t => t.Aviso(loja, "pedido", x.Id, x.MesaId, para));
        }
        op.Avisar(t => t.PendenciasMudou(loja, "aprovacao", para));
        return pedidos.Count;
    }

    /// <summary>"Peguei" sem "Entreguei" há mais de <c>entrega.minutos</c>: fecha como entrega presumida (se <c>entrega.fechaSozinho</c>).</summary>
    private async Task<int> Presumir(MesasOperacao op, JsonObject cfg)
    {
        if (MesasConfigRegras.Ler(cfg, "entrega.fechaSozinho") is JsonValue v && v.TryGetValue<bool>(out var liga) && !liga) return 0;
        var min = await op.ConfigInt("entrega.minutos", 20);
        var itens = (await op.C.QueryAsync<(Guid Id, Guid SessaoId, Guid MesaId, string Nome, decimal Qtd)>(op.Sql("""
            SELECT id, sessao_id, mesa_id, nome, qtd FROM mesa_item
             WHERE estabelecimento_id = @loja AND estado = 'pronto' AND pegou_em IS NOT NULL AND pegou_em < @agora - make_interval(mins => @min)
            """, new { loja = op.Ctx.Loja, agora = op.Agora, min }))).ToList();
        var feitos = 0;
        foreach (var s in itens.GroupBy(i => i.SessaoId).OrderBy(g => g.Key))
        {
            var mesas = await op.TravarSessao(s.Key);
            var estacoes = await PedidosService.FecharPresumidas(op.C, op.Tx, op.Ctx.Loja, null, s.Select(i => i.Id).ToList(), "entregaPresumida", op.Ct, cfg);
            if (estacoes.Count == 0) continue;
            feitos += s.Count();
            await op.TocarTodas(mesas);
            var primeiro = s.First();
            await op.Evento("entregar", "item", primeiro.Id, string.Join(", ", s.Select(i => R.Qtd(i.Qtd) + "× " + i.Nome)),
                depois: "Entrega presumida", mesaId: primeiro.MesaId, sessaoId: s.Key);
            var loja = op.Ctx.Loja;
            op.Avisar(_ => cozinhaRt.CozinhaMudou(loja, estacoes, null));
        }
        return feitos;
    }

    /// <summary>Item na produção acima de 1,5× o tempo do prato vira atrasado: avisa uma vez por passo (garçom e cozinha relêem).</summary>
    private async Task<int> Atrasos(MesasOperacao op, JsonObject cfg)
    {
        var itens = (await op.C.QueryAsync<(Guid Id, string EstacaoId, DateTime Desde, Guid? Garcom)>(op.Sql("""
            SELECT i.id, i.estacao_id, COALESCE(i.t_fila, i.criado_em) AS desde, COALESCE(i.leva_garcom_id, s.garcom_id) AS garcom
              FROM mesa_item i JOIN mesa_sessao s ON s.id = i.sessao_id
             WHERE i.estabelecimento_id = @loja AND i.estado IN ('em_fila', 'em_preparo') AND i.atraso_avisado_em IS NULL
            """, new { loja = op.Ctx.Loja }))).ToList();
        var atrasados = itens.Where(i => R.Atrasado((int)(op.Agora - i.Desde).TotalMinutes, R.TempoEsperado(cfg, i.EstacaoId))).ToList();
        if (atrasados.Count == 0) return 0;
        await op.C.ExecuteAsync(op.Sql("UPDATE mesa_item SET atraso_avisado_em = @agora WHERE id = ANY(@ids) AND atraso_avisado_em IS NULL",
            new { agora = op.Agora, ids = atrasados.Select(i => i.Id).ToArray() }));
        var loja = op.Ctx.Loja;
        foreach (var g in atrasados.Select(i => i.Garcom).Distinct()) op.Avisar(t => t.PendenciasMudou(loja, "atraso", g));
        var est = atrasados.Select(i => i.EstacaoId).Distinct().ToArray();
        op.Avisar(_ => cozinhaRt.CozinhaMudou(loja, est, null));
        return atrasados.Count;
    }
}

/// <summary>Roda o <see cref="PedidosSistema"/> a cada 30 s.</summary>
public sealed class PedidosWorker(IServiceScopeFactory scopes, ILogger<PedidosWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<PedidosSistema>().TicarAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogWarning(ex, "Falha no ciclo dos pedidos do Mesas"); }
        }
    }
}
