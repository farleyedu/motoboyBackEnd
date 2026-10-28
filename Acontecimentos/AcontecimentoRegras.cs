using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace APIBack.Acontecimentos;

public sealed record ContaInfo(Guid Id, string Tipo, int? DiaFechamento, int? DiaVencimento, bool Ativo);

public sealed record LancamentoPlano(int Linha, Guid? ContaId, string Natureza, string Sentido, decimal Valor, string Status,
    DateOnly MesReferencia, DateOnly? DataVencimento, DateOnly? DataPagamento, int Parcela, int Parcelas, DateOnly? FaturaMes,
    string? Categoria, string? Finalidade, string? Descricao, Guid? CorrigeLancamentoId);

public sealed record MovimentoPlano(int Linha, Guid ItemId, string ItemNome, decimal Quantidade, string Unidade, string Tipo,
    string? Motivo, DateOnly MesReferencia, Guid? CorrigeMovimentoId);

public sealed record LiquidacaoPlano(Guid LancamentoId, Guid ContaId, decimal Valor, DateOnly DataPagamento,
    Guid? CorrigeLiquidacaoId);

/// <summary>Tudo o que sera gravado numa transacao. Montado so pelas regras; o repositorio apenas insere.</summary>
public sealed record AcontecimentoPlano(string Chave, string Hash, string Tipo, string Modulo, DateTimeOffset DataFato,
    DateOnly MesReferencia, int? ResponsavelId, string? OrigemTipo, string? OrigemId, string? DocumentoChave,
    string Descricao, string DadosJson, Guid? CorrigeId, string? Correcao, string? Motivo, decimal? ValorAnterior,
    decimal? ValorNovo, IReadOnlyList<LancamentoPlano> Lancamentos, IReadOnlyList<MovimentoPlano> Movimentos,
    IReadOnlyList<LiquidacaoPlano> Liquidacoes)
{
    public IEnumerable<DateOnly> MesesTocados() =>
        Lancamentos.Select(l => l.MesReferencia)
            .Concat(Movimentos.Select(m => m.MesReferencia))
            .Concat(Liquidacoes.Select(q => AcontecimentoRegras.Mes(q.DataPagamento)))
            .Append(MesReferencia)
            .Distinct();
}

/// <summary>Estado atual de um lancamento (registro + liquidacoes), lido do banco para planejar correcoes.</summary>
public sealed record LancamentoAtual(Guid Id, Guid AcontecimentoId, int Linha, Guid? ContaId, string Natureza, string Sentido,
    decimal Valor, string StatusRegistro, string Status, DateOnly MesReferencia, DateOnly? DataVencimento, int Parcela,
    int Parcelas, DateOnly? FaturaMes, string? Categoria, string? Finalidade, string? Descricao, Guid? CorrigeLancamentoId,
    decimal Liquidado);

public sealed record MovimentoAtual(Guid Id, Guid AcontecimentoId, int Linha, Guid ItemId, string ItemNome, decimal Quantidade,
    string Unidade, string Tipo, DateOnly MesReferencia, Guid? CorrigeMovimentoId);

public sealed record LiquidacaoAtual(Guid Id, Guid AcontecimentoId, Guid LancamentoId, Guid ContaId, decimal Valor,
    DateOnly DataPagamento, Guid? CorrigeLiquidacaoId);

public sealed record AcontecimentoAtual(Guid Id, string Tipo, string Modulo, string? Correcao, DateOnly MesReferencia,
    bool Estornado);

/// <summary>
/// Regras do livro de acontecimentos (regras de negocio 1 a 5 do bloco comum). Puras, sem banco: recebem o pedido e o
/// estado atual e devolvem o plano a gravar ou uma GestaoException.
/// </summary>
public static class AcontecimentoRegras
{
    public const int MaxLinhas = 200;
    public const int MaxParcelas = 48;
    public const int MaxDadosBytes = 16_384;
    private static readonly Regex Slug = new("^[a-z][a-z_]{0,39}$", RegexOptions.Compiled);
    private static readonly TimeSpan ToleranciaFuturo = TimeSpan.FromMinutes(10);

    public static DateOnly Mes(DateOnly dia) => new(dia.Year, dia.Month, 1);
    public static DateOnly DiaLocal(DateTimeOffset instante, TimeZoneInfo fuso) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instante, fuso).DateTime);
    public static string MesTexto(DateOnly mes) => mes.ToString("MM/yyyy", CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------- publicacao

    public static AcontecimentoPlano PlanejarPublicacao(PublicarAcontecimentoRequest? r, int? responsavelId,
        IReadOnlyDictionary<Guid, ContaInfo> contas, TimeZoneInfo fuso, DateTimeOffset agora)
    {
        if (r == null) throw Invalido("Envie o acontecimento.");
        var chave = Chave(r.ChaveIdempotencia);
        var tipo = Texto(r.Tipo, "tipo", 40)!;
        if (!Slug.IsMatch(tipo)) throw Invalido("Tipo deve ter só letras minúsculas e _.");
        if (TipoAcontecimento.Reservados.Contains(tipo))
            throw Invalido($"O tipo {tipo} é criado só pelo próprio livro (use o endpoint de {tipo}).");
        var modulo = Texto(r.Modulo, "modulo", 20)!;
        if (!ModuloGestao.Todos.Contains(modulo)) throw Invalido("Módulo desconhecido.");
        var dataFato = r.DataFato ?? throw Invalido("Informe a data do fato.");
        if (dataFato > agora + ToleranciaFuturo) throw Invalido("A data do fato não pode estar no futuro.");
        var descricao = Texto(r.Descricao, "descricao", 300)!;
        var (origemTipo, origemId) = Origem(r.Origem);
        var documento = Documento(r.DocumentoChave);
        var dados = Dados(r.Dados);
        var diaFato = DiaLocal(dataFato, fuso);

        var lancamentos = PlanejarLancamentos(r.Lancamentos ?? new(), contas, diaFato);
        var movimentos = PlanejarMovimentos(r.Movimentos ?? new(), diaFato, r.ConfirmacaoHumana, responsavelId);

        // Perda nao gera saida de caixa (o custo vai para Gestao e Inteligencia); divergencia de inventario fica como
        // divergencia e nunca vira despesa ou perda sozinha.
        if (tipo == TipoAcontecimento.Perda)
        {
            if (lancamentos.Count > 0) throw Invalido("Perda não gera lançamento financeiro: o custo vai para Gestão e Inteligência.");
            if (movimentos.Count == 0 || movimentos.Any(m => m.Tipo != TipoMovimento.Perda))
                throw Invalido("Uma perda tem só movimentos do tipo perda.");
        }
        else if (movimentos.Any(m => m.Tipo == TipoMovimento.Perda))
            throw Invalido("Movimento de perda só num acontecimento do tipo perda.");
        if (tipo == TipoAcontecimento.Inventario)
        {
            if (lancamentos.Count > 0) throw Invalido("Inventário não gera lançamento financeiro.");
            if (movimentos.Any(m => m.Tipo != TipoMovimento.Divergencia))
                throw Invalido("Inventário registra só divergências; perda é outro acontecimento, com motivo.");
        }
        else if (movimentos.Any(m => m.Tipo == TipoMovimento.Divergencia))
            throw Invalido("Divergência só num acontecimento do tipo inventario.");

        var hash = Hash(new
        {
            tipo, modulo, dataFato = dataFato.ToUniversalTime(), descricao, origemTipo, origemId, documento, dados,
            responsavelId, r.ConfirmacaoHumana, lancamentos = r.Lancamentos, movimentos = r.Movimentos
        });
        return new AcontecimentoPlano(chave, hash, tipo, modulo, dataFato, Mes(diaFato), responsavelId, origemTipo, origemId,
            documento, descricao, dados, null, null, null, null, null, lancamentos, movimentos,
            Array.Empty<LiquidacaoPlano>());
    }

    private static List<LancamentoPlano> PlanejarLancamentos(List<LancamentoRequest> pedidos,
        IReadOnlyDictionary<Guid, ContaInfo> contas, DateOnly diaFato)
    {
        var plano = new List<LancamentoPlano>();
        foreach (var l in pedidos)
        {
            if (l == null) throw Invalido("Lançamento vazio.");
            var natureza = Texto(l.Natureza, "natureza", 15)!;
            if (!Natureza.Todas.Contains(natureza)) throw Invalido("Natureza do lançamento desconhecida.");
            var sentido = SentidoDe(natureza, l.Sentido);
            var valor = Dinheiro(l.Valor, "valor");
            if (valor <= 0) throw Invalido("O valor do lançamento deve ser maior que zero.");
            if (l.Parcelas < 1 || l.Parcelas > MaxParcelas) throw Invalido($"Parcelas entre 1 e {MaxParcelas}.");

            ContaInfo? conta = null;
            if (l.ContaId is { } contaId)
            {
                if (!contas.TryGetValue(contaId, out conta)) throw new GestaoException("NAO_ENCONTRADO", "Conta não encontrada nesta loja.", 404);
                if (!conta.Ativo) throw Invalido("Conta inativa.");
            }

            var status = StatusDe(natureza, sentido, l.Status, l.DataPagamento);
            var mesReferencia = Mes(l.MesReferencia ?? diaFato);
            DateOnly? pagamento = StatusLancamento.Liquidado(status) ? l.DataPagamento ?? diaFato : null;
            if (pagamento is { } p && p > diaFato.AddDays(1) && natureza != Natureza.Transferencia)
                throw Invalido("Pagamento já feito não pode ter data futura; use a pagar com vencimento.");
            if (StatusLancamento.Liquidado(status) && conta == null) throw Invalido("Diga em que conta o dinheiro entrou ou saiu.");

            if (natureza == Natureza.Transferencia)
            {
                if (conta == null) throw Invalido("Transferência precisa das duas contas.");
                if (l.Parcelas != 1) throw Invalido("Transferência não é parcelada.");
            }
            if (l.Parcelas > 1 && (StatusLancamento.Liquidado(status) || natureza is not (Natureza.Receita or Natureza.Despesa)))
                throw Invalido("Só receita ou despesa a pagar/a receber pode ser parcelada.");

            var parcelas = Parcelar(valor, l.Parcelas);
            for (var i = 0; i < parcelas.Length; i++)
            {
                DateOnly? vencimento;
                DateOnly? fatura = null;
                if (conta?.Tipo == TipoConta.CartaoEmpresa)
                {
                    // Cartao da empresa nao tira dinheiro na hora: vai para a fatura, que vence depois.
                    if (natureza != Natureza.Despesa) throw Invalido("No cartão da empresa só entra despesa.");
                    if (status != StatusLancamento.APagar) throw Invalido("Compra no cartão da empresa vai para a fatura: fica a pagar.");
                    (fatura, vencimento) = Fatura(diaFato, conta.DiaFechamento!.Value, conta.DiaVencimento!.Value, i);
                }
                else
                {
                    vencimento = l.DataVencimento is { } v ? v.AddMonths(i)
                        : l.Parcelas > 1 ? diaFato.AddMonths(i + 1) : null;
                }
                plano.Add(new LancamentoPlano(plano.Count + 1, conta?.Id, natureza, sentido, parcelas[i], status, mesReferencia,
                    vencimento, pagamento, i + 1, parcelas.Length, fatura, Opcional(l.Categoria, 60), Opcional(l.Finalidade, 60),
                    Opcional(l.Descricao, 300), null));
            }
        }

        if (plano.Count > MaxLinhas) throw Invalido($"No máximo {MaxLinhas} lançamentos por acontecimento.");
        var transferencias = plano.Where(p => p.Natureza == Natureza.Transferencia).ToList();
        if (transferencias.Count > 0)
        {
            // Transferencia nao e receita nem despesa: so move dinheiro de uma conta para outra e fecha em zero.
            if (transferencias.Count != plano.Count) throw Invalido("Transferência não se mistura com receita ou despesa.");
            var entradas = transferencias.Where(t => t.Sentido == Sentido.Entrada).ToList();
            var saidas = transferencias.Where(t => t.Sentido == Sentido.Saida).ToList();
            if (entradas.Count == 0 || saidas.Count == 0) throw Invalido("Transferência precisa de uma conta de saída e uma de entrada.");
            if (entradas.Sum(t => t.Valor) != saidas.Sum(t => t.Valor)) throw Invalido("O que sai e o que entra na transferência precisam bater.");
            if (entradas.Select(t => t.ContaId).Intersect(saidas.Select(t => t.ContaId)).Any())
                throw Invalido("Transferência entre contas diferentes.");
        }
        return plano;
    }

    private static List<MovimentoPlano> PlanejarMovimentos(List<MovimentoRequest> pedidos, DateOnly diaFato,
        bool confirmacaoHumana, int? responsavelId)
    {
        if (pedidos.Count == 0) return new();
        // Estoque nunca muda sem confirmacao humana: vinculo reconhecido nao e entrada confirmada.
        if (!confirmacaoHumana || responsavelId is null)
            throw new GestaoException("CONFIRMACAO_HUMANA", "Estoque só muda com a confirmação de uma pessoa.", 422);
        if (pedidos.Count > MaxLinhas) throw Invalido($"No máximo {MaxLinhas} movimentos por acontecimento.");
        var plano = new List<MovimentoPlano>();
        foreach (var m in pedidos)
        {
            if (m == null) throw Invalido("Movimento vazio.");
            if (m.ItemId == Guid.Empty) throw Invalido("Informe o item do estoque.");
            var tipo = Texto(m.Tipo, "tipo do movimento", 12)!;
            if (!TipoMovimento.Publicaveis.Contains(tipo)) throw Invalido("Tipo de movimento desconhecido.");
            var unidade = Texto(m.Unidade, "unidade", 2)!;
            if (unidade is not ("g" or "ml" or "un")) throw Invalido("Unidade do estoque é g, ml ou un.");
            var quantidade = Quantidade(m.Quantidade);
            if (quantidade == 0) throw Invalido("Quantidade não pode ser zero.");
            if (tipo == TipoMovimento.Entrada && quantidade < 0) throw Invalido("Entrada tem quantidade positiva.");
            if (tipo is TipoMovimento.Saida or TipoMovimento.Perda && quantidade > 0) throw Invalido("Saída e perda têm quantidade negativa.");
            var motivo = Opcional(m.Motivo, 300);
            if (tipo is TipoMovimento.Perda or TipoMovimento.Divergencia && motivo == null)
                throw Invalido("Perda e divergência precisam de motivo.");
            plano.Add(new MovimentoPlano(plano.Count + 1, m.ItemId, Texto(m.ItemNome, "nome do item", 120)!, quantidade, unidade,
                tipo, motivo, Mes(diaFato), null));
        }
        return plano;
    }

    private static string SentidoDe(string natureza, string? informado) => natureza switch
    {
        Natureza.Receita or Natureza.Aporte => Confere(Sentido.Entrada, informado),
        Natureza.Despesa or Natureza.Retirada => Confere(Sentido.Saida, informado),
        _ => informado is Sentido.Entrada or Sentido.Saida ? informado : throw Invalido("Transferência: diga se a linha é entrada ou saída."),
    };

    private static string Confere(string fixo, string? informado) =>
        informado == null || informado == fixo ? fixo : throw Invalido($"Essa natureza é sempre {fixo}.");

    /// <summary>Status inicial. Sem status: com data de pagamento ja aconteceu; sem, fica a pagar/a receber.</summary>
    public static string StatusDe(string natureza, string sentido, string? informado, DateOnly? dataPagamento)
    {
        var liquidado = sentido == Sentido.Entrada ? StatusLancamento.Recebido : StatusLancamento.Pago;
        var pendente = sentido == Sentido.Entrada ? StatusLancamento.AReceber : StatusLancamento.APagar;
        if (natureza == Natureza.Transferencia)
            return informado == null || informado == liquidado ? liquidado : throw Invalido("Transferência acontece na hora.");
        if (informado == null) return dataPagamento.HasValue ? liquidado : pendente;
        if (informado != StatusLancamento.Previsto && informado != liquidado && informado != pendente)
            throw Invalido($"Status {informado} não combina com {sentido}.");
        if (informado != liquidado && dataPagamento.HasValue) throw Invalido("Data de pagamento só para o que já foi pago ou recebido.");
        return informado;
    }

    /// <summary>
    /// Fatura do cartao da empresa para uma compra no dia <paramref name="compra"/>. Compra no dia do fechamento ou
    /// depois vai para a fatura seguinte. <paramref name="deslocamento"/> = parcelas seguintes (uma fatura por mes).
    /// O mes da fatura e o mes em que ela vence.
    /// </summary>
    public static (DateOnly FaturaMes, DateOnly Vencimento) Fatura(DateOnly compra, int diaFechamento, int diaVencimento,
        int deslocamento = 0)
    {
        if (diaFechamento is < 1 or > 31 || diaVencimento is < 1 or > 31) throw Invalido("Dias do cartão entre 1 e 31.");
        var mesFechamento = Mes(compra);
        if (compra.Day >= Math.Min(diaFechamento, DateTime.DaysInMonth(compra.Year, compra.Month)))
            mesFechamento = mesFechamento.AddMonths(1);
        var mesVencimento = (diaVencimento > diaFechamento ? mesFechamento : mesFechamento.AddMonths(1)).AddMonths(deslocamento);
        var dia = Math.Min(diaVencimento, DateTime.DaysInMonth(mesVencimento.Year, mesVencimento.Month));
        return (mesVencimento, new DateOnly(mesVencimento.Year, mesVencimento.Month, dia));
    }

    /// <summary>Divide em parcelas de centavos exatos; a diferenca de arredondamento fica na primeira.</summary>
    public static decimal[] Parcelar(decimal total, int parcelas)
    {
        if (parcelas < 1) throw Invalido("Parcelas deve ser ao menos 1.");
        var baseParcela = Math.Floor(total / parcelas * 100m) / 100m;
        var resultado = Enumerable.Repeat(baseParcela, parcelas).ToArray();
        resultado[0] += total - baseParcela * parcelas;
        return resultado;
    }

    // ---------------------------------------------------------------- mes fechado

    /// <summary>Mes fechado so aceita correcao (estorno/ajuste). Qualquer outro registro nele e recusado.</summary>
    public static void ValidarMesesFechados(AcontecimentoPlano plano, IReadOnlySet<DateOnly> fechados)
    {
        if (plano.Correcao != null) return;
        var mes = plano.MesesTocados().Where(fechados.Contains).OrderBy(m => m).FirstOrDefault();
        if (mes != default)
            throw new GestaoException("MES_FECHADO",
                $"{MesTexto(mes)} está fechado: só aceita ajuste ou estorno, com motivo.", 409);
    }

    // ---------------------------------------------------------------- correcoes

    /// <summary>
    /// Estorno: anula o efeito liquido do acontecimento (ja somadas correcoes anteriores) com linhas de sinal oposto
    /// ligadas as originais. Nada do original muda. Cada linha fica no mes de referencia da original.
    /// </summary>
    public static AcontecimentoPlano PlanejarEstorno(AcontecimentoAtual original, IReadOnlyList<LancamentoAtual> lancamentos,
        IReadOnlyList<MovimentoAtual> movimentos, IReadOnlyList<LiquidacaoAtual> liquidacoes, EstornoRequest? r,
        int? responsavelId, TimeZoneInfo fuso, DateTimeOffset agora)
    {
        if (r == null) throw Invalido("Envie o estorno.");
        var chave = Chave(r.ChaveIdempotencia);
        var motivo = Motivo(r.Motivo);
        if (responsavelId is null) throw new GestaoException("SEM_RESPONSAVEL", "Correção precisa de um responsável.", 403);
        if (original.Correcao != null) throw Invalido("Correção não se estorna: faça um ajuste no lançamento original.");
        if (original.Tipo == TipoAcontecimento.FechamentoMes) throw Invalido("Fechamento de mês é definitivo.");
        if (original.Estornado) throw new GestaoException("JA_ESTORNADO", "Este acontecimento já foi estornado.", 409);
        var dataFato = r.DataFato ?? agora;
        var dia = DiaLocal(dataFato, fuso);

        var linhas = new List<LancamentoPlano>();
        foreach (var l in lancamentos.Where(l => l.AcontecimentoId == original.Id && l.CorrigeLancamentoId == null).OrderBy(l => l.Linha))
        {
            var liquido = l.Valor + lancamentos.Where(c => c.CorrigeLancamentoId == l.Id).Sum(c => c.Valor);
            if (liquido == 0) continue;
            linhas.Add(LinhaDeCorrecao(l, -liquido, linhas.Count + 1, dia));
        }
        var movs = new List<MovimentoPlano>();
        foreach (var m in movimentos.Where(m => m.AcontecimentoId == original.Id && m.CorrigeMovimentoId == null).OrderBy(m => m.Linha))
        {
            var liquido = m.Quantidade + movimentos.Where(c => c.CorrigeMovimentoId == m.Id).Sum(c => c.Quantidade);
            if (liquido == 0) continue;
            movs.Add(new MovimentoPlano(movs.Count + 1, m.ItemId, m.ItemNome, -liquido, m.Unidade, TipoMovimento.Estorno, motivo,
                m.MesReferencia, m.Id));
        }
        if (movs.Count > 0 && !r.ConfirmacaoHumana)
            throw new GestaoException("CONFIRMACAO_HUMANA", "O estorno mexe no estoque: confirme antes.", 422);
        var liqs = new List<LiquidacaoPlano>();
        foreach (var q in liquidacoes.Where(q => q.AcontecimentoId == original.Id && q.CorrigeLiquidacaoId == null))
        {
            var liquido = q.Valor + liquidacoes.Where(c => c.CorrigeLiquidacaoId == q.Id).Sum(c => c.Valor);
            if (liquido == 0) continue;
            liqs.Add(new LiquidacaoPlano(q.LancamentoId, q.ContaId, -liquido, dia, q.Id));
        }
        if (linhas.Count + movs.Count + liqs.Count == 0) throw Invalido("Não há nada a estornar: o efeito já está zerado.");

        var valorAnterior = linhas.Count > 0 ? -linhas.Sum(l => l.Valor) : liqs.Count > 0 ? -liqs.Sum(q => q.Valor) : (decimal?)null;
        var hash = Hash(new { tipo = "estorno", original = original.Id, motivo, dataFato = dataFato.ToUniversalTime(), r.ConfirmacaoHumana });
        return new AcontecimentoPlano(chave, hash, TipoAcontecimento.Estorno, original.Modulo, dataFato, Mes(dia), responsavelId,
            null, null, null, $"Estorno: {motivo}", "{}", original.Id, Correcao.Estorno, motivo, valorAnterior,
            valorAnterior.HasValue ? 0m : null, linhas, movs, liqs);
    }

    /// <summary>Ajuste de valor: grava a diferenca (novo - atual) ligada a linha original, com anterior e novo.</summary>
    public static AcontecimentoPlano PlanejarAjusteLancamento(AcontecimentoAtual original, LancamentoAtual alvo,
        IReadOnlyList<LancamentoAtual> correcoesDoAlvo, AjusteRequest r, int? responsavelId, TimeZoneInfo fuso, DateTimeOffset agora)
    {
        var (chave, motivo, dataFato, dia) = BaseAjuste(original, r, responsavelId, fuso, agora);
        if (alvo.CorrigeLancamentoId != null) throw Invalido("Ajuste sempre o lançamento original, não uma correção.");
        if (alvo.Natureza == Natureza.Transferencia) throw Invalido("Transferência errada se estorna e se registra de novo.");
        var novo = Dinheiro(r.ValorNovo ?? throw Invalido("Informe o valor novo."), "valor novo");
        if (novo < 0) throw Invalido("Valor novo não pode ser negativo.");
        var atual = alvo.Valor + correcoesDoAlvo.Sum(c => c.Valor);
        if (novo == atual) throw Invalido("O valor novo é igual ao atual.");
        var linha = LinhaDeCorrecao(alvo, novo - atual, 1, dia);
        var hash = Hash(new { tipo = "ajuste", alvo = alvo.Id, novo, motivo, dataFato = dataFato.ToUniversalTime() });
        return new AcontecimentoPlano(chave, hash, TipoAcontecimento.Ajuste, original.Modulo, dataFato, Mes(dia), responsavelId,
            null, null, null, $"Ajuste: {motivo}", "{}", original.Id, Correcao.Ajuste, motivo, atual, novo,
            new[] { linha }, Array.Empty<MovimentoPlano>(), Array.Empty<LiquidacaoPlano>());
    }

    public static AcontecimentoPlano PlanejarAjusteMovimento(AcontecimentoAtual original, MovimentoAtual alvo,
        IReadOnlyList<MovimentoAtual> correcoesDoAlvo, AjusteRequest r, int? responsavelId, TimeZoneInfo fuso, DateTimeOffset agora)
    {
        var (chave, motivo, dataFato, dia) = BaseAjuste(original, r, responsavelId, fuso, agora);
        if (!r.ConfirmacaoHumana) throw new GestaoException("CONFIRMACAO_HUMANA", "Ajuste de estoque precisa da confirmação de uma pessoa.", 422);
        if (alvo.CorrigeMovimentoId != null) throw Invalido("Ajuste sempre o movimento original, não uma correção.");
        var nova = Quantidade(r.QuantidadeNova ?? throw Invalido("Informe a quantidade nova."));
        var atual = alvo.Quantidade + correcoesDoAlvo.Sum(c => c.Quantidade);
        if (nova == atual) throw Invalido("A quantidade nova é igual à atual.");
        var mov = new MovimentoPlano(1, alvo.ItemId, alvo.ItemNome, nova - atual, alvo.Unidade, TipoMovimento.Ajuste, motivo,
            alvo.MesReferencia, alvo.Id);
        var hash = Hash(new { tipo = "ajuste", alvo = alvo.Id, nova, motivo, dataFato = dataFato.ToUniversalTime() });
        return new AcontecimentoPlano(chave, hash, TipoAcontecimento.Ajuste, original.Modulo, dataFato, Mes(dia), responsavelId,
            null, null, null, $"Ajuste: {motivo}", "{}", original.Id, Correcao.Ajuste, motivo, atual, nova,
            Array.Empty<LancamentoPlano>(), new[] { mov }, Array.Empty<LiquidacaoPlano>());
    }

    private static (string Chave, string Motivo, DateTimeOffset DataFato, DateOnly Dia) BaseAjuste(AcontecimentoAtual original,
        AjusteRequest? r, int? responsavelId, TimeZoneInfo fuso, DateTimeOffset agora)
    {
        if (r == null) throw Invalido("Envie o ajuste.");
        var chave = Chave(r.ChaveIdempotencia);
        var motivo = Motivo(r.Motivo);
        if (responsavelId is null) throw new GestaoException("SEM_RESPONSAVEL", "Correção precisa de um responsável.", 403);
        if (original.Estornado) throw new GestaoException("JA_ESTORNADO", "Acontecimento estornado não recebe ajuste.", 409);
        if (original.Tipo == TipoAcontecimento.FechamentoMes) throw Invalido("Fechamento de mês é definitivo.");
        var dataFato = r.DataFato ?? agora;
        return (chave, motivo, dataFato, DiaLocal(dataFato, fuso));
    }

    /// <summary>
    /// Linha de correcao de um lancamento: mesmo sentido, conta e mes de referencia. Se o original ja foi pago/recebido,
    /// a correcao tambem mexe no dinheiro (na data da correcao); se ainda esta pendente, so muda o que falta pagar.
    /// </summary>
    private static LancamentoPlano LinhaDeCorrecao(LancamentoAtual l, decimal valor, int linha, DateOnly dia)
    {
        var liquidado = StatusLancamento.Liquidado(l.Status);
        return new LancamentoPlano(linha, l.ContaId, l.Natureza, l.Sentido, valor, l.Status, l.MesReferencia,
            liquidado ? null : l.DataVencimento, liquidado ? dia : null, l.Parcela, l.Parcelas, l.FaturaMes, l.Categoria,
            l.Finalidade, l.Descricao, l.Id);
    }

    // ---------------------------------------------------------------- liquidacao e fechamento

    /// <summary>
    /// Pagamento/recebimento de um lancamento a pagar/a receber, pelo valor liquido (original + correcoes pendentes).
    /// Sai ou entra no banco ou no caixa; cartao e recebivel se acertam por transferencia.
    /// </summary>
    public static AcontecimentoPlano PlanejarLiquidacao(LancamentoAtual alvo, IReadOnlyList<LancamentoAtual> correcoesDoAlvo,
        ContaInfo conta, ContaInfo? contaDoAlvo, AcontecimentoAtual original, LiquidarRequest? r, int? responsavelId,
        TimeZoneInfo fuso, DateTimeOffset agora)
    {
        if (r == null) throw Invalido("Envie o pagamento.");
        var chave = Chave(r.ChaveIdempotencia);
        if (original.Estornado) throw new GestaoException("JA_ESTORNADO", "Acontecimento estornado não se paga.", 409);
        if (contaDoAlvo?.Tipo == TipoConta.CartaoEmpresa)
            throw Invalido("Compra no cartão da empresa se paga pela fatura inteira.");
        var data = DataPagamento(r.DataPagamento, conta, fuso, agora);
        var liqs = Quitar(alvo, correcoesDoAlvo, conta, data, original).ToList();
        var verbo = alvo.Sentido == Sentido.Entrada ? "Recebido" : "Pago";
        var hash = Hash(new { tipo = "pagamento", alvo = alvo.Id, conta = conta.Id, data, responsavelId });
        return new AcontecimentoPlano(chave, hash, TipoAcontecimento.Pagamento, ModuloGestao.Financeiro, agora,
            Mes(DiaLocal(agora, fuso)), responsavelId, "lancamento", alvo.Id.ToString(), null,
            Opcional(r.Descricao, 300) ?? $"{verbo}: {alvo.Descricao ?? alvo.Natureza}", "{}", null, null, null, null,
            liqs.Sum(q => q.Valor), Array.Empty<LancamentoPlano>(), Array.Empty<MovimentoPlano>(), liqs);
    }

    /// <summary>
    /// Pagamento da fatura do cartao da empresa: quita, de uma vez, tudo o que esta a pagar naquela fatura, saindo do
    /// banco ou do caixa. Cada compra deixa de estar a pagar so aqui (o cartao nao tira dinheiro na hora da compra).
    /// </summary>
    public static AcontecimentoPlano PlanejarPagamentoFatura(ContaInfo cartao, DateOnly faturaMes,
        IReadOnlyList<(LancamentoAtual Alvo, IReadOnlyList<LancamentoAtual> Correcoes, AcontecimentoAtual Original)> itens,
        ContaInfo conta, LiquidarRequest? r, int? responsavelId, TimeZoneInfo fuso, DateTimeOffset agora)
    {
        if (r == null) throw Invalido("Envie o pagamento da fatura.");
        var chave = Chave(r.ChaveIdempotencia);
        if (cartao.Tipo != TipoConta.CartaoEmpresa) throw Invalido("Escolha um cartão da empresa.");
        var data = DataPagamento(r.DataPagamento, conta, fuso, agora);
        var pendentes = itens.Where(i => !i.Original.Estornado && i.Alvo.Liquidado == 0
            && i.Alvo.Valor + i.Correcoes.Sum(c => c.Valor) > 0).ToList();
        if (pendentes.Count == 0) throw Invalido($"A fatura de {MesTexto(faturaMes)} não tem nada a pagar.");
        var liqs = pendentes.SelectMany(i => Quitar(i.Alvo, i.Correcoes, conta, data, i.Original)).ToList();
        var hash = Hash(new { tipo = "pagamento_fatura", cartao = cartao.Id, faturaMes, conta = conta.Id, data, responsavelId });
        return new AcontecimentoPlano(chave, hash, TipoAcontecimento.Pagamento, ModuloGestao.Financeiro, agora,
            Mes(DiaLocal(agora, fuso)), responsavelId, "fatura", $"{cartao.Id}:{faturaMes:yyyy-MM}", null,
            Opcional(r.Descricao, 300) ?? $"Fatura {MesTexto(faturaMes)} paga", "{}", null, null, null, null, liqs.Sum(q => q.Valor),
            Array.Empty<LancamentoPlano>(), Array.Empty<MovimentoPlano>(), liqs);
    }

    private static DateOnly DataPagamento(DateOnly? informada, ContaInfo conta, TimeZoneInfo fuso, DateTimeOffset agora)
    {
        if (!conta.Ativo) throw Invalido("Conta inativa.");
        if (conta.Tipo is not (TipoConta.Banco or TipoConta.Caixa))
            throw Invalido("Pagamento sai ou entra no banco ou no caixa.");
        var hoje = DiaLocal(agora, fuso);
        var data = informada ?? hoje;
        return data > hoje ? throw Invalido("Pagamento não pode ter data futura.") : data;
    }

    /// <summary>Liquidacoes que quitam o original pelo valor liquido: o original e cada correcao ainda pendente.</summary>
    private static IEnumerable<LiquidacaoPlano> Quitar(LancamentoAtual alvo, IReadOnlyList<LancamentoAtual> correcoes,
        ContaInfo conta, DateOnly data, AcontecimentoAtual original)
    {
        if (original.Estornado) throw new GestaoException("JA_ESTORNADO", "Acontecimento estornado não se paga.", 409);
        if (alvo.CorrigeLancamentoId != null) throw Invalido("Pague o lançamento original; as correções vão junto.");
        if (alvo.Natureza == Natureza.Transferencia || StatusLancamento.Liquidado(alvo.StatusRegistro))
            throw Invalido("Este lançamento já nasceu pago ou recebido.");
        if (alvo.Liquidado != 0) throw new GestaoException("DUPLICADO", "Este lançamento já foi pago ou recebido.", 409);
        var pendentes = correcoes.Where(c => c.Liquidado == 0 && !StatusLancamento.Liquidado(c.StatusRegistro)).ToList();
        if (alvo.Valor + pendentes.Sum(c => c.Valor) <= 0) throw Invalido("Não sobrou nada a pagar neste lançamento.");
        yield return new LiquidacaoPlano(alvo.Id, conta.Id, alvo.Valor, data, null);
        foreach (var c in pendentes) yield return new LiquidacaoPlano(c.Id, conta.Id, c.Valor, data, null);
    }

    /// <summary>Fecha um mes que ja terminou. Depois disso ele so aceita correcao.</summary>
    public static AcontecimentoPlano PlanejarFechamento(DateOnly mes, FecharMesRequest? r, int? responsavelId,
        TimeZoneInfo fuso, DateTimeOffset agora)
    {
        var chave = Chave(r?.ChaveIdempotencia);
        if (responsavelId is null) throw new GestaoException("SEM_RESPONSAVEL", "Fechamento precisa de um responsável.", 403);
        if (mes.Day != 1) throw Invalido("Informe o mês (primeiro dia).");
        var atual = Mes(DiaLocal(agora, fuso));
        if (mes >= atual) throw Invalido("Só dá para fechar um mês que já terminou.");
        var hash = Hash(new { tipo = "fechamento_mes", mes, responsavelId });
        return new AcontecimentoPlano(chave, hash, TipoAcontecimento.FechamentoMes, ModuloGestao.Financeiro, agora, atual,
            responsavelId, "mes", mes.ToString("yyyy-MM", CultureInfo.InvariantCulture), null, $"Fechamento de {MesTexto(mes)}",
            "{}", null, null, null, null, null, Array.Empty<LancamentoPlano>(), Array.Empty<MovimentoPlano>(),
            Array.Empty<LiquidacaoPlano>());
    }

    // ---------------------------------------------------------------- utilitarios

    public static string Hash(object conteudo)
    {
        var json = JsonSerializer.Serialize(conteudo);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }

    /// <summary>Documento normalizado (sem espacos, pontos e tracos; maiusculas) para nao entrar duas vezes por formatacao.</summary>
    public static string? Documento(string? documento)
    {
        if (string.IsNullOrWhiteSpace(documento)) return null;
        var limpo = new string(documento.Where(c => !char.IsWhiteSpace(c) && c is not ('.' or '-' or '/')).ToArray()).ToUpperInvariant();
        if (limpo.Length > 200) throw Invalido("Identificação do documento muito longa.");
        return limpo.Length == 0 ? null : limpo;
    }

    private static string Chave(string? chave)
    {
        var valor = chave?.Trim();
        if (string.IsNullOrEmpty(valor)) throw Invalido("Informe a chave de idempotência.");
        if (valor.Length > 160) throw Invalido("Chave de idempotência muito longa.");
        return valor;
    }

    private static string Motivo(string? motivo)
    {
        var valor = motivo?.Trim();
        if (valor == null || valor.Length < 3) throw Invalido("Diga o motivo da correção.");
        if (valor.Length > 500) throw Invalido("Motivo muito longo.");
        return valor;
    }

    private static (string?, string?) Origem(OrigemRef? origem)
    {
        if (origem == null || (string.IsNullOrWhiteSpace(origem.Tipo) && string.IsNullOrWhiteSpace(origem.Id))) return (null, null);
        var tipo = Texto(origem.Tipo, "tipo da origem", 40)!;
        if (!Slug.IsMatch(tipo)) throw Invalido("Tipo da origem deve ter só letras minúsculas e _.");
        return (tipo, Texto(origem.Id, "id da origem", 120));
    }

    private static string Dados(JsonElement? dados)
    {
        if (dados is not { } d || d.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return "{}";
        if (d.ValueKind != JsonValueKind.Object) throw Invalido("Dados deve ser um objeto.");
        var texto = d.GetRawText();
        if (Encoding.UTF8.GetByteCount(texto) > MaxDadosBytes) throw Invalido("Dados grandes demais.");
        return texto;
    }

    private static decimal Dinheiro(decimal valor, string campo)
    {
        if (decimal.Round(valor, 2) != valor) throw Invalido($"{campo}: no máximo 2 casas decimais.");
        if (Math.Abs(valor) >= 1_000_000_000m) throw Invalido($"{campo} grande demais.");
        return valor;
    }

    private static decimal Quantidade(decimal valor)
    {
        if (decimal.Round(valor, 4) != valor) throw Invalido("Quantidade: no máximo 4 casas decimais.");
        if (Math.Abs(valor) >= 100_000_000m) throw Invalido("Quantidade grande demais.");
        return valor;
    }

    private static string? Texto(string? valor, string campo, int max)
    {
        var texto = valor?.Trim();
        if (string.IsNullOrEmpty(texto)) throw Invalido($"Informe {campo}.");
        if (texto.Length > max) throw Invalido($"{campo}: no máximo {max} caracteres.");
        return texto;
    }

    private static string? Opcional(string? valor, int max)
    {
        var texto = valor?.Trim();
        if (string.IsNullOrEmpty(texto)) return null;
        return texto.Length > max ? throw Invalido($"Texto com mais de {max} caracteres.") : texto;
    }

    private static GestaoException Invalido(string mensagem) => new("CAMPO_INVALIDO", mensagem, 422);
}
