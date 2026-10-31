using System.Threading;
using System.Threading.Tasks;

namespace APIBack.Service.Interface
{
    /// <summary>Uso de um servico pago do Google em um mes (UTC).</summary>
    public sealed record GoogleUsoResumo(string Servico, string Mes, int Usado, int Limite);

    /// <summary>
    /// Conta as chamadas ao Google que podem gerar cobranca, para parar dentro da franquia gratuita.
    /// A cota diaria do Google continua sendo o freio final; este contador evita chegar nela e mostra o uso do mes.
    /// </summary>
    public interface IGoogleUsoContador
    {
        /// <summary>Reserva uma chamada no mes. False = limite do mes atingido: nao chame o Google.</summary>
        Task<bool> TentarReservarAsync(string servico, int limiteMensal, CancellationToken cancellationToken = default);

        Task<GoogleUsoResumo> ObterAsync(string servico, int limiteMensal, CancellationToken cancellationToken = default);
    }
}
