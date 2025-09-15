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

        // Normaliza número brasileiro para uso no campo "to" da API do WhatsApp (sem '+').
        // Regra solicitada: se começar com "55" e tiver 12 dígitos, inserir '9' após o DDD (posição 4).
        // Ex.: 553491480112 -> 5534991480112
        public static string NormalizeBrazilianForWhatsappTo(string rawNumber)
        {
            var input = rawNumber ?? string.Empty;

            // Mantém apenas dígitos
            var digits = new string(input.Where(char.IsDigit).ToArray());

            // Insere o nono dígito quando vier no formato antigo (8 dígitos após DDD)
            if (digits.StartsWith("55") && digits.Length == 12)
            {
                // Índice 4: depois de "55" (2) + DDD (2)
                digits = digits.Insert(4, "9");
            }

            return digits;
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
