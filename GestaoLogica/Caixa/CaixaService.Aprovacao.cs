using System.Text.Json;
using APIBack.Acontecimentos;
using APIBack.Security;
using Dapper;
using Npgsql;

namespace APIBack.GestaoLogica.Caixa;

/// <summary>
/// Fechamento com aprovação do gerente. O operador conta às cegas; se a diferença passar da tolerância da loja, o fechamento fica
/// enviado (gestao_caixa_dias.envio_*) sem nada no livro, e o caixa trava: sem movimento, sem venda em dinheiro na gaveta e sem abrir
/// o dia seguinte. O gerente aprova (o fechamento vai para o livro como sempre, com ele assinando), reconta e aprova (a contagem do
/// operador fica guardada) ou devolve no próprio dia para o operador recontar. Gerente e diferença dentro da tolerância fecham direto.
/// </summary>
public sealed partial class CaixaService
{
    public const string DecisaoAprovar = "aprovar", DecisaoDevolver = "devolver";

    private sealed record EnvioGravado(ContagemCaixa Contagem, decimal Esperado, decimal Contado, decimal Diferenca,
        Dictionary<string, decimal> Maquininha, Dictionary<string, decimal> MaquininhaEsperado, string? Justificativa, decimal Fica, decimal Tolerancia);

    public async Task<ConfigDoCaixa> Config(Guid loja, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        return await LerConfig(c, loja);
    }

    public async Task<ConfigDoCaixa> SalvarConfig(Guid loja, int usuario, SalvarConfigDoCaixaRequest req, CancellationToken ct)
    {
        if (req.ToleranciaFechamento is { } t && (t < 0 || t > 100_000 || decimal.Round(t, 2) != t))
            throw Invalido("Tolerância: zero ou mais, até 2 casas.");
        await using var c = await dataSource.OpenConnectionAsync(ct);
        var atual = await LerConfig(c, loja);
        await c.ExecuteAsync(@"
INSERT INTO gestao_caixa_config (estabelecimento_id, tolerancia_fechamento, operador_ve_saldo, atualizado_por, atualizado_em)
VALUES (@loja, @tolerancia, @veSaldo, @usuario, now())
ON CONFLICT (estabelecimento_id) DO UPDATE SET tolerancia_fechamento = EXCLUDED.tolerancia_fechamento,
       operador_ve_saldo = EXCLUDED.operador_ve_saldo, atualizado_por = EXCLUDED.atualizado_por, atualizado_em = now()", new
        {
            loja, usuario, tolerancia = req.ToleranciaFechamento ?? atual.ToleranciaFechamento, veSaldo = req.OperadorVeSaldo ?? atual.OperadorVeSaldo,
        });
        return await LerConfig(c, loja);
    }

    public async Task<IReadOnlyList<FechamentoAguardando>> Aguardando(Guid loja, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        var dias = await c.QueryAsync<DiaRow>($@"SELECT {ColunasDia} FROM gestao_caixa_dias
 WHERE estabelecimento_id = @loja AND envio_em IS NOT NULL AND fechado_em IS NULL ORDER BY dia", new { loja });
        return dias.Select(d => (Dia: d.Dia, Envio: EnvioDe(d))).Where(x => x.Envio is not null)
            .Select(x => new FechamentoAguardando(DateOnly.FromDateTime(x.Dia), x.Envio!.Em, x.Envio.PorNome, x.Envio.Diferenca, x.Envio.Contado,
                x.Envio.Esperado)).ToList();
    }

    public async Task<CaixaDoDia> DecidirFechamento(Guid loja, int usuario, GestaoPapeis.Acesso acesso, DateOnly dia, DecisaoDoFechamentoRequest req,
        CancellationToken ct)
    {
        if (acesso.Papel != GestaoPapeis.Gerente) throw new GestaoException("SEM_PERMISSAO", "Só o gerente aprova o fechamento.", 403);
        var decisao = req.Decisao?.Trim().ToLowerInvariant();
        if (decisao is not (DecisaoAprovar or DecisaoDevolver)) throw Invalido("Decisão: aprovar ou devolver.");
        await using var c = await dataSource.OpenConnectionAsync(ct);
        var row = await Dia(c, loja, dia) ?? throw new GestaoException("NAO_ENCONTRADO", "Não há caixa neste dia.", 404);
        if (row.FechadoEm is not null) throw new GestaoException("FECHADO", "Este caixa já foi fechado.", 409);
        if (row.EnvioEm is null || row.Envio is null || row.EnvioPor is not { } operador)
            throw new GestaoException("SEM_ENVIO", "Este caixa não tem fechamento esperando aprovação.", 409);
        var envio = JsonSerializer.Deserialize<EnvioGravado>(row.Envio, Json)!;
        var hoje = await Hoje(loja, ct);

        if (decisao == DecisaoDevolver)
        {
            var motivo = req.Motivo?.Trim();
            if (motivo is null || motivo.Length < 3) throw Invalido("Diga ao operador o que recontar (mínimo de 3 letras).");
            // Devolver reabre o caixa para recontar: só no próprio dia. Dia passado o gerente reconta e aprova.
            if (dia != hoje) throw new GestaoException("DIA_PASSADO", "Caixa de outro dia não volta para o operador: reconte e aprove.", 409);
            await using var tx = await c.BeginTransactionAsync(ct);
            await c.ExecuteAsync(@"
UPDATE gestao_caixa_dias SET envio_em = NULL, envio_por = NULL, envio = NULL,
       devolvido_em = now(), devolvido_por = @usuario, devolucao_motivo = @motivo
 WHERE estabelecimento_id = @loja AND dia = @dia AND fechado_em IS NULL", new { loja, dia = row.Dia, usuario, motivo = Cortar(motivo, 300) }, tx);
            await GestaoTempoReal.AvisarNoCommit(c, tx, loja, Guid.NewGuid(), GestaoTempoReal.Caixa);
            await tx.CommitAsync(ct);
            return (await CarregarDia(loja, dia, ct))!;
        }

        // Aprovar: vale a recontagem do gerente quando houver (a do operador continua em gestao_caixa_contagens e no envio).
        var contagem = req.Recontagem ?? envio.Contagem;
        var contado = CaixaRegras.TotalContado(contagem);
        var contas = await ContasDoCaixa(c, loja, null, ct);
        // O caixa ficou travado desde o envio: o saldo da gaveta é o mesmo que o operador conferiu.
        var esperado = await Saldo(loja, contas.Gaveta, ct);
        var diferenca = CaixaRegras.Diferenca(esperado, contado);
        var justificativa = diferenca == 0 ? null : (req.Justificativa?.Trim() is { Length: > 0 } j ? j : envio.Justificativa);
        CaixaRegras.ExigirJustificativa(diferenca, justificativa);
        var (fica, cofre) = CaixaRegras.Repartir(contado, req.Fica ?? envio.Fica);
        if (req.Recontagem is not null)
            await GravarContagem(c, null, loja, dia, usuario, contagem, contado, esperado, "conferencia");
        return await Concluir(c, loja, usuario, acesso, dia, contagem, contado, esperado, envio.Maquininha, envio.MaquininhaEsperado,
            justificativa is null ? null : Cortar(justificativa, 300), fica, cofre, fechadoPor: req.Recontagem is null ? operador : usuario,
            aprovadoPor: usuario, hoje, ct);
    }

    /// <summary>Grava o envio do operador (sem nada no livro) e trava o caixa até o gerente decidir.</summary>
    private async Task Enviar(NpgsqlConnection c, Guid loja, int usuario, DateOnly dia, FecharCaixaRequest req, decimal esperado, decimal contado,
        decimal diferenca, IReadOnlyDictionary<string, decimal> maquininha, IReadOnlyDictionary<string, decimal> maqEsperado, string? justificativa,
        decimal fica, CancellationToken ct)
    {
        var config = await LerConfig(c, loja);
        var envio = new EnvioGravado(req.Contagem, esperado, contado, diferenca, maquininha.ToDictionary(k => k.Key, k => k.Value),
            maqEsperado.ToDictionary(k => k.Key, k => k.Value), justificativa, fica, config.ToleranciaFechamento);
        await using var tx = await c.BeginTransactionAsync(ct);
        await GravarContagem(c, tx, loja, dia, usuario, req.Contagem, contado, esperado, "fechamento");
        var n = await c.ExecuteAsync(@"
UPDATE gestao_caixa_dias SET envio_em = now(), envio_por = @usuario, envio = CAST(@envio AS jsonb)
 WHERE estabelecimento_id = @loja AND dia = @dia AND fechado_em IS NULL AND envio_em IS NULL", new
        {
            loja, dia = dia.ToDateTime(TimeOnly.MinValue), usuario, envio = JsonSerializer.Serialize(envio, Json),
        }, tx);
        if (n == 0) throw new GestaoException("AGUARDANDO_APROVACAO", "O fechamento de hoje já foi enviado ao gerente.", 409);
        await GestaoTempoReal.AvisarNoCommit(c, tx, loja, Guid.NewGuid(), GestaoTempoReal.Caixa);
        await tx.CommitAsync(ct);
    }

    private static async Task<ConfigDoCaixa> LerConfig(NpgsqlConnection c, Guid loja)
    {
        var linha = await c.QuerySingleOrDefaultAsync<(decimal Tolerancia, bool VeSaldo)?>(
            "SELECT tolerancia_fechamento, operador_ve_saldo FROM gestao_caixa_config WHERE estabelecimento_id = @loja", new { loja });
        // Sem configuração: toda diferença vai para o gerente e o operador vê o saldo (como era).
        return linha is { } l ? new ConfigDoCaixa(l.Tolerancia, l.VeSaldo) : new ConfigDoCaixa(0m, true);
    }

    private static EnvioDoFechamento? EnvioDe(DiaRow d)
    {
        if (d.EnvioEm is not { } em || d.EnvioPor is not { } por || d.Envio is null) return null;
        var e = JsonSerializer.Deserialize<EnvioGravado>(d.Envio, Json)!;
        return new EnvioDoFechamento(Utc(em), por, d.EnvioPorNome, e.Contagem, e.Esperado, e.Contado, e.Diferenca, e.Maquininha, e.MaquininhaEsperado,
            e.Justificativa, e.Fica, e.Tolerancia);
    }

    private static DevolucaoDoFechamento? DevolucaoDe(DiaRow d) =>
        d.DevolvidoEm is { } em && d.DevolvidoPor is { } por ? new DevolucaoDoFechamento(Utc(em), por, d.DevolvidoPorNome, d.DevolucaoMotivo ?? "") : null;
}
