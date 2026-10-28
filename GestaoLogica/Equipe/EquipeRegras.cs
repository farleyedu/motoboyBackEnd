using System.Globalization;
using System.Text.RegularExpressions;
using APIBack.Acontecimentos;

namespace APIBack.GestaoLogica.Equipe;

/// <summary>
/// Regras puras da Equipe: jornada, remuneração, escala recorrente, plano do livro e divisão do pagamento.
/// O cálculo é o mesmo do Painel (equipeRegras.ts), mais comissão e adicionais do turno.
/// </summary>
public static partial class EquipeRegras
{
    // Janelas noturnas (22:00–05:00) sobre dois dias seguidos, em minutos desde 00:00 do dia da entrada.
    private static readonly (int Ini, int Fim)[] Noite = { (0, 300), (1320, 1740), (2760, 3180) };

    public static int? Minutos(string? hhmm)
    {
        if (hhmm is null || HoraRegex().Match(hhmm.Trim()) is not { Success: true } m) return null;
        var h = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var mi = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        return h > 23 || mi > 59 ? null : h * 60 + mi;
    }

    /// <summary>Duração em minutos. Saída menor que a entrada passa da meia-noite (17:00 → 00:00 = 7 h). Iguais ou inválidas = 0.</summary>
    public static int DuracaoMin(string entrada, string saida)
    {
        if (Minutos(entrada) is not { } a || Minutos(saida) is not { } b || a == b) return 0;
        return b > a ? b - a : b + 1440 - a;
    }

    public static int MinutosNoturnos(string entrada, string saida)
    {
        var dur = DuracaoMin(entrada, saida);
        if (dur == 0 || Minutos(entrada) is not { } ini) return 0;
        var fim = ini + dur;
        return Noite.Sum(n => Math.Max(0, Math.Min(fim, n.Fim) - Math.Max(ini, n.Ini)));
    }

    public static decimal Horas(string entrada, string saida) => Arred(DuracaoMin(entrada, saida) / 60m);

    /// <summary>
    /// Quanto o turno rende. Cancelado = 0. Falta sem justificativa = 0 (zera a diária); com atestado paga a base do turno, sem
    /// entregas, comissão nem adicionais. Previsto e realizado calculam igual; entregas, comissão e adicionais só no realizado.
    /// </summary>
    public static Remuneracao Calcular(RegraPagamento regra, JornadaTurno t)
    {
        var horas = Horas(t.Entrada, t.Saida);
        var baseItens = new List<ItemRemuneracao>();
        if (t.PagamentoAvulso is { } av)
        {
            if (av.Forma == "hora") baseItens.Add(new($"{Q(horas)} h × {Moeda(av.Valor)}", Arred(horas * av.Valor)));
            else if (av.Forma != "entrega") baseItens.Add(new(av.Forma == "diaria" ? "Diária" : "Turno", Arred(av.Valor)));
        }
        else
        {
            if (regra.SalarioMensal is decimal s && s > 0) baseItens.Add(new("Salário proporcional (1/30)", Arred(s / 30)));
            if (regra.Diaria is decimal d && d > 0) baseItens.Add(new("Diária", Arred(d)));
            if (regra.PorTurno is decimal pt && pt > 0) baseItens.Add(new("Turno", Arred(pt)));
            if (regra.PorHora is decimal ph && ph > 0) baseItens.Add(new($"{Q(horas)} h × {Moeda(ph)}", Arred(horas * ph)));
            if (regra.AdicionalNoturno is decimal an && an > 0 && MinutosNoturnos(t.Entrada, t.Saida) >= 60)
                baseItens.Add(new("Adicional noturno", Arred(an)));
        }
        var previsto = Soma(baseItens);
        if (t.Status == StatusTurno.Cancelado) return new(0, previsto, Array.Empty<ItemRemuneracao>());
        if (t.Status == StatusTurno.Falta)
            return t.Justificada ? new(previsto, previsto, baseItens) : new(0, previsto, Array.Empty<ItemRemuneracao>());
        var itens = new List<ItemRemuneracao>(baseItens);
        if (t.Status == StatusTurno.Realizado)
        {
            var porEntrega = t.PagamentoAvulso is { Forma: "entrega" } e ? e.Valor : t.PagamentoAvulso is not null ? 0 : regra.PorEntrega ?? 0;
            if (t.Entregas > 0 && porEntrega > 0)
                itens.Add(new($"{t.Entregas} entregas × {Moeda(porEntrega)}", Arred(t.Entregas * porEntrega)));
            if (regra.ComissaoPercentual is decimal pc && pc > 0 && t.BaseComissao > 0 && t.PagamentoAvulso is null)
                itens.Add(new($"Comissão {Q(pc)}% de {Moeda(t.BaseComissao)}", Arred(t.BaseComissao * pc / 100)));
            itens.AddRange(t.Adicionais.Where(a => a.Valor > 0).Select(a => new ItemRemuneracao(a.Rotulo, Arred(a.Valor))));
        }
        return new(Soma(itens), previsto, itens);
    }

    /// <summary>Campos que cada tipo de vínculo usa (o resto é descartado). Igual ao Painel, mais comissão no fixo e no freelancer.</summary>
    public static RegraPagamento Normalizar(string vinculo, RegraPagamento? regra)
    {
        regra ??= new RegraPagamento();
        static decimal? V(decimal? x) => x is > 0 ? Dinheiro(x.Value) : null;
        static decimal? P(decimal? x) => x is > 0 and <= 100 ? Math.Round(x.Value, 2) : null;
        var r = vinculo switch
        {
            VinculoPessoa.Fixo => new RegraPagamento(SalarioMensal: V(regra.SalarioMensal), AdicionalNoturno: V(regra.AdicionalNoturno),
                ComissaoPercentual: P(regra.ComissaoPercentual)),
            VinculoPessoa.Diarista => new RegraPagamento(Diaria: V(regra.Diaria)),
            VinculoPessoa.Freelancer => new RegraPagamento(PorTurno: V(regra.PorTurno), ComissaoPercentual: P(regra.ComissaoPercentual)),
            VinculoPessoa.Hora => new RegraPagamento(PorHora: V(regra.PorHora)),
            VinculoPessoa.Motoboy => new RegraPagamento(Diaria: V(regra.Diaria), PorEntrega: V(regra.PorEntrega)),
            VinculoPessoa.Prestador => new RegraPagamento(PorTurno: V(regra.PorTurno)),
            _ => throw Invalido("Tipo de vínculo deve ser fixo, diarista, freelancer, hora, motoboy ou prestador."),
        };
        var principal = vinculo switch
        {
            VinculoPessoa.Fixo => r.SalarioMensal, VinculoPessoa.Diarista => r.Diaria, VinculoPessoa.Hora => r.PorHora,
            VinculoPessoa.Motoboy => r.Diaria ?? r.PorEntrega, _ => r.PorTurno,
        };
        return principal is null ? throw Invalido("Informe o valor que a pessoa recebe.") : r;
    }

    public static IReadOnlyList<DiaPadrao> NormalizarEscala(IReadOnlyList<DiaPadrao>? escala)
    {
        var lista = (escala ?? Array.Empty<DiaPadrao>()).ToList();
        if (lista.Any(d => d.Dow is < 0 or > 6 || Minutos(d.Entrada) is null || Minutos(d.Saida) is null || DuracaoMin(d.Entrada, d.Saida) == 0))
            throw Invalido("Escala: dia da semana de 0 a 6 e horários HH:MM diferentes.");
        if (lista.GroupBy(d => d.Dow).Any(g => g.Count() > 1)) throw Invalido("Escala: um turno por dia da semana.");
        return lista.OrderBy(d => d.Dow).ToList();
    }

    public static string Chave(Guid pessoaId, DateOnly data) => $"{pessoaId}:{data:yyyy-MM-dd}";

    public static int DiaDaSemana(DateOnly d) => (int)d.DayOfWeek;

    /// <summary>Turno da escala recorrente para o dia (nulo se a pessoa não trabalha nesse dia, está inativa ou começou depois).</summary>
    public static DiaPadrao? DaEscala(PessoaDto p, DateOnly dia) =>
        !p.Ativo || dia < p.Desde ? null : p.EscalaPadrao.FirstOrDefault(x => x.Dow == DiaDaSemana(dia));

    /// <summary>Só realizado, ou falta justificada, vira despesa no livro.</summary>
    public static bool GeraDespesa(string status, bool justificada, decimal valor) =>
        valor > 0 && (status == StatusTurno.Realizado || (status == StatusTurno.Falta && justificada));

    public enum Plano { Nada, Publicar, Ajustar, Estornar }

    /// <summary>O que fazer no livro quando o turno muda (o mesmo de planoDoLivro do Painel).</summary>
    public static Plano PlanoDoLivro(bool temRegistro, decimal valorAntes, string status, bool justificada, decimal valor)
    {
        var gera = GeraDespesa(status, justificada, valor);
        if (!temRegistro) return gera ? Plano.Publicar : Plano.Nada;
        if (!gera) return Plano.Estornar;
        return valor != valorAntes ? Plano.Ajustar : Plano.Nada;
    }

    public static TotaisSemana Totais(IEnumerable<TurnoDto?> turnos)
    {
        decimal previsto = 0, apurado = 0;
        int n = 0, realizados = 0, faltas = 0;
        foreach (var t in turnos)
        {
            if (t is null || t.Status == StatusTurno.Cancelado) continue;
            n++;
            if (t.Status == StatusTurno.Falta) { faltas++; previsto += t.Previsto; continue; }
            previsto += t.Valor;
            if (t.Status == StatusTurno.Realizado) { apurado += t.Valor; realizados++; }
        }
        return new TotaisSemana(Arred(previsto), Arred(apurado), n, realizados, faltas);
    }

    /// <summary>
    /// Divide um pagamento pelos itens a pagar, do mais antigo ao mais novo. Item que cabe inteiro é liquidado; o que não cabe
    /// é ajustado para a parte paga e o resto vira saldo (decisão 4 da Lógica: o livro não tem liquidação parcial).
    /// </summary>
    public static (IReadOnlyList<(Guid Id, decimal Valor)> Inteiros, (Guid Id, decimal Pago, decimal Resto)? Parcial) Dividir(
        IReadOnlyList<ItemDevido> aPagar, decimal valor)
    {
        if (valor <= 0) throw Invalido("Valor do pagamento deve ser maior que zero.");
        var restante = Dinheiro(valor);
        var inteiros = new List<(Guid, decimal)>();
        foreach (var i in aPagar.Where(i => i.Valor > 0).OrderBy(i => i.Data).ThenBy(i => i.LancamentoId))
        {
            if (restante <= 0) break;
            if (i.Valor <= restante) { inteiros.Add((i.LancamentoId, i.Valor)); restante -= i.Valor; continue; }
            return (inteiros, (i.LancamentoId, restante, i.Valor - restante));
        }
        if (restante > 0) throw Invalido($"O valor passa do que está a pagar em {Moeda(restante)}.");
        return (inteiros, null);
    }

    /// <summary>Quanto de cada adiantamento aberto abate de cada item a pagar (mais antigos primeiro, dos dois lados).</summary>
    public static IReadOnlyList<(Guid AdiantamentoId, Guid LancamentoId, decimal Valor)> Compensar(
        IReadOnlyList<AdiantamentoDto> adiantamentos, IReadOnlyList<ItemDevido> aPagar)
    {
        var saldoItem = aPagar.Where(i => i.Valor > 0).OrderBy(i => i.Data).ThenBy(i => i.LancamentoId)
            .Select(i => (i.LancamentoId, Resto: i.Valor)).ToList();
        var r = new List<(Guid, Guid, decimal)>();
        foreach (var a in adiantamentos.Where(a => a.Aberto > 0).OrderBy(a => a.Data).ThenBy(a => a.AcontecimentoId))
        {
            var aberto = a.Aberto;
            for (var k = 0; k < saldoItem.Count && aberto > 0; k++)
            {
                var x = Math.Min(aberto, saldoItem[k].Resto);
                if (x <= 0) continue;
                r.Add((a.AcontecimentoId, saldoItem[k].LancamentoId, x));
                saldoItem[k] = (saldoItem[k].LancamentoId, saldoItem[k].Resto - x);
                aberto -= x;
            }
        }
        return r;
    }

    public static decimal Arred(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    private static decimal Soma(IEnumerable<ItemRemuneracao> itens) => Arred(itens.Sum(i => i.Valor));

    private static decimal Dinheiro(decimal v) =>
        decimal.Round(v, 2) != v ? throw Invalido("Valores em dinheiro têm no máximo 2 casas.") : v;

    private static string Moeda(decimal v) => v.ToString("C2", CultureInfo.GetCultureInfo("pt-BR"));

    private static string Q(decimal v) => v.ToString("0.##", CultureInfo.GetCultureInfo("pt-BR"));

    private static GestaoException Invalido(string m) => new("CAMPO_INVALIDO", m, 422);

    [GeneratedRegex(@"^(\d{1,2}):(\d{2})$")]
    private static partial Regex HoraRegex();
}
