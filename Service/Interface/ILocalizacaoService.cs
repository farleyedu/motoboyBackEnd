namespace APIBack.Service.Interface
{
    public interface ILocalizacaoService
    {
        Task<(string Latitude, string Longitude)?> ObterCoordenadasAsync(string endereco);
    }
}
