using APIBack.Repository.Interface;
using System;
using System.Threading.Tasks;

namespace APIBack.Service
{
    public class ReservaService
    {
        private readonly IReservaRepository _reservaRepository;

        public ReservaService(IReservaRepository reservaRepository)
        {
            _reservaRepository = reservaRepository;
        }

        /// <summary>
        /// Valida se a data da reserva é permitida (não pode ser terça-feira)
        /// </summary>
        /// <param name="dataReserva">Data da reserva a ser validada</param>
        /// <param name="mensagemErro">Mensagem de erro caso validação falhe</param>
        /// <returns>True se data é válida, False se for terça-feira</returns>
        private bool ValidarDiaSemana(DateTime dataReserva, out string mensagemErro)
        {
            mensagemErro = string.Empty;

            // Verifica se é terça-feira (DayOfWeek.Tuesday = 2)
            if (dataReserva.DayOfWeek == DayOfWeek.Tuesday)
            {
                mensagemErro = "Não abrimos às terças-feira. Por favor, escolha outro dia da semana.";
                return false;
            }

            return true;
        }

        /// <summary>
        /// Valida se a quantidade de pessoas atende o mínimo exigido (10 pessoas)
        /// </summary>
        /// <param name="qtdPessoas">Quantidade de pessoas na reserva</param>
        /// <param name="mensagemErro">Mensagem de erro caso validação falhe</param>
        /// <returns>True se quantidade é válida, False se menor que 10</returns>
        private bool ValidarQuantidadeMinima(int qtdPessoas, out string mensagemErro)
        {
            mensagemErro = string.Empty;

            const int QUANTIDADE_MINIMA = 10;

            if (qtdPessoas < QUANTIDADE_MINIMA)
            {
                mensagemErro = $"Reservas são aceitas a partir de {QUANTIDADE_MINIMA} pessoas. " +
                              "Para grupos menores, pode vir sem reservar - teremos prazer em recebê-los!";
                return false;
            }

            return true;
        }

        /// <summary>
        /// Verifica se há capacidade disponível para a reserva no dia solicitado
        /// Aplica validações de: dia da semana, quantidade mínima e capacidade
        /// </summary>
        /// <param name="idEstabelecimento">ID do estabelecimento</param>
        /// <param name="dataReserva">Data da reserva</param>
        /// <param name="qtdPessoasSolicitada">Quantidade de pessoas solicitada</param>
        /// <returns>Tupla com (Sucesso, Mensagem)</returns>
        public async Task<(bool Sucesso, string Mensagem)> VerificarCapacidadeDiaAsync(
            Guid idEstabelecimento,
            DateTime dataReserva,
            int qtdPessoasSolicitada)
        {
            // ===== VALIDAÇÃO 1: Dia da semana (terça fechada) =====
            if (!ValidarDiaSemana(dataReserva, out string mensagemDia))
            {
                return (false, mensagemDia);
            }

            // ===== VALIDAÇÃO 2: Quantidade mínima (10 pessoas) =====
            if (!ValidarQuantidadeMinima(qtdPessoasSolicitada, out string mensagemQtd))
            {
                return (false, mensagemQtd);
            }

            // ===== VALIDAÇÃO 3: Capacidade do dia =====
            var hoje = DateTime.Today;
            var mesmoDia = dataReserva.Date == hoje;

            // TODO: buscar limites personalizados em estab_agenda_config e overrides especificos
            var limite = mesmoDia ? 50 : 110;

            var ocupadas = await _reservaRepository.SomarPessoasDoDiaAsync(idEstabelecimento, dataReserva.Date);

            var disponiveis = limite - ocupadas;

            if (ocupadas + qtdPessoasSolicitada > limite)
            {
                return (false, $"Capacidade insuficiente para o dia solicitado. " +
                              $"Disponível: {disponiveis} pessoas. Solicitado: {qtdPessoasSolicitada} pessoas.");
            }

            return (true, "Reserva disponível");
        }

        /// <summary>
        /// Busca disponibilidade para uma reserva
        /// </summary>
        /// <param name="idEstabelecimento">ID do estabelecimento</param>
        /// <param name="dataReserva">Data da reserva</param>
        /// <param name="qtdPessoas">Quantidade de pessoas</param>
        /// <returns>Tupla com (Disponível, Mensagem)</returns>
        public async Task<(bool Disponivel, string Mensagem)> BuscarDisponibilidadeAsync(
            Guid idEstabelecimento,
            DateTime dataReserva,
            int qtdPessoas)
        {
            var resultado = await VerificarCapacidadeDiaAsync(idEstabelecimento, dataReserva, qtdPessoas);
            return (resultado.Sucesso, resultado.Mensagem);
        }
    }
}