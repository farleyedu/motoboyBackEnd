using APIBack.DTOs.Clientes;

namespace APIBack.Service;

public static class ClienteEnderecoRules
{
    public static ClienteEnderecoRequest Validate(ClienteEnderecoRequest input)
    {
        string? Text(string? value, string campo, int max, bool required = false)
        {
            var clean = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if ((required && clean == null) || clean?.Length > max)
                throw new DeliveryDomainException(422, "INVALID_ADDRESS", $"Confira {campo} (máximo de {max} caracteres).");
            return clean;
        }
        input.Logradouro = Text(input.Logradouro, "rua", 200, true);
        input.Numero = Text(input.Numero, "número", 20, true);
        input.Bairro = Text(input.Bairro, "bairro", 100, true);
        input.Cidade = Text(input.Cidade, "cidade", 100, true);
        input.Uf = Text(input.Uf, "UF", 2, true)?.ToUpperInvariant();
        if (input.Uf?.Length != 2 || !input.Uf.All(char.IsLetter))
            throw new DeliveryDomainException(422, "INVALID_ADDRESS", "Informe a UF com duas letras.");
        input.Cep = Text(input.Cep, "CEP", 9);
        if (input.Cep != null)
        {
            input.Cep = new string(input.Cep.Where(char.IsDigit).ToArray());
            if (input.Cep.Length != 8) throw new DeliveryDomainException(422, "INVALID_ADDRESS", "CEP deve ter 8 dígitos.");
        }
        input.Apelido = Text(input.Apelido, "apelido", 60);
        input.Complemento = Text(input.Complemento, "complemento", 100);
        input.Referencia = Text(input.Referencia, "referência", 200);
        if (input.Latitude.HasValue != input.Longitude.HasValue ||
            (input.Latitude.HasValue && (!double.IsFinite(input.Latitude.Value) || !double.IsFinite(input.Longitude!.Value) ||
                input.Latitude < -90 || input.Latitude > 90 || input.Longitude < -180 || input.Longitude > 180)))
            throw new DeliveryDomainException(422, "INVALID_ADDRESS", "Localização inválida.");
        return input;
    }
}
