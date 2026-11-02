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

        // Telefone de pessoa (usuario da equipe, contato): E.164 ou nulo. Aceita "(34) 99148-0112", "34991480112", "+55 34 99148-0112".
        // Vazio vira nulo; numero que nao parece brasileiro (DDD + 8 ou 9 digitos, com ou sem 55) e invalido (devolve false).
        public static bool TryNormalizarE164(string? bruto, out string? e164)
        {
            e164 = null;
            if (string.IsNullOrWhiteSpace(bruto))
            {
                return true;
            }

            var digits = new string(bruto.Where(char.IsDigit).ToArray());
            if (digits.StartsWith("55") && (digits.Length == 12 || digits.Length == 13))
            {
                digits = digits.Substring(2);
            }

            if (digits.Length is not (10 or 11))
            {
                return false;
            }

            var ddd = int.Parse(digits.Substring(0, 2));
            if (ddd < 11 || (digits.Length == 11 && digits[2] != '9'))
            {
                return false;
            }

            e164 = "+55" + digits;
            return true;
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
