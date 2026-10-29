using APIBack.Acontecimentos;

namespace APIBack.GestaoLogica.Despesas;

// Contas fixas (recorrências). Espelha ContaFixa do Painel (zippy-admin/src/features/zippy-gestao/despesas/despesasRegras.ts).
// docs/zippy-gestao/CONTRATOS.md, seção 9.8.

public sealed record ContaFixaDto(Guid Id, string Nome, string? Finalidade, decimal ValorPrevisto, int DiaVencimento, bool ValorVaria,
    bool Arquivada, Guid? ContaId, int Versao);

public sealed record ContaFixaRequest(string? Nome, string? Finalidade, decimal? ValorPrevisto, int? DiaVencimento, bool ValorVaria = false,
    Guid? ContaId = null, int? Versao = null, string? Motivo = null);

public static class AcaoContaFixa
{
    public const string Criada = "criada", Editada = "editada", Arquivada = "arquivada", Reativada = "reativada";
}

/// <summary>Uma mudança na conta fixa: antes, depois e o mês em que passa a valer (o que já está no livro não muda).</summary>
public sealed record ContaFixaHistoricoDto(Guid Id, int Versao, string Acao, ContaFixaDto? Antes, ContaFixaDto Depois, DateOnly ValeAPartir,
    string? Motivo, int Por, string? PorNome, DateTimeOffset Em);

public sealed record MotivoRequest(string? Motivo);

public static class EstadoOcorrencia
{
    /// <summary>Já está no livro. <c>AguardandoValor</c> = conta de valor variável ainda sem o valor do mês. <c>Prevista</c> = fixa ainda não gerada.</summary>
    public const string Lancada = "lancada", AguardandoValor = "aguardando_valor", Prevista = "prevista";
}

/// <summary>A conta fixa num mês: lançada (com o acontecimento) ou ainda prevista/aguardando valor.</summary>
public sealed record OcorrenciaDto(Guid ContaFixaId, string Nome, DateOnly Mes, DateOnly Vencimento, decimal Valor, string Estado,
    Guid? AcontecimentoId, string? Status);

public sealed record LancarOcorrenciaRequest(string? ChaveIdempotencia, DateOnly Mes, decimal Valor, DateOnly? Vencimento = null);

public sealed record LembreteDto(Guid ContaFixaId, string Nome, DateOnly Mes, DateOnly Vencimento, decimal Valor, string Estado, bool Vencida,
    int DiasParaVencer, Guid? LancamentoId);

public sealed record GeracaoDto(DateOnly Mes, IReadOnlyList<OcorrenciaDto> Geradas, IReadOnlyList<OcorrenciaDto> AguardandoValor);

/// <summary>Regras puras da recorrência: vencimento no mês, estado da ocorrência e quem entra nos lembretes.</summary>
public static class RecorrenciaRegras
{
    public static DateOnly Mes(DateOnly d) => new(d.Year, d.Month, 1);

    /// <summary>"Todo dia 31" em fevereiro vence no último dia do mês.</summary>
    public static DateOnly Vencimento(DateOnly mes, int dia) => new(mes.Year, mes.Month, Math.Min(dia, DateTime.DaysInMonth(mes.Year, mes.Month)));

    public static ContaFixaRequest Validar(ContaFixaRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Nome) || r.Nome.Trim().Length < 2) throw Invalido("Dê um nome à conta (ex.: Aluguel do salão).");
        if (r.ValorPrevisto is not > 0 || decimal.Round(r.ValorPrevisto.Value, 2) != r.ValorPrevisto) throw Invalido("Informe o valor previsto (até 2 casas).");
        if (r.DiaVencimento is not (>= 1 and <= 31)) throw Invalido("O dia do vencimento vai de 1 a 31.");
        if (r.Motivo is { Length: > 300 }) throw Invalido("Motivo com até 300 letras.");
        return r with { Nome = r.Nome.Trim(), Finalidade = string.IsNullOrWhiteSpace(r.Finalidade) ? null : r.Finalidade.Trim(),
            Motivo = string.IsNullOrWhiteSpace(r.Motivo) ? null : r.Motivo.Trim() };
    }

    /// <summary>Mudança na conta vale no mês atual se ele ainda não foi lançado; senão, do mês que vem.</summary>
    public static DateOnly ValeAPartir(DateOnly hoje, bool mesAtualLancado) => mesAtualLancado ? Mes(hoje).AddMonths(1) : Mes(hoje);

    /// <summary>Muda algo além da versão? Salvar igual não vira histórico.</summary>
    public static bool Mudou(ContaFixaDto? antes, ContaFixaDto depois) => antes is null || antes with { Versao = 0 } != depois with { Versao = 0 };

    public static string? Motivo(string? m) =>
        string.IsNullOrWhiteSpace(m) ? null : m.Trim().Length > 300 ? throw Invalido("Motivo com até 300 letras.") : m.Trim();

    /// <summary>Gerar só até o mês seguinte: conta de dois meses à frente ainda pode mudar.</summary>
    public static void ValidarMesDeGeracao(DateOnly mes, DateOnly hoje)
    {
        if (Mes(mes) > Mes(hoje).AddMonths(1)) throw Invalido("Gere as contas fixas no máximo do mês que vem.");
    }

    public static string EstadoSemLancamento(bool valorVaria) => valorVaria ? EstadoOcorrencia.AguardandoValor : EstadoOcorrencia.Prevista;

    /// <summary>
    /// Entra no lembrete o que ainda não foi pago: vence em até <paramref name="dias"/> dias ou já venceu. Conta paga ou estornada
    /// não lembra; conta de valor variável sem valor lembra pelo vencimento do mês.
    /// </summary>
    public static IReadOnlyList<LembreteDto> Lembretes(IEnumerable<OcorrenciaDto> ocorrencias, IReadOnlyDictionary<Guid, Guid> lancamentoDoAcontecimento,
        DateOnly hoje, int dias)
    {
        if (dias is < 0 or > 60) throw Invalido("Dias de antecedência entre 0 e 60.");
        return ocorrencias
            .Where(o => o.Estado != EstadoOcorrencia.Lancada || o.Status is StatusLancamento.APagar or StatusLancamento.Previsto)
            .Select(o => (o, faltam: o.Vencimento.DayNumber - hoje.DayNumber))
            .Where(x => x.faltam <= dias)
            .OrderBy(x => x.o.Vencimento).ThenBy(x => x.o.Nome)
            .Select(x => new LembreteDto(x.o.ContaFixaId, x.o.Nome, x.o.Mes, x.o.Vencimento, x.o.Valor, x.o.Estado, x.faltam < 0, x.faltam,
                x.o.AcontecimentoId is { } a && lancamentoDoAcontecimento.TryGetValue(a, out var l) ? l : null))
            .ToList();
    }

    private static GestaoException Invalido(string m) => new("CAMPO_INVALIDO", m, 422);
}
