using System;

namespace APIBack.Automation.Helpers
{
    public static class DateFormattingHelper
    {
        private static readonly string[] DiasSemanaPt =
        {
            "Domingo", "Segunda-feira", "Terca-feira", "Quarta-feira",
            "Quinta-feira", "Sexta-feira", "Sabado"
        };

        private static readonly string[] MesesPt =
        {
            string.Empty,
            "janeiro",
            "fevereiro",
            "marco",
            "abril",
            "maio",
            "junho",
            "julho",
            "agosto",
            "setembro",
            "outubro",
            "novembro",
            "dezembro"
        };

        public static string FormatarDataCompleta(DateTime data)
        {
            var diaSemana = DiasSemanaPt[(int)data.DayOfWeek];
            var mes = MesesPt[data.Month];
            return $"{diaSemana}, {data.Day} de {mes} de {data.Year}";
        }

        public static string FormatarDataCurta(DateTime data)
        {
            var diaSemana = DiasSemanaPt[(int)data.DayOfWeek];
            return $"{diaSemana}, {data:dd/MM}";
        }

        public static string FormatarDiaSemana(DateTime data)
        {
            return DiasSemanaPt[(int)data.DayOfWeek];
        }

        public static string FormatarHorario(TimeSpan hora)
        {
            return hora.ToString(@"hh\:mm");
        }

        public static string FormatarDataHorario(DateTime data, TimeSpan hora)
        {
            var diaSemana = DiasSemanaPt[(int)data.DayOfWeek];
            return $"{diaSemana}, {data:dd/MM} as {hora:hh\\:mm}";
        }
    }
}
