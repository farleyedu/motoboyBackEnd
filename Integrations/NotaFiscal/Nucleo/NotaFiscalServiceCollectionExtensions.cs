using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace APIBack.Integrations.NotaFiscal.Nucleo;

public static class NotaFiscalServiceCollectionExtensions
{
    /// <summary>
    /// Registra o nucleo fiscal. A fabrica de emissores e o parceiro entram com TryAdd: o prompt 2 registra as implementacoes
    /// reais antes desta chamada (ou depois, trocando por Replace), e ate la o padrao e "provedor indisponivel" e "parceiro desligado".
    /// </summary>
    public static IServiceCollection AddNotaFiscalNucleo(this IServiceCollection services)
    {
        services.TryAddSingleton<IEmissorFiscalFabrica, EmissorFiscalFabricaVazia>();
        services.TryAddSingleton<IProvedorParceiro, ProvedorParceiroDesligado>();
        services.AddScoped<INotaFiscalRepositorio, NotaFiscalRepositorio>();
        services.AddScoped<INotaFiscalNucleoService, NotaFiscalNucleoService>();
        return services;
    }
}
