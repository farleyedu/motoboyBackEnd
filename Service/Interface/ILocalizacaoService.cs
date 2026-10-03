namespace APIBack.Service.Interface
{
    /// <summary>Resultado de uma busca de endereco: a coordenada e se ela aponta para a porta do endereco.</summary>
    /// <param name="Exata">
    /// Verdadeiro so quando o servico achou o numero informado (nao so a rua, o bairro ou o CEP). Fora disso o ponto e
    /// aproximado e nao serve para entrega sem o cliente confirmar no mapa.
    /// </param>
    public sealed record GeocodeResultado(string Latitude, string Longitude, bool Exata, string Provedor);

    /// <summary>Endereco de um ponto do mapa (para "usar minha localizacao"). Qualquer campo pode faltar.</summary>
    public sealed record EnderecoReverso(string? Logradouro, string? Numero, string? Bairro, string? Cidade, string? Uf, string? Cep);

    public interface ILocalizacaoService
    {
        /// <summary>
        /// Coordenada do endereco, exata ou nao (telas internas, onde o operador ajusta o pino). Devolve null quando
        /// nenhum servico achou o endereco.
        /// </summary>
        Task<(string Latitude, string Longitude)?> ObterCoordenadasAsync(string endereco);

        /// <summary>
        /// Busca pelo Google Geocoding e informa se o ponto e exato. Devolve null quando o servico nao esta configurado
        /// ou nao achou o endereco. <paramref name="cep"/> (8 digitos) restringe a busca ao CEP informado.
        /// </summary>
        Task<GeocodeResultado?> GeocodificarAsync(string endereco, string? cep = null);

        /// <summary>Endereco do ponto (Google). Nulo quando o servico nao esta configurado ou nao conhece o ponto.</summary>
        Task<EnderecoReverso?> GeocodificarReversoAsync(double latitude, double longitude);
    }
}
