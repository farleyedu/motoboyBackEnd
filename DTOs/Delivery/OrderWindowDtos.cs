using System.Collections.Generic;

namespace APIBack.DTOs.Delivery
{
    /// <summary>Como a janela de pedidos do mapa e calculada.</summary>
    public static class OrderWindowModes
    {
        /// <summary>Pedidos feitos nas ultimas N horas (campo Hours).</summary>
        public const string LastHours = "last_hours";
        /// <summary>Pedidos feitos desde a meia-noite (horario de Brasilia). E o padrao.</summary>
        public const string OperationalDay = "operational_day";
        /// <summary>Faixas de horario por dia da semana (campo Shifts): vale a faixa em andamento ou a ultima que terminou.</summary>
        public const string Shifts = "shifts";
        /// <summary>Intervalo fixo entre dois horarios (CustomFrom e CustomTo).</summary>
        public const string Custom = "custom";

        public static bool IsValid(string? value) =>
            value is LastHours or OperationalDay or Shifts or Custom;
    }

    /// <summary>Uma faixa de horario recorrente, por exemplo "seg a sex, 18:00 ate 02:00".</summary>
    public sealed class OrderWindowShiftDto
    {
        /// <summary>Dias da semana em que a faixa COMECA: 0 = domingo ... 6 = sabado.</summary>
        public List<int> Days { get; set; } = new();
        /// <summary>Inicio "HH:mm" (horario de Brasilia).</summary>
        public string Start { get; set; } = "00:00";
        /// <summary>Fim "HH:mm". Menor ou igual ao inicio = termina no dia seguinte.</summary>
        public string End { get; set; } = "23:59";
    }

    /// <summary>
    /// Quais pedidos o mapa e a lista do modo comando mostram, pelo horario em que o pedido
    /// foi feito. E configuracao do estabelecimento (a tela de parametros vira depois).
    /// </summary>
    public sealed class OrderWindowDto
    {
        public string Mode { get; set; } = OrderWindowModes.OperationalDay;
        /// <summary>Usado em last_hours: de 1 a 720.</summary>
        public int? Hours { get; set; }
        /// <summary>Usado em shifts.</summary>
        public List<OrderWindowShiftDto> Shifts { get; set; } = new();
        /// <summary>Usados em custom: "yyyy-MM-ddTHH:mm" no horario de Brasilia. Qualquer um pode faltar.</summary>
        public string? CustomFrom { get; set; }
        public string? CustomTo { get; set; }
        /// <summary>
        /// Pedido ainda em aberto (pendente, com motoboy ou em rota) aparece mesmo fora da
        /// janela: um pedido esquecido de ontem continua precisando de despacho. Padrao: sim.
        /// </summary>
        public bool AlwaysShowOpenOrders { get; set; } = true;
    }
}
