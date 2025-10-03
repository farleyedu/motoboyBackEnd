using System;
using System.Threading.Tasks;
using APIBack.Repository.Interface;

namespace APIBack.Service
{
    public class ReservaService
    {
        private readonly IReservaRepository _reservaRepository;

        public ReservaService(IReservaRepository reservaRepository)
        {
            _reservaRepository = reservaRepository;
        }

        public async Task<bool> VerificarCapacidadeDiaAsync(Guid idEstabelecimento, DateTime dataReserva, int qtdPessoasSolicitada)
        {
            var hoje = DateTime.Today;
            var mesmoDia = dataReserva.Date == hoje;

            // TODO: buscar limites personalizados em estab_agenda_config e overrides especificos
            var limite = mesmoDia ? 50 : 110;

            var ocupadas = await _reservaRepository.SomarPessoasDoDiaAsync(idEstabelecimento, dataReserva.Date);

            return ocupadas + qtdPessoasSolicitada <= limite;
        }

        public Task<bool> BuscarDisponibilidadeAsync(Guid idEstabelecimento, DateTime dataReserva, int qtdPessoas)
        {
            return VerificarCapacidadeDiaAsync(idEstabelecimento, dataReserva, qtdPessoas);
        }
    }
}
