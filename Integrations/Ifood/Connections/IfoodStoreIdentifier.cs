namespace APIBack.Integrations.Ifood.Connections;

public enum IfoodIdentifierKind { Invalid, MerchantId, Cnpj }

/// <summary>O restaurante informa o ID da loja no iFood (UUID) OU o CNPJ; o servidor decide qual e e valida o formato.</summary>
public sealed record IfoodStoreIdentifier(IfoodIdentifierKind Kind, Guid MerchantId, string CnpjDigits)
{
    public static IfoodStoreIdentifier Parse(string? input)
    {
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text)) return new(IfoodIdentifierKind.Invalid, Guid.Empty, "");
        if (Guid.TryParse(text, out var id))
            return id == Guid.Empty ? new(IfoodIdentifierKind.Invalid, Guid.Empty, "") : new(IfoodIdentifierKind.MerchantId, id, "");
        // CNPJ aceita mascara (00.000.000/0000-00) ou so digitos; qualquer outra letra e erro.
        if (text.All(c => char.IsDigit(c) || c is '.' or '/' or '-' or ' '))
        {
            var digits = new string(text.Where(char.IsDigit).ToArray());
            if (IsValidCnpj(digits)) return new(IfoodIdentifierKind.Cnpj, Guid.Empty, digits);
        }
        return new(IfoodIdentifierKind.Invalid, Guid.Empty, "");
    }

    public static string Digits(string? value) => new((value ?? "").Where(char.IsDigit).ToArray());

    public static bool IsValidCnpj(string digits)
    {
        if (digits.Length != 14 || digits.Distinct().Count() == 1) return false;
        return Check(digits, 12) && Check(digits, 13);
        static bool Check(string d, int length)
        {
            var sum = 0; var weight = length - 7;
            for (var i = 0; i < length; i++)
            {
                sum += (d[i] - '0') * weight--;
                if (weight < 2) weight = 9;
            }
            var rest = sum % 11;
            return d[length] - '0' == (rest < 2 ? 0 : 11 - rest);
        }
    }
}
