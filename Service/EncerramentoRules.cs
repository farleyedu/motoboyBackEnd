using System;
using APIBack.Model.Enum;

namespace APIBack.Service
{
    /// <summary>
    /// Regras (puras, testaveis) do encerramento automatico de pedidos em aberto.
    ///
    /// O pedido que sobrou do expediente anterior e encerrado (status EncerradoAuto) um numero de
    /// horas depois do fechamento do estabelecimento; o atendente tambem pode encerrar o
    /// expediente na hora. Fica gravado com qual motoboy o pedido estava (pedido_encerramento).
    /// </summary>
    public static class EncerramentoRules
    {
        /// <summary>Horas depois do fechamento em que o sistema encerra o que sobrou.</summary>
        public const int HorasPadrao = 4;
        public const int HorasMaximo = 24;

        public const string MotivoExpediente = "expediente";
        public const string MotivoManual = "manual";

        public const string OrigemBalcao = "balcao";
        public const string OrigemEntregaManual = "entrega_manual";

        /// <summary>Pedido em aberto: o que o encerramento alcanca. Rascunho, entregue e cancelado ficam fora.</summary>
        public static bool IsOpen(StatusPedido status) =>
            status is StatusPedido.Pendente or StatusPedido.Atribuido or StatusPedido.EmRota or StatusPedido.AguardandoMotoboy;

        /// <summary>So o pedido encerrado automaticamente pode ser reaberto por aqui.</summary>
        public static bool CanReopen(StatusPedido status) => status == StatusPedido.EncerradoAuto;

        /// <summary>
        /// Sem motoboy, pendente so pode ser encerrado como retirada no balcao; em rota so como entrega manual.
        /// A origem e derivada do estado persistido para o cliente nao conseguir registrar uma entrega de motoboy inexistente.
        /// </summary>
        public static string OrigemConclusaoSemMotoboy(StatusPedido status, string? origemInformada)
        {
            var esperada = status switch
            {
                StatusPedido.Pendente => OrigemBalcao,
                StatusPedido.EmRota => OrigemEntregaManual,
                _ => throw new DeliveryDomainException(409, "PEDIDO_NAO_ENTREGAVEL", "Este pedido nao pode ser concluido sem motoboy.")
            };
            var informada = origemInformada?.Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(informada) && informada != esperada)
                throw new DeliveryDomainException(422, "ORIGEM_CONCLUSAO_INVALIDA", "A origem da conclusao nao corresponde ao estado do pedido.");
            return esperada;
        }

        public static string MotivoConclusaoSemMotoboy(string origem, string? motivo)
        {
            var informado = motivo?.Trim();
            if (!string.IsNullOrEmpty(informado)) return informado.Length > 200 ? informado[..200] : informado;
            return origem == OrigemBalcao
                ? "Retirada no balcao confirmada pelo operador."
                : "Entrega manual confirmada pelo operador.";
        }

        public static bool IsValidHoras(int horas) => horas is >= 0 and <= HorasMaximo;

        /// <summary>
        /// Instante (UTC) do ultimo fechamento que ja passou das <paramref name="horasApos"/> horas de
        /// tolerancia, ou null se nenhum dos ultimos dias tem horario de fechamento. Tudo que foi feito
        /// ate esse instante e do expediente anterior; o que veio depois pertence ao expediente atual.
        /// </summary>
        /// <param name="fechaAs">Hora de fechamento (local) de um dia; null = fechado ou sem horario cadastrado.</param>
        public static DateTimeOffset? UltimoFechamentoElegivel(
            Func<DateOnly, TimeSpan?> fechaAs, DateTimeOffset agoraUtc, TimeZoneInfo tz, int horasApos, int diasParaTras = 3)
        {
            var hoje = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(agoraUtc, tz).DateTime);
            for (var recuo = 0; recuo <= diasParaTras; recuo++)
            {
                var dia = hoje.AddDays(-recuo);
                var fecha = fechaAs(dia);
                if (fecha is null) continue;

                var local = dia.ToDateTime(TimeOnly.FromTimeSpan(fecha.Value), DateTimeKind.Unspecified);
                // Hora que nao existe (salto do horario de verao): ignora o dia em vez de chutar.
                if (tz.IsInvalidTime(local)) continue;

                var fechamento = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, tz), TimeSpan.Zero);
                if (fechamento.AddHours(horasApos) <= agoraUtc) return fechamento;
            }
            return null;
        }

        /// <summary>O pedido e do expediente que ja fechou: foi feito ate o instante do fechamento.</summary>
        public static bool PertenceAoExpedienteEncerrado(DateTimeOffset? feitoEmUtc, DateTimeOffset fechamentoUtc) =>
            feitoEmUtc is not null && feitoEmUtc.Value <= fechamentoUtc;

        public static TimeZoneInfo ResolveTimeZone(string? timezoneIana)
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(string.IsNullOrWhiteSpace(timezoneIana) ? "America/Sao_Paulo" : timezoneIana);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                return TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");
            }
        }

        /// <summary>Texto do motivo gravado na parada cancelada e no historico.</summary>
        public static string DescricaoMotivo(string motivo) => motivo == MotivoManual
            ? "Expediente encerrado pelo atendente."
            : "Encerrado automaticamente apos o fechamento do expediente.";
    }

    /// <summary>Parametros do encerramento automatico de um estabelecimento.</summary>
    public sealed class EncerramentoSettings
    {
        public bool Ativo { get; set; } = true;
        public int Horas { get; set; } = EncerramentoRules.HorasPadrao;
    }

    /// <summary>Pedido em aberto e quando foi feito (null = sem horario confiavel; nesse caso nao e encerrado).</summary>
    public sealed class EncerramentoCandidate
    {
        public int Id { get; set; }
        public DateTimeOffset? FeitoEmUtc { get; set; }
    }

    /// <summary>Resultado de encerrar o expediente (automatico ou manual).</summary>
    public sealed class EncerramentoResultDto
    {
        public int Encerrados { get; set; }
        public int Falhas { get; set; }
    }
}
