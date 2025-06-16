namespace APIBack.Service.Interface
{
    public interface ILocalizacaoService
    {
        Task<(double Latitude, double Longitude)?> ObterCoordenadasAsync(string endereco);
    }
}
