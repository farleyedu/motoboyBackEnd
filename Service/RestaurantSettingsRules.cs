using System.Linq;
using APIBack.DTOs.Delivery;

namespace APIBack.Service
{
    /// <summary>Validacao e normalizacao dos dados do restaurante. Sem banco, para poder testar.</summary>
    public static class RestaurantSettingsRules
    {
        public static UpdateRestaurantSettingsRequest Validate(UpdateRestaurantSettingsRequest? request)
        {
            if (request == null) throw Invalid("Corpo da requisicao obrigatorio.");

            var uf = Text(request.Uf, "UF", 2)?.ToUpperInvariant();
            if (uf != null && (uf.Length != 2 || !uf.All(char.IsLetter)))
            {
                throw Invalid("UF deve ter 2 letras (ex.: MG).");
            }

            string? cep = null;
            if (!string.IsNullOrWhiteSpace(request.Cep))
            {
                var digits = new string(request.Cep.Where(char.IsDigit).ToArray());
                if (digits.Length != 8) throw Invalid("CEP deve ter 8 digitos.");
                cep = $"{digits[..5]}-{digits[5..]}";
            }

            if (request.Latitude.HasValue != request.Longitude.HasValue)
            {
                throw Invalid("Informe latitude e longitude juntas (ou nenhuma).");
            }
            if (request.Latitude.HasValue)
            {
                var lat = request.Latitude.Value;
                var lng = request.Longitude!.Value;
                if (!double.IsFinite(lat) || lat is < -90 or > 90 || !double.IsFinite(lng) || lng is < -180 or > 180
                    || (lat == 0 && lng == 0))
                {
                    throw Invalid("Localizacao invalida.");
                }
            }

            return new UpdateRestaurantSettingsRequest
            {
                Telefone = Text(request.Telefone, "telefone", 30),
                Logradouro = Text(request.Logradouro, "logradouro", 200),
                Numero = Text(request.Numero, "numero", 20),
                Complemento = Text(request.Complemento, "complemento", 100),
                Bairro = Text(request.Bairro, "bairro", 100),
                Cidade = Text(request.Cidade, "cidade", 100),
                Uf = uf,
                Cep = cep,
                Latitude = request.Latitude,
                Longitude = request.Longitude,
                AceitaPedidos = request.AceitaPedidos,
                RaioEntregaKm = Money(request.RaioEntregaKm, "raio de entrega", 500),
                PedidoMinimo = Money(request.PedidoMinimo, "pedido minimo", 100_000),
                TaxaEntregaFixa = Money(request.TaxaEntregaFixa, "taxa de entrega fixa", 10_000),
                TaxaEntregaPorKm = Money(request.TaxaEntregaPorKm, "taxa de entrega por km", 1_000),
                TempoPreparoMin = Minutes(request.TempoPreparoMin, "tempo de preparo", 600),
            };
        }

        private static string? Text(string? value, string label, int max)
        {
            var trimmed = value?.Trim();
            if (string.IsNullOrEmpty(trimmed)) return null;
            if (trimmed.Length > max) throw Invalid($"Campo {label} excede {max} caracteres.");
            return trimmed;
        }

        private static decimal? Money(decimal? value, string label, decimal max)
        {
            if (value is null) return null;
            if (value < 0 || value > max) throw Invalid($"Valor de {label} deve ficar entre 0 e {max}.");
            return decimal.Round(value.Value, 2);
        }

        private static int? Minutes(int? value, string label, int max)
        {
            if (value is null) return null;
            if (value < 0 || value > max) throw Invalid($"Valor de {label} deve ficar entre 0 e {max} minutos.");
            return value;
        }

        private static DeliveryDomainException Invalid(string message) => new(422, "INVALID_RESTAURANT_SETTINGS", message);
    }
}
