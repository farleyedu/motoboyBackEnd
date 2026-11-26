using System;
using System.Collections.Generic;
using System.Linq;

namespace APIBack.Payments.Assinaturas
{
    public static class Periodicidades
    {
        public const string Mensal = "mensal";
        public const string Anual = "anual";

        public static bool Valida(string? p) => p is Mensal or Anual;
        public static int Meses(string p) => p == Anual ? 12 : 1;
        /// <summary>Quanto o preço representa por mês (receita recorrente): o anual é dividido por 12.</summary>
        public static decimal PorMes(decimal preco, string p) => p == Anual ? Proporcional.Arredondar(preco / 12) : preco;
    }

    public static class StatusAssinatura
    {
        public const string Pendente = "pendente";
        public const string Ativa = "ativa";
        public const string Encerrada = "encerrada";
    }

    /// <summary>Estado financeiro, separado da situacao comercial (estabelecimentos.situacao_comercial). Só informa: nada trava a loja.</summary>
    public static class EstadoFinanceiro
    {
        public const string EmDia = "em_dia";
        public const string EmAtraso = "em_atraso";
    }

    public static class TiposCobranca
    {
        public const string Ciclo = "ciclo";
        public const string Upgrade = "upgrade";
        public const string CompraExtra = "compra_extra";
    }

    public static class StatusCobrancaComercial
    {
        public const string Aberta = "aberta";
        public const string Processando = "processando";
        public const string Paga = "paga";
        public const string Cancelada = "cancelada";

        public static bool EmAberto(string s) => s is Aberta or Processando;
    }

    /// <summary>Por que uma cobrança foi cancelada (comercial_cobranca.cancelamento_motivo).</summary>
    public static class MotivosCancelamentoCobranca
    {
        public const string Expirada = "expirada";
        public const string Substituida = "substituida";
        public const string PlanoTrocado = "plano_trocado";
        public const string TrocaParaAnual = "troca_para_anual";
        public const string AssinaturaCancelada = "assinatura_cancelada";
        /// <summary>A dívida passou para a cobrança de quem voltou a assinar (agrupada_em).</summary>
        public const string Agrupada = "agrupada";
        public const string ProximaFaturaPaga = "proxima_fatura_paga";
        /// <summary>A Gestão dispensou a cobrança: o período fica sem cobrança.</summary>
        public const string Gestao = "gestao";
    }

    /// <summary>Datas da loja no fuso de Sao Paulo: o ciclo vira a meia-noite local, nao em UTC.</summary>
    public static class FusoComercial
    {
        private static readonly TimeZoneInfo Zona = Encontrar();

        private static TimeZoneInfo Encontrar()
        {
            foreach (var id in new[] { "America/Sao_Paulo", "E. South America Standard Time" })
            {
                try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
                catch (TimeZoneNotFoundException) { }
                catch (InvalidTimeZoneException) { }
            }
            return TimeZoneInfo.CreateCustomTimeZone("BRT", TimeSpan.FromHours(-3), "BRT", "BRT");
        }

        public static DateOnly Data(DateTimeOffset instante) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instante, Zona).DateTime);

        /// <summary>Meia-noite local do dia, como instante.</summary>
        public static DateTimeOffset InicioDoDia(DateOnly dia)
        {
            var local = dia.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
            return new DateTimeOffset(local, Zona.GetUtcOffset(local)).ToUniversalTime();
        }

        /// <summary>Meia-noite local de hoje: o que venceu antes disto está atrasado.</summary>
        public static DateTimeOffset InicioDeHoje(DateTimeOffset agora) => InicioDoDia(Data(agora));
    }

    /// <summary>
    /// Ciclos: mensal = um mes-calendario, anual = 12 meses. Sempre calculado a partir da ancora (nunca encadeado), entao o dia
    /// volta ao original: 31/01 -> 28/02 (29/02 no bissexto) -> 31/03; anual de 29/02/2028 -> 28/02/2029 -> ... -> 29/02/2032.
    /// </summary>
    public static class CicloDatas
    {
        public static DateOnly Somar(DateOnly ancora, int meses)
        {
            var totalMeses = ancora.Year * 12 + (ancora.Month - 1) + meses;
            var ano = totalMeses / 12;
            var mes = totalMeses % 12 + 1;
            return new DateOnly(ano, mes, Math.Min(ancora.Day, DateTime.DaysInMonth(ano, mes)));
        }

        /// <summary>Ciclo numero n (1 = primeiro): [inicio, fim).</summary>
        public static (DateOnly Inicio, DateOnly Fim) Ciclo(DateOnly ancora, int numero, string periodicidade)
        {
            if (numero < 1) throw new ArgumentOutOfRangeException(nameof(numero));
            var meses = Periodicidades.Meses(periodicidade);
            return (Somar(ancora, (numero - 1) * meses), Somar(ancora, numero * meses));
        }

        public static int Dias(DateOnly inicio, DateOnly fim) => fim.DayNumber - inicio.DayNumber;
    }

    public static class TiposMudanca
    {
        public const string Upgrade = "upgrade";
        public const string Downgrade = "downgrade";
        /// <summary>Mensal para anual: comeca um ciclo anual hoje, com o que sobrou do mensal descontado.</summary>
        public const string ParaAnual = "para_anual";
        public const string Nenhuma = "nenhuma";
    }

    public sealed record PrevisaoMudanca(
        string Tipo,
        decimal PrecoAtual,
        decimal PrecoNovo,
        int DiasRestantes,
        int DiasCiclo,
        decimal Credito,
        decimal ValorAgora,
        /// <summary>"agora" (depois do pagamento) ou "fim_do_periodo".</summary>
        string Aplicacao,
        DateOnly AplicaEm,
        IReadOnlyList<string> Explicacao);

    /// <summary>Calculo da troca de plano. Tudo em dias inteiros do fuso da loja; dinheiro arredondado para centavos (meio para cima).</summary>
    public static class Proporcional
    {
        /// <summary>Abaixo disto nao vale gerar Pix: a troca entra sem cobranca e a diferenca e registrada.</summary>
        public const decimal ValorMinimoCobranca = 1.00m;

        public static decimal Arredondar(decimal v) => decimal.Round(v, 2, MidpointRounding.AwayFromZero);

        /// <param name="precoMensalNovo">Preço mensal do plano novo, só quando é outro plano: se for menor que o atual,
        /// a ida para o anual é redução e espera o fim do mês pago.</param>
        /// <param name="cicloPago">Falso quando o ciclo atual ainda não foi pago: não há crédito a descontar no anual.</param>
        /// <param name="proximoPago">Preço da próxima fatura já paga, se houver: entra na conta da troca que vale agora.</param>
        public static PrevisaoMudanca Calcular(decimal precoAtual, string periodicidadeAtual, decimal precoNovo, string periodicidadeNova,
            DateOnly inicioCiclo, DateOnly fimCiclo, DateOnly hoje, bool mesmoPlano, decimal? precoMensalNovo = null, bool cicloPago = true,
            decimal? proximoPago = null)
        {
            var diasCiclo = Math.Max(1, CicloDatas.Dias(inicioCiclo, fimCiclo));
            var restantes = Math.Clamp(CicloDatas.Dias(hoje, fimCiclo), 0, diasCiclo);
            var credito = cicloPago ? Arredondar(precoAtual * restantes / diasCiclo) : 0;
            var explicacao = new List<string>();

            if (mesmoPlano && periodicidadeAtual == periodicidadeNova)
                return new PrevisaoMudanca(TiposMudanca.Nenhuma, precoAtual, precoNovo, restantes, diasCiclo, 0, 0, "agora", hoje, explicacao);

            var paraAnual = periodicidadeAtual == Periodicidades.Mensal && periodicidadeNova == Periodicidades.Anual;
            var anualMenor = paraAnual && !mesmoPlano && precoMensalNovo.HasValue && precoMensalNovo.Value < precoAtual;

            if (paraAnual && !anualMenor)
            {
                var total = credito + (proximoPago ?? 0);
                var valor = Math.Max(0, Arredondar(precoNovo - total));
                explicacao.Add($"O plano anual custa {Brl(precoNovo)} e começa no pagamento.");
                if (cicloPago)
                    explicacao.Add($"Sobram {restantes} de {diasCiclo} dias do mês atual: {Brl(precoAtual)} x {restantes}/{diasCiclo} = {Brl(credito)} de crédito.");
                if (proximoPago > 0) explicacao.Add($"A próxima fatura já paga também é descontada: {Brl(proximoPago.Value)}.");
                explicacao.Add($"Você paga agora {Brl(precoNovo)} - {Brl(total)} = {Brl(valor)}.");
                return new PrevisaoMudanca(TiposMudanca.ParaAnual, precoAtual, precoNovo, restantes, diasCiclo, total, valor, "agora", hoje, explicacao);
            }

            // Mais barato, ou anual para mensal: vale no fim do periodo ja pago, sem apagar nada.
            if (anualMenor || (precoNovo < precoAtual && periodicidadeAtual == periodicidadeNova)
                || (periodicidadeAtual == Periodicidades.Anual && periodicidadeNova == Periodicidades.Mensal))
            {
                explicacao.Add($"A troca vale a partir de {fimCiclo:dd/MM/yyyy}, quando termina o período atual.");
                explicacao.Add($"Até lá nada muda. Depois, o plano passa a custar {Brl(precoNovo)}. Seus dados continuam guardados.");
                return new PrevisaoMudanca(TiposMudanca.Downgrade, precoAtual, precoNovo, restantes, diasCiclo, 0, 0, "fim_do_periodo", fimCiclo, explicacao);
            }

            var diferenca = Arredondar((precoNovo - precoAtual) * restantes / diasCiclo);
            explicacao.Add($"Diferença do período: {Brl(precoNovo)} - {Brl(precoAtual)} = {Brl(precoNovo - precoAtual)}.");
            explicacao.Add($"Faltam {restantes} de {diasCiclo} dias: {Brl(precoNovo - precoAtual)} x {restantes}/{diasCiclo} = {Brl(diferenca)}.");
            if (proximoPago.HasValue && precoNovo > proximoPago.Value)
            {
                var doProximo = Arredondar(precoNovo - proximoPago.Value);
                explicacao.Add($"A próxima fatura já foi paga: soma a diferença dela, {Brl(doProximo)}.");
                diferenca = Arredondar(diferenca + doProximo);
            }
            explicacao.Add(diferenca < ValorMinimoCobranca
                ? "O valor é pequeno demais para cobrar: a troca entra agora sem custo."
                : "O plano novo libera assim que o pagamento for aprovado. A data de vencimento continua a mesma.");
            return new PrevisaoMudanca(TiposMudanca.Upgrade, precoAtual, precoNovo, restantes, diasCiclo, 0,
                diferenca < ValorMinimoCobranca ? 0 : diferenca, "agora", hoje, explicacao);
        }

        public static string Brl(decimal v) => "R$ " + v.ToString("N2", new System.Globalization.CultureInfo("pt-BR"));
    }

    /// <summary>Política dos avisos (pagamento_politica). A tolerância continua gravada, mas não suspende nada.</summary>
    public sealed record PoliticaInadimplencia(int ToleranciaDias, IReadOnlyList<int> AvisosAntes, IReadOnlyList<int> AvisosDepois)
    {
        /// <summary>Padrao quando a tabela pagamento_politica (sessao 2) ainda nao existe.</summary>
        public static PoliticaInadimplencia Padrao { get; } = new(0, new[] { 3, 1 }, new[] { 3 });
    }

    public sealed record AvaliacaoInadimplencia(string Financeiro, int DiasAtraso);

    public sealed record AvisoDevido(string Chave, string Tipo, string Mensagem);

    /// <summary>
    /// Devendo = mensalidade vencida e não paga, a partir do dia seguinte ao vencimento. Não existe prazo para suspender:
    /// nada é bloqueado sozinho, só a Gestão bloqueia. Os avisos informam, sem ameaça.
    /// </summary>
    public static class Inadimplencia
    {
        public static AvaliacaoInadimplencia Avaliar(DateOnly? vencimentoMaisAntigo, DateOnly hoje)
        {
            if (vencimentoMaisAntigo is not { } venc || hoje <= venc) return new AvaliacaoInadimplencia(EstadoFinanceiro.EmDia, 0);
            return new AvaliacaoInadimplencia(EstadoFinanceiro.EmAtraso, hoje.DayNumber - venc.DayNumber);
        }

        /// <summary>Avisos que ja deveriam ter saido para uma cobranca (o banco garante um por chave; repetir nao duplica).</summary>
        public static IReadOnlyList<AvisoDevido> AvisosDevidos(Guid cobrancaId, string descricao, decimal valor, DateOnly vencimento, DateOnly hoje,
            PoliticaInadimplencia politica)
        {
            var lista = new List<AvisoDevido>();
            var id = cobrancaId.ToString("N");
            var valorTxt = Proporcional.Brl(valor);
            foreach (var d in politica.AvisosAntes.Where(d => d > 0).Distinct())
                if (hoje >= vencimento.AddDays(-d) && hoje < vencimento)
                    lista.Add(new($"{id}:antes:{d}", "antes_vencimento", $"{descricao} ({valorTxt}) vence em {vencimento:dd/MM}."));
            if (hoje >= vencimento)
                lista.Add(new($"{id}:vencimento", "vencimento",
                    hoje == vencimento ? $"{descricao} ({valorTxt}) vence hoje." : $"{descricao} ({valorTxt}) venceu em {vencimento:dd/MM}."));
            foreach (var d in politica.AvisosDepois.Where(d => d > 0).Distinct())
                if (hoje >= vencimento.AddDays(d))
                    lista.Add(new($"{id}:atraso:{d}", "atraso", $"{descricao} ({valorTxt}) está em atraso há {d} {(d == 1 ? "dia" : "dias")}."));
            return lista;
        }
    }
}
