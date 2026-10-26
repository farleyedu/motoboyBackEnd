using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Http;

namespace APIBack.Payments.Assinaturas
{
    public static class Periodicidades
    {
        public const string Mensal = "mensal";
        public const string Anual = "anual";

        public static bool Valida(string? p) => p is Mensal or Anual;
        public static int Meses(string p) => p == Anual ? 12 : 1;
    }

    public static class StatusAssinatura
    {
        public const string Pendente = "pendente";
        public const string Ativa = "ativa";
        public const string Encerrada = "encerrada";
    }

    /// <summary>Estado financeiro, separado da situacao comercial (estabelecimentos.situacao_comercial).</summary>
    public static class EstadoFinanceiro
    {
        public const string EmDia = "em_dia";
        public const string EmAtraso = "em_atraso";
        public const string Suspensa = "suspensa";
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

        public static PrevisaoMudanca Calcular(decimal precoAtual, string periodicidadeAtual, decimal precoNovo, string periodicidadeNova,
            DateOnly inicioCiclo, DateOnly fimCiclo, DateOnly hoje, bool mesmoPlano)
        {
            var diasCiclo = Math.Max(1, CicloDatas.Dias(inicioCiclo, fimCiclo));
            var restantes = Math.Clamp(CicloDatas.Dias(hoje, fimCiclo), 0, diasCiclo);
            var credito = Arredondar(precoAtual * restantes / diasCiclo);
            var explicacao = new List<string>();

            if (mesmoPlano && periodicidadeAtual == periodicidadeNova)
                return new PrevisaoMudanca(TiposMudanca.Nenhuma, precoAtual, precoNovo, restantes, diasCiclo, 0, 0, "agora", hoje, explicacao);

            if (periodicidadeAtual == Periodicidades.Mensal && periodicidadeNova == Periodicidades.Anual)
            {
                var valor = Math.Max(0, Arredondar(precoNovo - credito));
                explicacao.Add($"O plano anual custa {Brl(precoNovo)} e comeca hoje.");
                explicacao.Add($"Sobram {restantes} de {diasCiclo} dias do ciclo atual: {Brl(precoAtual)} x {restantes}/{diasCiclo} = {Brl(credito)} de credito.");
                explicacao.Add($"Voce paga agora {Brl(precoNovo)} - {Brl(credito)} = {Brl(valor)}.");
                return new PrevisaoMudanca(TiposMudanca.ParaAnual, precoAtual, precoNovo, restantes, diasCiclo, credito, valor, "agora", hoje, explicacao);
            }

            // Mais barato, ou anual para mensal: vale no fim do periodo ja pago, sem apagar nada.
            if (precoNovo < precoAtual && periodicidadeAtual == periodicidadeNova || periodicidadeAtual == Periodicidades.Anual && periodicidadeNova == Periodicidades.Mensal)
            {
                explicacao.Add($"A troca vale a partir de {fimCiclo:dd/MM/yyyy}, quando termina o periodo ja pago.");
                explicacao.Add($"Ate la nada muda. Depois, o ciclo passa a custar {Brl(precoNovo)}. Seus dados continuam guardados.");
                return new PrevisaoMudanca(TiposMudanca.Downgrade, precoAtual, precoNovo, restantes, diasCiclo, 0, 0, "fim_do_periodo", fimCiclo, explicacao);
            }

            var diferenca = Arredondar((precoNovo - precoAtual) * restantes / diasCiclo);
            explicacao.Add($"Diferenca do ciclo: {Brl(precoNovo)} - {Brl(precoAtual)} = {Brl(precoNovo - precoAtual)}.");
            explicacao.Add($"Faltam {restantes} de {diasCiclo} dias: {Brl(precoNovo - precoAtual)} x {restantes}/{diasCiclo} = {Brl(diferenca)}.");
            explicacao.Add(diferenca < ValorMinimoCobranca
                ? "O valor e pequeno demais para cobrar: a troca entra agora sem custo."
                : "O plano novo libera assim que o pagamento for aprovado. A data de vencimento continua a mesma.");
            return new PrevisaoMudanca(TiposMudanca.Upgrade, precoAtual, precoNovo, restantes, diasCiclo, 0,
                diferenca < ValorMinimoCobranca ? 0 : diferenca, "agora", hoje, explicacao);
        }

        public static string Brl(decimal v) => "R$ " + v.ToString("N2", new System.Globalization.CultureInfo("pt-BR"));
    }

    public sealed record PoliticaInadimplencia(int ToleranciaDias, IReadOnlyList<int> AvisosAntes, IReadOnlyList<int> AvisosDepois)
    {
        /// <summary>Padrao quando a tabela pagamento_politica (sessao 2) ainda nao existe.</summary>
        public static PoliticaInadimplencia Padrao { get; } = new(7, new[] { 3, 1 }, new[] { 3 });
    }

    public sealed record AvaliacaoInadimplencia(string Financeiro, int DiasAtraso, DateOnly? SuspendeEm);

    public sealed record AvisoDevido(string Chave, string Tipo, string Mensagem);

    /// <summary>
    /// Inadimplencia: no vencimento a loja entra em atraso (tolerancia, padrao 7 dias da pagamento_politica). Passada a tolerancia,
    /// suspensa: so as operacoes novas param. Avisos no vencimento, no 3o dia (e outros da politica) e na vespera do bloqueio.
    /// </summary>
    public static class Inadimplencia
    {
        public static AvaliacaoInadimplencia Avaliar(DateOnly? vencimentoMaisAntigo, DateOnly hoje, int toleranciaDias)
        {
            if (vencimentoMaisAntigo is not { } venc || hoje < venc) return new AvaliacaoInadimplencia(EstadoFinanceiro.EmDia, 0, null);
            var dias = hoje.DayNumber - venc.DayNumber;
            var suspende = venc.AddDays(toleranciaDias);
            return new AvaliacaoInadimplencia(hoje >= suspende ? EstadoFinanceiro.Suspensa : EstadoFinanceiro.EmAtraso, dias, suspende);
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
                lista.Add(new($"{id}:vencimento", "vencimento", $"{descricao} ({valorTxt}) vence hoje. Pague para evitar a suspensao."));
            var bloqueio = vencimento.AddDays(politica.ToleranciaDias);
            foreach (var d in politica.AvisosDepois.Append(3).Where(d => d > 0 && d < politica.ToleranciaDias).Distinct())
                if (hoje >= vencimento.AddDays(d) && hoje < bloqueio)
                    lista.Add(new($"{id}:atraso:{d}", "atraso", $"{descricao} esta {d} dia(s) em atraso. Novas operacoes param em {bloqueio:dd/MM}."));
            if (politica.ToleranciaDias > 0 && hoje >= bloqueio.AddDays(-1) && hoje < bloqueio)
                lista.Add(new($"{id}:antes_bloqueio", "antes_bloqueio", $"Ultimo dia: amanha ({bloqueio:dd/MM}) as operacoes novas sao suspensas ate o pagamento."));
            return lista;
        }
    }

    /// <summary>
    /// O que a loja suspensa por falta de pagamento ainda faz. Para so o que comeca algo novo (POST de criacao);
    /// leitura, historico, pagamento, suporte, Gestao e o motoboy terminando a rota continuam.
    /// </summary>
    public static class AcessoFinanceiroRules
    {
        public const string CodigoSuspensa = "ACESSO_SUSPENSO_FINANCEIRO";

        private static readonly string[] Liberados =
        {
            "/api/v2/comercial", "/api/v2/chamados", "/api/auth", "/api/me", "/api/gestao", "/api/admin", "/hubs", "/api/v2/pagamentos"
        };

        // Avancos de um pedido que ja existe: a entrega iniciada termina.
        private static readonly string[] SufixosDeAndamento =
            { "/saiu", "/entregue", "/concluir", "/cancelar", "/concluir-atual", "/code", "/proof", "/pickup-completion", "/read" };

        public static bool Bloqueia(string? path, string? method, bool liberadoNoBloqueio)
        {
            var m = method ?? string.Empty;
            if (HttpMethods.IsGet(m) || HttpMethods.IsHead(m) || HttpMethods.IsOptions(m)) return false;
            if (liberadoNoBloqueio) return false;
            var p = (path ?? string.Empty).TrimEnd('/').ToLowerInvariant();
            foreach (var prefixo in Liberados)
                if (p == prefixo || p.StartsWith(prefixo + "/", StringComparison.Ordinal)) return false;
            if (p.StartsWith("/api/v2/motoboys/me/session", StringComparison.Ordinal)) return false;
            if (SufixosDeAndamento.Any(s => p.EndsWith(s, StringComparison.Ordinal))) return false;
            // PUT/PATCH/DELETE ajustam o que ja existe; o que para e o POST que cria (pedido novo, despacho, convite...).
            return HttpMethods.IsPost(m);
        }
    }
}
