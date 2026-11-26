using System;
using System.Collections.Generic;
using System.Linq;

namespace APIBack.Payments.Assinaturas
{
    /// <summary>Em que pé está a assinatura da loja (campo "estado" do contrato).</summary>
    public static class EstadosAssinatura
    {
        public const string SemAssinatura = "sem_assinatura";
        public const string Teste = "teste";
        public const string AguardandoPagamento = "aguardando_pagamento";
        public const string EmDia = "em_dia";
        public const string Devendo = "devendo";
        public const string CanceladaVigente = "cancelada_vigente";
        public const string Encerrada = "encerrada";
    }

    public static class MesesVigentes
    {
        public const string Pago = "pago";
        public const string EmAberto = "em_aberto";
        public const string Atrasado = "atrasado";
        public const string SemCobranca = "sem_cobranca";
    }

    /// <summary>Mensalidades vencidas (e a dívida somada na volta de quem reassinou). Upgrade e extra nunca entram.</summary>
    public sealed record DividaDto(decimal Valor, DateTimeOffset Desde, IReadOnlyList<Guid> Cobrancas);

    public sealed record AcaoDto(bool Pode, string? Motivo, string? Codigo);

    /// <summary>O que a loja pode fazer agora. As telas mostram; a mesma regra recusa no servidor.</summary>
    public sealed record AcoesAssinaturaDto(AcaoDto Assinar, AcaoDto Pagar, AcaoDto Upgrade, AcaoDto Downgrade, AcaoDto TrocaParaMensal, AcaoDto Cancelar,
        AcaoDto Retomar, AcaoDto DesfazerTroca, AcaoDto ComprarMotoboys, AcaoDto ComprarModulo, AcaoDto CartaoAutomatico);

    public sealed record SituacaoAssinatura(string Estado, string MesVigente, AcoesAssinaturaDto Acoes);

    /// <summary>Uma mensalidade em aberto, só com o que a conta da dívida precisa.</summary>
    public sealed record MensalidadeAberta(Guid Id, decimal Valor, DateTimeOffset Vencimento, decimal DividaValor, DateTimeOffset? DividaDesde);

    public static class Divida
    {
        /// <summary>
        /// Devendo: mensalidade vencida e não paga, a partir do dia seguinte ao vencimento. A cobrança de quem voltou a assinar deve
        /// só a parte antiga (divida_valor) desde a data antiga: o primeiro período novo não é dívida antes de ser pago.
        /// </summary>
        public static DividaDto? De(IEnumerable<MensalidadeAberta> abertas, DateTimeOffset agora)
        {
            var hoje = FusoComercial.InicioDeHoje(agora);
            var itens = new List<(Guid Id, decimal Valor, DateTimeOffset Desde)>();
            foreach (var c in abertas)
            {
                var somada = c.DividaValor > 0;
                var desde = somada ? c.DividaDesde ?? c.Vencimento : c.Vencimento;
                if (desde < hoje) itens.Add((c.Id, somada ? c.DividaValor : c.Valor, desde));
            }
            return itens.Count == 0 ? null
                : new DividaDto(itens.Sum(i => i.Valor), itens.Min(i => i.Desde), itens.OrderBy(i => i.Desde).Select(i => i.Id).ToList());
        }
    }

    /// <summary>Tudo o que a regra precisa saber de uma loja. Sem banco, para testar cada linha das tabelas.</summary>
    public sealed record FatosAssinatura
    {
        public string? SituacaoLoja { get; init; }
        public DateTimeOffset? TesteTerminaEm { get; init; }
        /// <summary>Status da assinatura viva (pendente|ativa) ou da última encerrada. Nulo = nunca assinou.</summary>
        public string? Status { get; init; }
        public string Periodicidade { get; init; } = Periodicidades.Mensal;
        public string? EncerramentoMotivo { get; init; }
        public bool Cancelada { get; init; }
        public bool TrocaMarcada { get; init; }
        /// <summary>A próxima fatura já foi paga com o plano marcado: não há mais o que desfazer antes dela começar.</summary>
        public bool ProximaPagaComATroca { get; init; }
        /// <summary>Há um ano já pago à frente (mensal passando para anual).</summary>
        public bool AnualPagoAFrente { get; init; }
        public DividaDto? Divida { get; init; }
        public bool TemCobrancaAPagar { get; init; }
        /// <summary>Situação da cobrança do período atual: "aberta", "paga" ou nulo (sem cobrança: cortesia ou dispensada).</summary>
        public string? CobrancaDoPeriodo { get; init; }
    }

    /// <summary>As duas tabelas de "Regras da assinatura": o que cada situação pode fazer.</summary>
    public static class RegrasDeAcesso
    {
        public const string StatusPeriodoPago = "paga";
        public const string StatusPeriodoAberto = "aberta";

        public static SituacaoAssinatura Avaliar(FatosAssinatura f, DateTimeOffset agora)
        {
            var viva = f.Status is StatusAssinatura.Pendente or StatusAssinatura.Ativa;
            var ativa = f.Status == StatusAssinatura.Ativa;
            var devendo = f.Divida != null;
            var emTeste = f.SituacaoLoja == Service.SituacoesComerciais.Teste && f.TesteTerminaEm > agora;

            var estado = devendo ? EstadosAssinatura.Devendo
                : viva && f.Cancelada ? EstadosAssinatura.CanceladaVigente
                : ativa ? EstadosAssinatura.EmDia
                : emTeste ? EstadosAssinatura.Teste
                : viva ? EstadosAssinatura.AguardandoPagamento
                : f.Status == StatusAssinatura.Encerrada && f.EncerramentoMotivo != Encerramentos.AntesDoPagamento ? EstadosAssinatura.Encerrada
                : EstadosAssinatura.SemAssinatura;

            var mes = devendo ? MesesVigentes.Atrasado
                : f.CobrancaDoPeriodo == StatusPeriodoAberto ? MesesVigentes.EmAberto
                : f.CobrancaDoPeriodo == StatusPeriodoPago && viva ? MesesVigentes.Pago
                : MesesVigentes.SemCobranca;

            return new SituacaoAssinatura(estado, mes, new AcoesAssinaturaDto(
                Assinar: Acao(Assinar(f)), Pagar: Acao(Pagar(f)), Upgrade: Acao(Trocar(f)), Downgrade: Acao(Trocar(f)),
                TrocaParaMensal: Acao(TrocarParaMensal(f)), Cancelar: Acao(Cancelar(f)), Retomar: Acao(Retomar(f)), DesfazerTroca: Acao(Desfazer(f)),
                ComprarMotoboys: Acao(Extra(f, modulo: false)), ComprarModulo: Acao(Extra(f, modulo: true)), CartaoAutomatico: Acao(Cartao(f))));
        }

        private static AcaoDto Acao(Recusa? r) => r == null ? new AcaoDto(true, null, null) : new AcaoDto(false, r.Motivo, r.Codigo);

        // Cada regra devolve nulo quando pode. O serviço usa as mesmas para recusar.

        public static Recusa? Assinar(FatosAssinatura f) =>
            Viva(f) ? new(AssinaturaCodigos.JaExiste, "A loja já tem uma assinatura.") : null;

        public static Recusa? Pagar(FatosAssinatura f) =>
            f.TemCobrancaAPagar ? null : new(AssinaturaCodigos.SemCobranca, "Não há fatura para pagar agora.");

        /// <summary>Upgrade e downgrade.</summary>
        public static Recusa? Trocar(FatosAssinatura f)
        {
            if (!Viva(f)) return SemAssinatura();
            if (f.Cancelada) return CanceladaRetome();
            return f.Divida != null ? Devendo(f.Divida, "Quite o que está em aberto para trocar de plano.") : null;
        }

        public static Recusa? TrocarParaMensal(FatosAssinatura f)
        {
            if (!Viva(f)) return SemAssinatura();
            if (f.Cancelada) return CanceladaRetome();
            if (AnualValendo(f)) return AnualSoGestao();
            if (f.Divida != null) return Devendo(f.Divida, "Quite o que está em aberto para trocar de plano.");
            return f.Periodicidade == Periodicidades.Mensal ? new(AssinaturaCodigos.SemMudanca, "Seu plano já é mensal.") : null;
        }

        public static bool AnualValendo(FatosAssinatura f) =>
            f.Status == StatusAssinatura.Ativa && (f.Periodicidade == Periodicidades.Anual || f.AnualPagoAFrente);

        public static Recusa? Cancelar(FatosAssinatura f) =>
            !Viva(f) ? SemAssinatura() : f.Cancelada ? new(AssinaturaCodigos.SemMudanca, "A assinatura já está cancelada.") : null;

        public static Recusa? Retomar(FatosAssinatura f)
        {
            if (!Viva(f)) return SemAssinatura();
            if (!f.Cancelada) return new(AssinaturaCodigos.SemMudanca, "A assinatura não está cancelada.");
            return f.Divida != null ? Devendo(f.Divida, "Quite o que está em aberto para retomar.") : null;
        }

        public static Recusa? Desfazer(FatosAssinatura f)
        {
            if (!Viva(f)) return SemAssinatura();
            if (f.Cancelada) return CanceladaRetome();
            if (!f.TrocaMarcada) return new(AssinaturaCodigos.SemMudanca, "Não há troca marcada.");
            if (f.Divida != null) return Devendo(f.Divida, "Quite o que está em aberto para desfazer a troca.");
            return f.ProximaPagaComATroca
                ? new(AssinaturaCodigos.SemMudanca, "A próxima fatura já foi paga com o plano novo. Quando ele começar, faça a troca de volta.") : null;
        }

        /// <summary>Extra só com plano ativo. Devendo, a loja ainda compra motoboys, mas não módulos.</summary>
        public static Recusa? Extra(FatosAssinatura f, bool modulo)
        {
            if (f.Status != StatusAssinatura.Ativa) return new(AssinaturaCodigos.ExtraSemPlano, "Para comprar extras, a loja precisa de um plano ativo.");
            return modulo && f.Divida != null ? new(AssinaturaCodigos.ModuloDevendo, "Quite o que está em aberto para comprar módulos.") : null;
        }

        /// <summary>Ligar ou trocar o cartão automático (desligar é sempre permitido).</summary>
        public static Recusa? Cartao(FatosAssinatura f)
        {
            if (!Viva(f)) return SemAssinatura();
            if (f.Cancelada) return CanceladaRetome();
            return f.Divida != null ? Devendo(f.Divida, "Quite o que está em aberto antes de ligar o cartão automático.") : null;
        }

        private static bool Viva(FatosAssinatura f) => f.Status is StatusAssinatura.Pendente or StatusAssinatura.Ativa;
        private static Recusa SemAssinatura() => new(AssinaturaCodigos.SemAssinatura, "Assine um plano primeiro.");
        private static Recusa CanceladaRetome() => new(AssinaturaCodigos.CanceladaRetome, "A assinatura está cancelada. Retome antes.");
        public static Recusa AnualSoGestao() => new(AssinaturaCodigos.AnualSoGestao, "Para passar do anual para o mensal, fale com a Zippy.");

        private static Recusa Devendo(DividaDto d, string motivo) => new(AssinaturaCodigos.Devendo, motivo);
    }

    public sealed record Recusa(string Codigo, string Motivo);

    /// <summary>assinatura.encerramento_motivo.</summary>
    public static class Encerramentos
    {
        public const string FimDoPeriodo = "fim_do_periodo";
        /// <summary>Desistiu (ou a Gestão encerrou) antes de qualquer pagamento: nunca chegou a valer.</summary>
        public const string AntesDoPagamento = "cancelada_antes_do_pagamento";
        public const string PelaGestao = "encerrada_pela_gestao";
        public const string CobrancaParada = "cobranca_parada";
    }
}
