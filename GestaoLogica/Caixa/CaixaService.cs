using System.Text.Json;
using APIBack.Acontecimentos;
using APIBack.GestaoLogica.Equipe;
using APIBack.Security;
using Dapper;
using Npgsql;

namespace APIBack.GestaoLogica.Caixa;

/// <summary>
/// Caixa do dia pelo livro. Gaveta e cofre são contas <c>caixa</c>; o esperado é o saldo da gaveta. Abertura acerta a gaveta no
/// fundo de troco (transferência com o cofre); sangria, suprimento e recolhimento são transferências; pagamento em dinheiro é
/// despesa paga na gaveta; devolução estorna a venda (ou vira despesa "devolução de venda"). Fechamento: contagem às cegas
/// gravada, justificativa obrigatória se não bater, falta = despesa <c>quebra_de_caixa</c>, sobra = receita <c>sobra_de_caixa</c>.
/// </summary>
public interface ICaixaService
{
    Task<CaixaDoDia?> CarregarDia(Guid loja, DateOnly dia, CancellationToken ct);
    Task<CaixaDoDia> Abrir(Guid loja, int usuario, GestaoPapeis.Acesso acesso, AbrirCaixaRequest req, CancellationToken ct);
    Task<CaixaDoDia> RegistrarMovimento(Guid loja, int usuario, GestaoPapeis.Acesso acesso, MovimentoCaixaRequest req, CancellationToken ct);
    Task<ConferenciaDaContagem> ConferirContagem(Guid loja, int usuario, DateOnly dia, ContagemCaixa contagem, CancellationToken ct);
    Task<CaixaDoDia> Fechar(Guid loja, int usuario, GestaoPapeis.Acesso acesso, FecharCaixaRequest req, CancellationToken ct);
    Task<IReadOnlyList<FechamentoDto>> Fechamentos(Guid loja, DateOnly mes, CancellationToken ct);
    Task<FechamentoDto> AjustarFechamento(Guid loja, int usuario, DateOnly dia, AjustarFechamentoRequest req, CancellationToken ct);
    Task<DateOnly> Hoje(Guid loja, CancellationToken ct);
    Task<APagarNaGaveta> APagar(Guid loja, CancellationToken ct);
    Task<SolicitacaoDto> Solicitar(Guid loja, int usuario, GestaoPapeis.Acesso acesso, Guid movimentoId, SolicitarCorrecaoRequest req, CancellationToken ct);
    Task<CaixaDoDia> EstornarMovimento(Guid loja, int usuario, GestaoPapeis.Acesso acesso, Guid movimentoId, EstornarMovimentoRequest req, CancellationToken ct);
    Task<IReadOnlyList<SolicitacaoDto>> Solicitacoes(Guid loja, int usuario, GestaoPapeis.Acesso acesso, string? status, DateOnly? dia, CancellationToken ct);
    Task<SolicitacaoDto> Decidir(Guid loja, int usuario, GestaoPapeis.Acesso acesso, Guid solicitacaoId, DecisaoRequest req, CancellationToken ct);
    Task<CorrigirFundoResposta> CorrigirFundo(Guid loja, int usuario, GestaoPapeis.Acesso acesso, DateOnly dia, CorrigirFundoRequest req, CancellationToken ct);
}

public sealed partial class CaixaService(NpgsqlDataSource dataSource, IAcontecimentosService livro, TimeProvider? relogio = null,
    IEquipeService? equipe = null) : ICaixaService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly TimeProvider _relogio = relogio ?? TimeProvider.System;

    public const string TipoAbertura = "abertura_caixa", TipoSangria = "sangria", TipoSuprimento = "suprimento",
        TipoPagamento = "pagamento_dinheiro", TipoDevolucao = "devolucao_venda", TipoFechamento = "fechamento_caixa",
        TipoRecolhimento = "recolhimento", TipoAjusteFechamento = "ajuste_fechamento_caixa";

    private sealed record Contas(Guid Gaveta, Guid Cofre);

    private sealed class DiaRow
    {
        public DateTime Dia { get; set; }
        public decimal Fundo { get; set; }
        public int AbertoPor { get; set; }
        public DateTime AbertoEm { get; set; }
        public int? FechadoPor { get; set; }
        public DateTime? FechadoEm { get; set; }
        public Guid? FechamentoId { get; set; }
        public decimal? Esperado { get; set; }
        public decimal? Contado { get; set; }
        public string? Maquininha { get; set; }
        public string? MaquininhaEsperado { get; set; }
        public string? Justificativa { get; set; }
        public decimal? Fica { get; set; }
        public decimal? Cofre { get; set; }
        public string? AbertoPorNome { get; set; }
        public string? FechadoPorNome { get; set; }
    }

    private const string ColunasDia = @"dia, fundo, aberto_por, aberto_em, fechado_por, fechado_em, fechamento_id, esperado, contado,
       maquininha::text AS maquininha, maquininha_esperado::text AS maquininha_esperado, justificativa, fica, cofre,
       (SELECT u.nome::text FROM usuario u WHERE u.id = aberto_por) AS aberto_por_nome,
       (SELECT u.nome::text FROM usuario u WHERE u.id = fechado_por) AS fechado_por_nome";

    public async Task<DateOnly> Hoje(Guid loja, CancellationToken ct) =>
        AcontecimentoRegras.DiaLocal(_relogio.GetUtcNow(), await livro.Fuso(loja, ct));

    public async Task<CaixaDoDia?> CarregarDia(Guid loja, DateOnly dia, CancellationToken ct)
    {
        if (dia > await Hoje(loja, ct)) return null;
        await using var c = await dataSource.OpenConnectionAsync(ct);
        var row = await Dia(c, loja, dia);
        if (row is null) return new CaixaDoDia(dia, false, null, null, 0, Array.Empty<LinhaFita>(), Array.Empty<PedidoEmAberto>(), null);
        var contas = await ContasDoCaixa(c, loja, null, ct);
        var correcoes = await CorrecoesDoFundo(c, loja, dia);
        var fundo = correcoes.Count == 0 ? row.Fundo : correcoes[^1].FundoNovo;
        var pendentes = await PendentesPorMovimento(c, loja, dia);
        var fita = await Fita(loja, dia, contas.Gaveta, ct, fundo, row.FechadoEm is null, pendentes.Movimentos);
        // Pedido em aberto é pendência de hoje: dia fechado ou passado não reconstrói o que estava ativo (o banco guarda só o status atual).
        var emAberto = row.FechadoEm is null && dia == await Hoje(loja, ct)
            ? await PedidosEmAbertoQuery.Carregar(c, loja, ct) : Array.Empty<PedidoEmAberto>();
        return new CaixaDoDia(dia, row.FechadoEm is null, Utc(row.AbertoEm), row.AbertoPor, fundo, fita, emAberto,
            row.FechadoEm is null ? null : await Fechamento(c, loja, row), row.AbertoPorNome, row.Fundo, correcoes, pendentes.Total);
    }

    public async Task<CaixaDoDia> Abrir(Guid loja, int usuario, GestaoPapeis.Acesso acesso, AbrirCaixaRequest req, CancellationToken ct)
    {
        if (req.Fundo < 0 || decimal.Round(req.Fundo, 2) != req.Fundo) throw Invalido("Fundo de troco: zero ou mais, até 2 casas.");
        var hoje = await SoHoje(loja, req.Dia, ct);
        await using (var c = await dataSource.OpenConnectionAsync(ct))
        {
            if (await Dia(c, loja, hoje) is not null) return (await CarregarDia(loja, hoje, ct))!;
            var contas = await ContasDoCaixa(c, loja, usuario, ct);
            var gaveta = await Saldo(loja, contas.Gaveta, ct);
            // Abrir põe a gaveta no fundo de troco: o que falta vem do cofre, o que sobra vai para ele.
            var acerto = CaixaRegras.R2(req.Fundo - gaveta);
            var pedido = new PublicarAcontecimentoRequest
            {
                ChaveIdempotencia = $"caixa:abertura:{hoje:yyyy-MM-dd}", Tipo = TipoAbertura, Modulo = ModuloGestao.Caixa,
                DataFato = _relogio.GetUtcNow(), Descricao = $"Abertura do caixa · fundo R$ {req.Fundo:N2}",
                Origem = new OrigemRef { Tipo = "caixa", Id = hoje.ToString("yyyy-MM-dd") },
                Dados = JsonSerializer.SerializeToElement(new { fundo = req.Fundo, gavetaAntes = gaveta }, Json),
                Lancamentos = acerto == 0 ? new() : Transferencia(acerto > 0 ? contas.Cofre : contas.Gaveta,
                    acerto > 0 ? contas.Gaveta : contas.Cofre, Math.Abs(acerto), hoje, "Acerto do fundo de troco"),
            };
            GestaoPapeis.ValidarPublicacao(acesso, pedido, hoje, hoje);
            await using var tx = await c.BeginTransactionAsync(ct);
            var r = await livro.Publicar(c, tx, loja, usuario, pedido, ct);
            await c.ExecuteAsync(@"
INSERT INTO gestao_caixa_dias (estabelecimento_id, dia, fundo, aberto_por, aberto_em, abertura_id)
VALUES (@loja, @dia, @Fundo, @usuario, @agora, @id) ON CONFLICT DO NOTHING",
                new { loja, dia = hoje.ToDateTime(TimeOnly.MinValue), req.Fundo, usuario, agora = _relogio.GetUtcNow(), id = r.Id }, tx);
            await tx.CommitAsync(ct);
        }
        return (await CarregarDia(loja, hoje, ct))!;
    }

    public async Task<CaixaDoDia> RegistrarMovimento(Guid loja, int usuario, GestaoPapeis.Acesso acesso, MovimentoCaixaRequest req,
        CancellationToken ct)
    {
        var hoje = await SoHoje(loja, req.Dia, ct);
        if (string.IsNullOrWhiteSpace(req.ChaveIdempotencia)) throw Invalido("Envie a chave de idempotência.");
        Contas contas;
        await using (var c = await dataSource.OpenConnectionAsync(ct))
        {
            await DiaAberto(c, loja, hoje);
            contas = await ContasDoCaixa(c, loja, null, ct);
        }
        var gaveta = await Saldo(loja, contas.Gaveta, ct);
        CaixaRegras.ValidarMovimento(req.Tipo, req.Valor, gaveta);
        var motivo = string.IsNullOrWhiteSpace(req.Motivo) ? null : req.Motivo.Trim();
        var agora = _relogio.GetUtcNow();
        var dados = JsonSerializer.SerializeToElement(new { movimento = req.Tipo, motivo, referencia = req.Referencia, dia = hoje }, Json);

        if (req.Tipo == MovimentoCaixa.Devolucao && await VendaParaEstornar(loja, hoje, req.Referencia, req.Valor, contas.Gaveta, ct) is { } venda)
        {
            // A venda inteira em dinheiro volta: estorno da venda (a receita sai do dia e o dinheiro sai da gaveta).
            if (!acesso.PodeLancar) throw new GestaoException("SEM_PERMISSAO", "Você não pode registrar no caixa.", 403);
            await livro.Estornar(loja, usuario, venda, new EstornoRequest
            {
                ChaveIdempotencia = req.ChaveIdempotencia.Trim(), Motivo = Cortar($"Devolução: {motivo ?? "pedido devolvido"}", 300),
                ConfirmacaoHumana = true,
            }, ct);
            return (await CarregarDia(loja, hoje, ct))!;
        }

        if (req.Tipo == MovimentoCaixa.Pagamento && (req.PessoaId is not null || req.LancamentoId is not null))
        {
            // Diária ou compra paga pela gaveta: quita o que já está a pagar (não cria outra despesa) e o dinheiro sai da gaveta.
            if (!acesso.PodeLancar) throw new GestaoException("SEM_PERMISSAO", "Você não pode registrar no caixa.", 403);
            await PagarPelaGaveta(loja, usuario, req, hoje, contas.Gaveta, motivo, ct);
            return (await CarregarDia(loja, hoje, ct))!;
        }

        var pedido = new PublicarAcontecimentoRequest
        {
            ChaveIdempotencia = req.ChaveIdempotencia.Trim(), Modulo = ModuloGestao.Caixa, DataFato = agora, Dados = dados,
        };
        switch (req.Tipo)
        {
            case MovimentoCaixa.Sangria:
                pedido.Tipo = TipoSangria;
                pedido.Descricao = Cortar($"Sangria · {motivo ?? "cofre"}", 300);
                pedido.Lancamentos = Transferencia(contas.Gaveta, req.ContaId ?? contas.Cofre, req.Valor, hoje, pedido.Descricao);
                break;
            case MovimentoCaixa.Suprimento:
                pedido.Tipo = TipoSuprimento;
                pedido.Descricao = Cortar($"Suprimento · {motivo ?? "reforço de troco"}", 300);
                pedido.Lancamentos = Transferencia(req.ContaId ?? contas.Cofre, contas.Gaveta, req.Valor, hoje, pedido.Descricao);
                break;
            case MovimentoCaixa.Pagamento:
                if (motivo is null) throw Invalido("Diga para quem foi o pagamento.");
                pedido.Tipo = TipoPagamento;
                pedido.Descricao = Cortar($"{motivo} · pago em dinheiro", 300);
                pedido.Lancamentos = new() { Saida(Natureza.Despesa, req.Valor, contas.Gaveta, hoje, "despesas_caixa", pedido.Descricao) };
                break;
            default: // devolução sem venda no livro (ou parcial)
                if (motivo is null) throw Invalido("Diga o motivo da devolução.");
                pedido.Tipo = TipoDevolucao;
                pedido.Descricao = Cortar($"Devolução · {motivo}" + (req.Referencia is null ? "" : $" · pedido {req.Referencia}"), 300);
                pedido.Lancamentos = new() { Saida(Natureza.Despesa, req.Valor, contas.Gaveta, hoje, "devolucao_de_venda", pedido.Descricao) };
                break;
        }
        GestaoPapeis.ValidarPublicacao(acesso, pedido, hoje, hoje);
        await livro.Publicar(loja, usuario, pedido, ct);
        return (await CarregarDia(loja, hoje, ct))!;
    }

    public async Task<APagarNaGaveta> APagar(Guid loja, CancellationToken ct) =>
        new(equipe is null ? Array.Empty<PessoaAPagar>() : await equipe.APagarPorPessoa(loja, ct), await ComprasAPagar(loja, ct));

    private async Task PagarPelaGaveta(Guid loja, int usuario, MovimentoCaixaRequest req, DateOnly hoje, Guid gaveta, string? motivo,
        CancellationToken ct)
    {
        var chave = req.ChaveIdempotencia!.Trim();
        if (req.PessoaId is { } pessoaId)
        {
            if (equipe is null) throw new GestaoException("NAO_ENCONTRADO", "A Equipe não está disponível.", 404);
            var devido = (await equipe.APagarPorPessoa(loja, ct)).FirstOrDefault(p => p.PessoaId == pessoaId);
            // Nova tentativa com a mesma chave (o primeiro envio pode já ter pago): a Equipe não paga duas vezes.
            if (devido is null && !await ChaveUsada(loja, chave)) throw Invalido("Esta pessoa não tem nada a receber.");
            if (devido is not null && req.Valor > devido.APagar) throw Invalido($"{devido.Nome} tem {devido.APagar:N2} a receber: pague até esse valor.");
            await equipe.Pagar(loja, usuario, new PagamentoRequest(chave, pessoaId, gaveta, hoje, req.Valor), ct);
            return;
        }
        var compra = (await ComprasAPagar(loja, ct)).FirstOrDefault(x => x.LancamentoId == req.LancamentoId);
        if (compra is null && !await ChaveUsada(loja, chave)) throw Invalido("Esta compra não está a pagar.");
        if (compra is not null && compra.Valor != req.Valor) throw Invalido($"A compra se paga inteira: {compra.Valor:N2}.");
        await livro.Liquidar(loja, usuario, new LiquidarRequest
        {
            ChaveIdempotencia = chave, LancamentoId = req.LancamentoId!.Value, ContaId = gaveta, DataPagamento = hoje,
            Descricao = Cortar(motivo is null ? null : $"{motivo} · pago em dinheiro", 300),
        }, ct);
    }

    private async Task<bool> ChaveUsada(Guid loja, string chave)
    {
        await using var c = await dataSource.OpenConnectionAsync();
        return await c.ExecuteScalarAsync<bool>(@"SELECT EXISTS (SELECT 1 FROM gestao_acontecimentos WHERE estabelecimento_id = @loja
   AND (chave_idempotencia = @chave OR left(chave_idempotencia, @n) = @prefixo))", new { loja, chave, prefixo = chave + ":", n = chave.Length + 1 });
    }

    /// <summary>Compras a pagar fora do cartão (o cartão vai para a fatura), com o valor líquido, das mais antigas às mais novas.</summary>
    private async Task<IReadOnlyList<CompraAPagar>> ComprasAPagar(Guid loja, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<(Guid Id, Guid AcontecimentoId, string Descricao, decimal Valor, DateTime? Vencimento)>(@"
SELECT s.id, a.id, coalesce(s.descricao, a.descricao),
       s.valor + coalesce((SELECT sum(x.valor) FROM gestao_lancamentos x
                            WHERE x.estabelecimento_id = s.estabelecimento_id AND x.corrige_lancamento_id = s.id), 0),
       s.data_vencimento
  FROM gestao_acontecimentos a
  JOIN gestao_lancamentos_situacao s ON s.estabelecimento_id = a.estabelecimento_id AND s.acontecimento_id = a.id AND s.corrige_lancamento_id IS NULL
 WHERE a.estabelecimento_id = @loja AND a.modulo = 'compras' AND a.corrige_id IS NULL
   AND s.status = 'a_pagar' AND s.natureza = 'despesa' AND s.fatura_mes IS NULL
 ORDER BY s.data_vencimento NULLS LAST, a.data_fato, s.linha
 LIMIT 100", new { loja }))
            .Where(x => x.Valor > 0)
            .Select(x => new CompraAPagar(x.Id, x.AcontecimentoId, x.Descricao, x.Valor, x.Vencimento is { } v ? DateOnly.FromDateTime(v) : null))
            .ToList();
    }

    public async Task<ConferenciaDaContagem> ConferirContagem(Guid loja, int usuario, DateOnly dia, ContagemCaixa contagem, CancellationToken ct)
    {
        var hoje = await SoHoje(loja, dia, ct);
        var contado = CaixaRegras.TotalContado(contagem);
        await using var c = await dataSource.OpenConnectionAsync(ct);
        await DiaAberto(c, loja, hoje);
        var esperado = await Saldo(loja, (await ContasDoCaixa(c, loja, null, ct)).Gaveta, ct);
        // Às cegas: a pessoa conta primeiro; só depois recebe o esperado. Toda contagem fica gravada.
        await GravarContagem(c, null, loja, hoje, usuario, contagem, contado, esperado, "conferencia");
        return new ConferenciaDaContagem(esperado, contado, CaixaRegras.Diferenca(esperado, contado));
    }

    public async Task<CaixaDoDia> Fechar(Guid loja, int usuario, GestaoPapeis.Acesso acesso, FecharCaixaRequest req, CancellationToken ct)
    {
        var hoje = await SoHoje(loja, req.Dia, ct);
        var contado = CaixaRegras.TotalContado(req.Contagem);
        await using var c = await dataSource.OpenConnectionAsync(ct);
        await DiaAberto(c, loja, hoje);
        var contas = await ContasDoCaixa(c, loja, null, ct);
        var esperado = await Saldo(loja, contas.Gaveta, ct);
        var diferenca = CaixaRegras.Diferenca(esperado, contado);
        CaixaRegras.ExigirJustificativa(diferenca, req.Justificativa);
        var (fica, cofre) = CaixaRegras.Repartir(contado, req.Fica);
        var fita = await Fita(loja, hoje, contas.Gaveta, ct);
        var maqEsperado = FormaCaixa.Maquininha.ToDictionary(f => f, f => CaixaRegras.R2(fita.Where(l => l.Tipo == "venda" && l.Forma == f).Sum(l => l.Valor)));
        var maquininha = FormaCaixa.Maquininha.ToDictionary(f => f, f => CaixaRegras.R2(req.Maquininha?.GetValueOrDefault(f) ?? 0));
        var justificativa = diferenca == 0 ? null : Cortar(req.Justificativa!.Trim(), 300);
        var dia = hoje.ToString("yyyy-MM-dd");

        var fechamento = new PublicarAcontecimentoRequest
        {
            ChaveIdempotencia = $"caixa:fechamento:{dia}", Tipo = TipoFechamento, Modulo = ModuloGestao.Caixa, DataFato = _relogio.GetUtcNow(),
            Descricao = diferenca == 0 ? "Fechamento do caixa · bateu" : $"Fechamento do caixa · {(diferenca < 0 ? "faltou" : "sobrou")} R$ {Math.Abs(diferenca):N2}",
            Origem = new OrigemRef { Tipo = "caixa", Id = dia },
            Dados = JsonSerializer.SerializeToElement(new
            {
                contagem = req.Contagem, esperado, contado, diferenca, justificativa, maquininha, maquininhaEsperado = maqEsperado,
                maquininhaConfere = FormaCaixa.Maquininha.ToDictionary(f => f, f => CaixaRegras.MaquininhaConfere(maquininha[f], maqEsperado[f])),
                fica, cofre,
            }, Json),
            Lancamentos = diferenca switch
            {
                < 0 => new() { Saida(Natureza.Despesa, -diferenca, contas.Gaveta, hoje, "quebra_de_caixa", "Quebra de caixa") },
                > 0 => new() { new LancamentoRequest { Natureza = Natureza.Receita, Valor = diferenca, ContaId = contas.Gaveta, DataPagamento = hoje,
                    Categoria = "sobra_de_caixa", Descricao = "Sobra de caixa" } },
                _ => new(),
            },
        };
        GestaoPapeis.ValidarPublicacao(acesso, fechamento, hoje, hoje);
        await using var tx = await c.BeginTransactionAsync(ct);
        await GravarContagem(c, tx, loja, hoje, usuario, req.Contagem, contado, esperado, "fechamento");
        var f = await livro.Publicar(c, tx, loja, usuario, fechamento, ct);
        Guid? recolhimento = null;
        if (cofre > 0)
            recolhimento = (await livro.Publicar(c, tx, loja, usuario, new PublicarAcontecimentoRequest
            {
                ChaveIdempotencia = $"caixa:recolhimento:{dia}", Tipo = TipoRecolhimento, Modulo = ModuloGestao.Caixa, DataFato = _relogio.GetUtcNow(),
                Descricao = $"Recolhimento ao cofre · R$ {cofre:N2}", Origem = new OrigemRef { Tipo = "caixa", Id = dia },
                Lancamentos = Transferencia(contas.Gaveta, contas.Cofre, cofre, hoje, "Recolhimento ao cofre"),
            }, ct)).Id;
        var n = await c.ExecuteAsync(@"
UPDATE gestao_caixa_dias SET fechado_por = @usuario, fechado_em = @agora, fechamento_id = @fechamentoId, recolhimento_id = @recolhimento,
       esperado = @esperado, contado = @contado, maquininha = CAST(@maquininha AS jsonb), maquininha_esperado = CAST(@maqEsperado AS jsonb),
       justificativa = @justificativa, fica = @fica, cofre = @cofre
 WHERE estabelecimento_id = @loja AND dia = @dia AND fechado_em IS NULL", new
        {
            loja, dia = hoje.ToDateTime(TimeOnly.MinValue), usuario, agora = _relogio.GetUtcNow(), fechamentoId = f.Id, recolhimento, esperado,
            contado, maquininha = JsonSerializer.Serialize(maquininha), maqEsperado = JsonSerializer.Serialize(maqEsperado), justificativa, fica, cofre,
        }, tx);
        if (n == 0) throw new GestaoException("FECHADO", "O caixa de hoje já foi fechado.", 409);
        await tx.CommitAsync(ct);
        return (await CarregarDia(loja, hoje, ct))!;
    }

    public async Task<IReadOnlyList<FechamentoDto>> Fechamentos(Guid loja, DateOnly mes, CancellationToken ct)
    {
        mes = new DateOnly(mes.Year, mes.Month, 1);
        await using var c = await dataSource.OpenConnectionAsync(ct);
        var dias = await c.QueryAsync<DiaRow>($@"SELECT {ColunasDia} FROM gestao_caixa_dias
 WHERE estabelecimento_id = @loja AND fechado_em IS NOT NULL AND dia >= @de AND dia < @ate ORDER BY dia DESC",
            new { loja, de = mes.ToDateTime(TimeOnly.MinValue), ate = mes.AddMonths(1).ToDateTime(TimeOnly.MinValue) });
        var lista = new List<FechamentoDto>();
        foreach (var d in dias) lista.Add(await Fechamento(c, loja, d));
        return lista;
    }

    public async Task<FechamentoDto> AjustarFechamento(Guid loja, int usuario, DateOnly dia, AjustarFechamentoRequest req, CancellationToken ct)
    {
        var motivo = req.Motivo?.Trim();
        if (motivo is null || motivo.Length < 3) throw Invalido("Diga o motivo da correção (mínimo de 3 letras).");
        if (req.ContadoNovo < 0 || decimal.Round(req.ContadoNovo, 2) != req.ContadoNovo) throw Invalido("Valor contado inválido.");
        await using var c = await dataSource.OpenConnectionAsync(ct);
        var row = await Dia(c, loja, dia);
        if (row?.FechadoEm is null) throw new GestaoException("NAO_ENCONTRADO", "Não há fechamento neste dia.", 404);
        var atual = (await Fechamento(c, loja, row)) is var f && f.Ajustes.Count > 0 ? f.Ajustes[^1].ContadoNovo : row.Contado!.Value;
        var delta = CaixaRegras.R2(req.ContadoNovo - atual);
        if (delta == 0) throw Invalido("O valor novo é igual ao atual.");
        var contas = await ContasDoCaixa(c, loja, null, ct);
        var ajusteId = Guid.NewGuid();
        var hoje = await Hoje(loja, ct);
        // O fechamento nunca é reescrito: a diferença da recontagem vira mais sobra ou mais quebra, com motivo.
        await using var tx = await c.BeginTransactionAsync(ct);
        var r = await livro.Publicar(c, tx, loja, usuario, new PublicarAcontecimentoRequest
        {
            ChaveIdempotencia = $"caixa:ajuste:{dia:yyyy-MM-dd}:{ajusteId}", Tipo = TipoAjusteFechamento, Modulo = ModuloGestao.Caixa,
            DataFato = _relogio.GetUtcNow(), Descricao = Cortar($"Correção do fechamento de {dia:dd/MM}: {motivo}", 300),
            Origem = new OrigemRef { Tipo = "caixa_ajuste", Id = ajusteId.ToString() },
            Dados = JsonSerializer.SerializeToElement(new { dia, contadoAnterior = atual, contadoNovo = req.ContadoNovo, motivo }, Json),
            Lancamentos = new()
            {
                delta < 0
                    ? Saida(Natureza.Despesa, -delta, contas.Gaveta, hoje, "quebra_de_caixa", "Quebra de caixa (recontagem)")
                    : new LancamentoRequest { Natureza = Natureza.Receita, Valor = delta, ContaId = contas.Gaveta, DataPagamento = hoje,
                        Categoria = "sobra_de_caixa", Descricao = "Sobra de caixa (recontagem)" },
            },
        }, ct);
        await c.ExecuteAsync(@"
INSERT INTO gestao_caixa_ajustes (id, estabelecimento_id, dia, por, em, contado_anterior, contado_novo, motivo, acontecimento_id)
VALUES (@ajusteId, @loja, @dia, @usuario, @agora, @atual, @ContadoNovo, @motivo, @acontecimento)", new
        {
            ajusteId, loja, dia = dia.ToDateTime(TimeOnly.MinValue), usuario, agora = _relogio.GetUtcNow(), atual, req.ContadoNovo, motivo,
            acontecimento = r.Id,
        }, tx);
        await tx.CommitAsync(ct);
        return await Fechamento(c, loja, row);
    }

    // ---------------------------------------------------------------- apoio

    /// <summary>Fita do dia a partir do livro: abertura, vendas (todas as formas) e o que mexeu na gaveta.</summary>
    private async Task<IReadOnlyList<LinhaFita>> Fita(Guid loja, DateOnly dia, Guid gaveta, CancellationToken ct, decimal? fundoEfetivo = null,
        bool diaAberto = false, IReadOnlyDictionary<Guid, Guid>? pendentes = null)
    {
        LinhaFita Marca(LinhaFita l, AcontecimentoDto a)
        {
            var estornada = a.CorrigidoPor.Count > 0;
            var pendente = pendentes is not null && pendentes.TryGetValue(a.Id, out var s) ? s : (Guid?)null;
            return l with { Estornada = estornada, SolicitacaoPendente = pendente,
                Corrigivel = diaAberto && !estornada && pendente is null && Estornavel(a, gaveta) };
        }
        var fuso = await livro.Fuso(loja, ct);
        var acontecimentos = await livro.LinhaDoTempo(loja, new PeriodoFiltro { Mes = new DateOnly(dia.Year, dia.Month, 1), Dia = dia, Limite = 1000 }, ct);
        var fita = new List<LinhaFita>();
        foreach (var a in acontecimentos.OrderBy(a => a.DataFato).ThenBy(a => a.RegistradoEm))
        {
            var hora = TimeZoneInfo.ConvertTime(a.DataFato, fuso).ToString("HH:mm");
            if (a.Tipo == TipoAbertura)
            {
                fita.Add(new LinhaFita(a.Id.ToString(), hora, "abre", "Abertura · fundo de troco", FormaCaixa.Dinheiro,
                    fundoEfetivo ?? (a.Dados.TryGetProperty("fundo", out var fundo) ? fundo.GetDecimal() : 0)));
                continue;
            }
            if (a.Tipo is TipoFechamento or TipoRecolhimento or TipoAjusteFechamento or TipoCorrecaoFundo) continue;
            if (a.Tipo == "venda")
            {
                var forma = a.Dados.TryGetProperty("forma", out var fx) && fx.ValueKind == JsonValueKind.String ? fx.GetString()! : null;
                foreach (var l in a.Lancamentos.Where(l => l.Natureza == Natureza.Receita))
                    fita.Add(new LinhaFita($"{a.Id}:{l.Linha}", hora, "venda", a.Descricao,
                        forma ?? (l.ContaId == gaveta ? FormaCaixa.Dinheiro : FormaCaixa.Pix), l.Valor));
                continue;
            }
            var tipo = a.Tipo switch
            {
                TipoSuprimento => "sup",
                TipoSangria => "sang",
                TipoPagamento => "pag",
                TipoDevolucao => "canc",
                _ when a.Correcao == Correcao.Estorno => "canc",
                _ => a.Lancamentos.Any(l => l.Sentido == Sentido.Entrada) ? "sup" : "pag",
            };
            foreach (var l in a.Lancamentos.Where(l => l.ContaId == gaveta))
                fita.Add(Marca(new LinhaFita($"{a.Id}:{l.Linha}", hora, tipo, a.Descricao, FormaCaixa.Dinheiro,
                    l.Sentido == Sentido.Entrada ? l.Valor : -l.Valor), a));
            // Algo que estava a pagar (diária, compra) quitado com dinheiro da gaveta: o esperado já conta, a fita mostra.
            var naGaveta = a.Liquidacoes.Where(q => q.ContaId == gaveta).ToList();
            var sentidos = naGaveta.Count == 0 ? null : await SentidosLiquidados(loja, naGaveta);
            foreach (var q in naGaveta)
            {
                var entra = sentidos!.GetValueOrDefault(q.LancamentoId) == Sentido.Entrada;
                var valor = entra ? q.Valor : -q.Valor;
                fita.Add(Marca(new LinhaFita($"{a.Id}:q:{q.Id}", hora, q.CorrigeLiquidacaoId is null ? (entra ? "sup" : "pag") : "canc", a.Descricao,
                    FormaCaixa.Dinheiro, valor), a));
            }
        }
        return fita;
    }

    private async Task<Dictionary<Guid, string>> SentidosLiquidados(Guid loja, IReadOnlyList<LiquidacaoDto> liquidacoes)
    {
        await using var c = await dataSource.OpenConnectionAsync();
        return (await c.QueryAsync<(Guid Id, string Sentido)>("SELECT id, sentido FROM gestao_lancamentos WHERE estabelecimento_id = @loja AND id = ANY(@ids)",
            new { loja, ids = liquidacoes.Select(q => q.LancamentoId).Distinct().ToArray() })).ToDictionary(x => x.Id, x => x.Sentido);
    }

    /// <summary>A venda do pedido, em dinheiro na gaveta, com o mesmo valor: devolver tudo = estornar a venda.</summary>
    private async Task<Guid?> VendaParaEstornar(Guid loja, DateOnly dia, string? referencia, decimal valor, Guid gaveta, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(referencia)) return null;
        var vendas = await livro.LinhaDoTempo(loja, new PeriodoFiltro { Mes = new DateOnly(dia.Year, dia.Month, 1), Tipo = "venda",
            OrigemTipo = "pedido", OrigemId = referencia.Trim(), Limite = 5 }, ct);
        var venda = vendas.FirstOrDefault(v => v.CorrigeId is null && v.CorrigidoPor.Count == 0);
        if (venda is null) return null;
        var naGaveta = venda.Lancamentos.Where(l => l.Natureza == Natureza.Receita && l.ContaId == gaveta).Sum(l => l.Valor);
        return naGaveta == valor ? venda.Id : null;
    }

    private async Task<FechamentoDto> Fechamento(NpgsqlConnection c, Guid loja, DiaRow d)
    {
        var dia = DateOnly.FromDateTime(d.Dia);
        var ajustes = (await c.QueryAsync<(Guid Id, DateTime Em, int Por, decimal Anterior, decimal Novo, string Motivo, string? PorNome)>(@"
SELECT a.id, a.em, a.por, a.contado_anterior, a.contado_novo, a.motivo, u.nome::text AS por_nome
  FROM gestao_caixa_ajustes a
  LEFT JOIN usuario u ON u.id = a.por
 WHERE a.estabelecimento_id = @loja AND a.dia = @dia ORDER BY a.em, a.id", new { loja, dia = d.Dia }))
            .Select(a => new AjusteFechamento(a.Id, Utc(a.Em), a.Por, a.Anterior, a.Novo, a.Motivo, a.PorNome)).ToList();
        var vazio = new Dictionary<string, decimal>();
        return new FechamentoDto(dia.ToString("yyyy-MM-dd"), dia, d.FechadoPor!.Value, Utc(d.AbertoEm), Utc(d.FechadoEm!.Value), d.Esperado!.Value,
            d.Contado!.Value, d.Maquininha is null ? vazio : JsonSerializer.Deserialize<Dictionary<string, decimal>>(d.Maquininha)!,
            d.MaquininhaEsperado is null ? vazio : JsonSerializer.Deserialize<Dictionary<string, decimal>>(d.MaquininhaEsperado)!,
            d.Justificativa, d.Fica ?? 0, d.Cofre ?? 0, ajustes, d.FechadoPorNome);
    }

    /// <summary>Gaveta e cofre da loja, criados na primeira abertura (contas "caixa" do livro, idempotentes pela chave).</summary>
    private async Task<Contas> ContasDoCaixa(NpgsqlConnection c, Guid loja, int? criarComo, CancellationToken ct)
    {
        var atual = await c.QuerySingleOrDefaultAsync<(Guid Gaveta, Guid Cofre)>(
            "SELECT conta_gaveta_id, conta_cofre_id FROM gestao_caixas WHERE estabelecimento_id = @loja", new { loja });
        if (atual.Gaveta != Guid.Empty) return new Contas(atual.Gaveta, atual.Cofre);
        if (criarComo is null) throw new GestaoException("SEM_CAIXA", "O caixa desta loja ainda não foi aberto.", 409);
        var gaveta = await livro.CriarConta(loja, criarComo, new ContaRequest { ChaveIdempotencia = "caixa:gaveta", Tipo = TipoConta.Caixa, Nome = "Gaveta do caixa" }, ct);
        var cofre = await livro.CriarConta(loja, criarComo, new ContaRequest { ChaveIdempotencia = "caixa:cofre", Tipo = TipoConta.Caixa, Nome = "Cofre" }, ct);
        await c.ExecuteAsync(@"
INSERT INTO gestao_caixas (estabelecimento_id, conta_gaveta_id, conta_cofre_id) VALUES (@loja, @gaveta, @cofre) ON CONFLICT DO NOTHING",
            new { loja, gaveta = gaveta.Id, cofre = cofre.Id });
        return await ContasDoCaixa(c, loja, null, ct);
    }

    private async Task<decimal> Saldo(Guid loja, Guid conta, CancellationToken ct) =>
        (await livro.Contas(loja, ct)).First(x => x.Id == conta).Saldo;

    private async Task<DateOnly> SoHoje(Guid loja, DateOnly dia, CancellationToken ct)
    {
        var hoje = await Hoje(loja, ct);
        if (dia != hoje) throw Invalido("O caixa registra só o dia de hoje. Para corrigir um dia passado, ajuste o fechamento.");
        return hoje;
    }

    private static async Task<DiaRow?> Dia(NpgsqlConnection c, Guid loja, DateOnly dia) =>
        await c.QuerySingleOrDefaultAsync<DiaRow>($"SELECT {ColunasDia} FROM gestao_caixa_dias WHERE estabelecimento_id = @loja AND dia = @dia",
            new { loja, dia = dia.ToDateTime(TimeOnly.MinValue) });

    private static async Task DiaAberto(NpgsqlConnection c, Guid loja, DateOnly dia)
    {
        var d = await Dia(c, loja, dia) ?? throw new GestaoException("SEM_CAIXA", "Abra o caixa antes.", 409);
        if (d.FechadoEm is not null) throw new GestaoException("FECHADO", "O caixa de hoje já foi fechado.", 409);
    }

    private static Task GravarContagem(NpgsqlConnection c, NpgsqlTransaction? tx, Guid loja, DateOnly dia, int usuario, ContagemCaixa contagem,
        decimal contado, decimal esperado, string etapa) =>
        c.ExecuteAsync(@"
INSERT INTO gestao_caixa_contagens (id, estabelecimento_id, dia, por, em, notas, moedas, contado, esperado, etapa)
VALUES (@id, @loja, @dia, @usuario, now(), CAST(@notas AS jsonb), @Moedas, @contado, @esperado, @etapa)", new
        {
            id = Guid.NewGuid(), loja, dia = dia.ToDateTime(TimeOnly.MinValue), usuario,
            notas = JsonSerializer.Serialize(contagem.Notas ?? new Dictionary<int, int>()), contagem.Moedas, contado, esperado, etapa,
        }, tx);

    private static List<LancamentoRequest> Transferencia(Guid de, Guid para, decimal valor, DateOnly dia, string descricao) => new()
    {
        new LancamentoRequest { Natureza = Natureza.Transferencia, Sentido = Sentido.Saida, Valor = valor, ContaId = de, DataPagamento = dia, Descricao = descricao },
        new LancamentoRequest { Natureza = Natureza.Transferencia, Sentido = Sentido.Entrada, Valor = valor, ContaId = para, DataPagamento = dia, Descricao = descricao },
    };

    private static LancamentoRequest Saida(string natureza, decimal valor, Guid conta, DateOnly dia, string categoria, string descricao) =>
        new() { Natureza = natureza, Valor = valor, ContaId = conta, DataPagamento = dia, Categoria = categoria, Descricao = descricao };

    private static DateTimeOffset Utc(DateTime d) => new(DateTime.SpecifyKind(d, DateTimeKind.Utc));

    private static string Cortar(string s, int max) => s.Length <= max ? s : s[..max];

    private static GestaoException Invalido(string m) => new("CAMPO_INVALIDO", m, 422);
}
