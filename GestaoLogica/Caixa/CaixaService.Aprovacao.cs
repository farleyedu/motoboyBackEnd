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
        TimeSpan? virada = null;
        if (req.ViradaDoDia is { } v)
        {
            if (!TimeSpan.TryParse(v, out var tv) || tv < TimeSpan.Zero || tv > TimeSpan.FromHours(8) || tv.Seconds != 0)
                throw Invalido("Virada do dia: um horário entre 00:00 e 08:00.");
            virada = tv;
        }
        await using var c = await dataSource.OpenConnectionAsync(ct);
        var atual = await LerConfig(c, loja);
        await c.ExecuteAsync(@"
INSERT INTO gestao_caixa_config (estabelecimento_id, tolerancia_fechamento, operador_ve_saldo, virada_do_dia, atualizado_por, atualizado_em)
VALUES (@loja, @tolerancia, @veSaldo, @virada, @usuario, now())
ON CONFLICT (estabelecimento_id) DO UPDATE SET tolerancia_fechamento = EXCLUDED.tolerancia_fechamento,
       operador_ve_saldo = EXCLUDED.operador_ve_saldo, virada_do_dia = EXCLUDED.virada_do_dia, atualizado_por = EXCLUDED.atualizado_por,
       atualizado_em = now()", new
        {
            loja, usuario, tolerancia = req.ToleranciaFechamento ?? atual.ToleranciaFechamento, veSaldo = req.OperadorVeSaldo ?? atual.OperadorVeSaldo,
            virada = virada ?? atual.Virada,
        });
        return await LerConfig(c, loja);
    }

    /// <summary>Fechamentos esperando o gerente: os enviados (nada no livro) e os que o operador fechou e falta aprovar.</summary>
    public async Task<IReadOnlyList<FechamentoAguardando>> Aguardando(Guid loja, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        return await Pendentes(c, loja);
    }

    private static async Task<IReadOnlyList<FechamentoAguardando>> Pendentes(NpgsqlConnection c, Guid loja)
    {
        var dias = await c.QueryAsync<DiaRow>($@"SELECT {ColunasDia} FROM gestao_caixa_dias
 WHERE estabelecimento_id = @loja AND ((envio_em IS NOT NULL AND fechado_em IS NULL) OR (fechado_em IS NOT NULL AND aprovado_em IS NULL))
 ORDER BY dia", new { loja });
        return dias.Select(d =>
        {
            if (d.FechadoEm is { } fechado)
                return new FechamentoAguardando(DateOnly.FromDateTime(d.Dia), Utc(fechado), d.FechadoPorNome, CaixaRegras.Diferenca(d.Esperado!.Value, d.Contado!.Value),
                    d.Contado!.Value, d.Esperado!.Value, Fechado: true);
            var e = EnvioDe(d);
            return e is null ? null : new FechamentoAguardando(DateOnly.FromDateTime(d.Dia), e.Em, e.PorNome, e.Diferenca, e.Contado, e.Esperado);
        }).OfType<FechamentoAguardando>().ToList();
    }

    /// <summary>O que o painel precisa para saber que dia o caixa está rodando e o que impede abrir outro.</summary>
    public async Task<OperacaoDoCaixa> Operacao(Guid loja, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        var dia = await DiaEmOperacao(c, loja, ct);
        var row = await Dia(c, loja, dia);
        var situacao = row is null ? "sem_caixa" : row.FechadoEm is not null ? "fechado" : row.EnvioEm is not null ? "aguardando" : "aberto";
        var config = await LerConfig(c, loja);
        var estado = await EstadoDaLoja(loja, ct);
        var aberta = estado is null || estado.AbertoAgora;
        return new OperacaoDoCaixa(dia, await HojeDoCalendario(loja, ct), config.ViradaDoDia, situacao, row?.Turno ?? 1, await Pendentes(c, loja),
            aberta, aberta ? null : estado!.MotivoFechada, aberta ? null : CaixaRegras.MensagemLojaFechada(estado!.MotivoFechada, estado.ProximaAbertura));
    }

    public async Task<CaixaDoDia> DecidirFechamento(Guid loja, int usuario, GestaoPapeis.Acesso acesso, DateOnly dia, DecisaoDoFechamentoRequest req,
        CancellationToken ct)
    {
        if (acesso.Papel != GestaoPapeis.Gerente) throw new GestaoException("SEM_PERMISSAO", "Só o gerente aprova o fechamento.", 403);
        var decisao = req.Decisao?.Trim().ToLowerInvariant();
        if (decisao is not (DecisaoAprovar or DecisaoDevolver)) throw Invalido("Decisão: aprovar ou devolver.");
        await using var c = await dataSource.OpenConnectionAsync(ct);
        var row = await Dia(c, loja, dia) ?? throw new GestaoException("NAO_ENCONTRADO", "Não há caixa neste dia.", 404);
        if (row.FechadoEm is not null)
        {
            // Fechado pelo operador: aprovar só dá o visto (já está no livro). Depois disso o próximo caixa pode abrir.
            if (decisao != DecisaoAprovar || row.AprovadoEm is not null) throw new GestaoException("FECHADO", "Este caixa já foi fechado e aprovado.", 409);
            await using var tx = await c.BeginTransactionAsync(ct);
            await c.ExecuteAsync(@"UPDATE gestao_caixa_dias SET aprovado_por = @usuario, aprovado_em = now()
 WHERE estabelecimento_id = @loja AND dia = @dia AND fechado_em IS NOT NULL AND aprovado_em IS NULL", new { loja, dia = row.Dia, usuario }, tx);
            await GestaoTempoReal.AvisarNoCommit(c, tx, loja, Guid.NewGuid(), GestaoTempoReal.Caixa);
            await tx.CommitAsync(ct);
            return (await CarregarDia(loja, dia, ct))!;
        }
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
        var linha = await c.QuerySingleOrDefaultAsync<(decimal Tolerancia, bool VeSaldo, TimeSpan Virada)?>(
            "SELECT tolerancia_fechamento, operador_ve_saldo, virada_do_dia FROM gestao_caixa_config WHERE estabelecimento_id = @loja", new { loja });
        // Sem configuração: toda diferença vai para o gerente, o operador vê o saldo e o dia vira às 05:00.
        return linha is { } l ? new ConfigDoCaixa(l.Tolerancia, l.VeSaldo, l.Virada.ToString(@"hh\:mm")) : new ConfigDoCaixa(0m, true);
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
