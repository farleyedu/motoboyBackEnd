using APIBack.DTOs.Delivery;

namespace APIBack.Service
{
    /// <summary>Validacao das zonas de entrega (faixa de raio com taxa propria). Sem banco.</summary>
    public static class DeliveryZonaRules
    {
        public static SalvarZonaRequest Validate(SalvarZonaRequest? request)
        {
            if (request == null) throw Invalid("Corpo da requisicao obrigatorio.");

            var nome = request.Nome?.Trim();
            if (string.IsNullOrEmpty(nome)) throw Invalid("Informe o nome da zona.");
            if (nome.Length > 60) throw Invalid("Nome da zona excede 60 caracteres.");

            if (request.RaioAteKm <= 0 || request.RaioAteKm > 500) throw Invalid("Raio da zona deve ficar entre 0 e 500 km.");
            if (request.Taxa < 0 || request.Taxa > 10_000) throw Invalid("Taxa da zona deve ficar entre 0 e 10.000.");

            var cor = request.Cor?.Trim();
            if (!string.IsNullOrEmpty(cor) && (cor.Length != 7 || cor[0] != '#')) throw Invalid("Cor deve estar no formato #RRGGBB.");

            return new SalvarZonaRequest
            {
                Nome = nome,
                RaioAteKm = decimal.Round(request.RaioAteKm, 2),
                Taxa = decimal.Round(request.Taxa, 2),
                Cor = string.IsNullOrEmpty(cor) ? null : cor,
                Ordem = request.Ordem,
                Ativo = request.Ativo,
            };
        }

        private static DeliveryDomainException Invalid(string message) => new(422, "INVALID_ZONA", message);
    }
}
