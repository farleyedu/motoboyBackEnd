using System;
using System.Linq;
using System.Text.RegularExpressions;
using APIBack.DTOs.Clientes;

namespace APIBack.Service
{
    /// <summary>Cliente validado e normalizado, pronto para gravar.</summary>
    public sealed record ClienteInput
    {
        public string Nome { get; init; } = string.Empty;
        /// <summary>Telefone em E.164 (+55...), a MESMA chave que o webhook do WhatsApp usa para achar o cliente.</summary>
        public string TelefoneE164 { get; init; } = string.Empty;
        public string? Email { get; init; }
        public string? Observacoes { get; init; }
        public string? Cep { get; init; }
        public string? Logradouro { get; init; }
        public string? Numero { get; init; }
        public string? Complemento { get; init; }
        public string? Bairro { get; init; }
        public string? Cidade { get; init; }
        public string? Uf { get; init; }
        public double? Latitude { get; init; }
        public double? Longitude { get; init; }
        public bool Simulado { get; init; }
    }

    public static class ClienteRules
    {
        private static readonly Regex EmailPattern = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);

        /// <summary>Digitos do telefone sem o DDI 55 (10 ou 11 digitos); vazio = null.</summary>
        public static string? NationalDigits(string? phone)
        {
            var digits = new string((phone ?? string.Empty).Where(char.IsDigit).ToArray());
            if (digits.Length == 0) return null;
            if ((digits.Length == 12 || digits.Length == 13) && digits.StartsWith("55", StringComparison.Ordinal))
            {
                digits = digits[2..];
            }
            return digits;
        }

        public static ClienteInput Validate(ClienteRequest? request)
        {
            if (request == null) throw Invalid("Corpo da requisicao obrigatorio.");

            var nome = Text(request.Nome, "nome", 150) ?? throw Invalid("Informe o nome do cliente.");

            // Telefone obrigatorio: e o que liga o cadastro ao WhatsApp, as conversas e os pedidos.
            var national = NationalDigits(Text(request.Telefone, "telefone", 30))
                ?? throw Invalid("Informe o telefone do cliente.");
            if (national.Length < 10 || national.Length > 11)
            {
                throw Invalid("Telefone invalido: informe DDD + numero (10 ou 11 digitos).");
            }

            var email = Text(request.Email, "e-mail", 200)?.ToLowerInvariant();
            if (email != null && !EmailPattern.IsMatch(email))
            {
                throw Invalid("E-mail invalido.");
            }

            string? cep = null;
            if (!string.IsNullOrWhiteSpace(request.Cep))
            {
                var digits = new string(request.Cep.Where(char.IsDigit).ToArray());
                if (digits.Length != 8) throw Invalid("CEP deve ter 8 digitos.");
                cep = $"{digits[..5]}-{digits[5..]}";
            }

            var uf = Text(request.Uf, "UF", 2)?.ToUpperInvariant();
            if (uf != null && (uf.Length != 2 || !uf.All(char.IsLetter)))
            {
                throw Invalid("UF deve ter 2 letras (ex.: MG).");
            }

            // Posicao e opcional, mas so vale se vier completa e real (0,0 e cadastro vazio).
            if (request.Latitude.HasValue != request.Longitude.HasValue)
            {
                throw Invalid("Informe latitude e longitude juntas.");
            }
            if (request.Latitude.HasValue)
            {
                var lat = request.Latitude.Value;
                var lng = request.Longitude!.Value;
                if (!double.IsFinite(lat) || lat is < -90 or > 90
                    || !double.IsFinite(lng) || lng is < -180 or > 180
                    || (lat == 0 && lng == 0))
                {
                    throw Invalid("Localizacao invalida.");
                }
            }

            return new ClienteInput
            {
                Nome = nome,
                TelefoneE164 = APIBack.Automation.Helpers.TelefoneHelper.ToE164(national),
                Email = email,
                Observacoes = Text(request.Observacoes, "observacoes", 1000),
                Cep = cep,
                Logradouro = Text(request.Logradouro, "rua", 200),
                Numero = Text(request.Numero, "numero", 20),
                Complemento = Text(request.Complemento, "complemento", 100),
                Bairro = Text(request.Bairro, "bairro", 100),
                Cidade = Text(request.Cidade, "cidade", 100),
                Uf = uf,
                Latitude = request.Latitude,
                Longitude = request.Longitude,
                Simulado = request.Simulado
            };
        }

        /// <summary>Pagina segura: o front nunca decide o tamanho do SELECT.</summary>
        public static (int Page, int PageSize) ClampPaging(int page, int pageSize) =>
            (Math.Max(1, page), Math.Clamp(pageSize <= 0 ? 25 : pageSize, 1, 100));

        private static string? Text(string? value, string label, int max)
        {
            var trimmed = value?.Trim();
            if (string.IsNullOrEmpty(trimmed)) return null;
            if (trimmed.Length > max) throw Invalid($"Campo {label} excede {max} caracteres.");
            return trimmed;
        }

        private static DeliveryDomainException Invalid(string message) => new(422, "INVALID_CLIENT", message);
    }
}
