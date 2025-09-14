// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System.Linq;

namespace APIBack.Automation.Helpers
{
    public static class TelefoneHelper
    {
        // Converte um telefone bruto para o formato E.164 do Brasil (+55...)
        public static string ToE164(string telefoneBruto)
        {
            var bruto = telefoneBruto ?? string.Empty;

            // 1) Mantém apenas dígitos
            var digits = new string(bruto.Where(char.IsDigit).ToArray());

            // 2) Garante prefixo do Brasil (55)
            if (!digits.StartsWith("55"))
            {
                digits = "55" + digits;
            }

            // 3) Retorna com '+'
            return "+" + digits;
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================

