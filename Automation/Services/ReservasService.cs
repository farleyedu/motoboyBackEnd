using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using APIBack.DTOs;
using APIBack.Repository.Interface;
using APIBack.Service.Interface;
using OfficeOpenXml;

namespace APIBack.Service
{
    public class ReservasService : IReservasService
    {
        private const string StatusConcluido = "conclu\u00EDdo";

        private static readonly HashSet<string> StatusConfirmados = new(StringComparer.OrdinalIgnoreCase)
        {
            "confirmado",
            "confirmada",
            StatusConcluido,
            "concluido"
        };

        private readonly IReservasRepository _reservasRepository;
        private const int CapacidadePadraoPorDia = 110;

        public ReservasService(IReservasRepository reservasRepository)
        {
            _reservasRepository = reservasRepository;
        }

        /// <summary>
        /// Obtem reservas de restaurante por mes/ano/estabelecimento agrupadas por data com total de pessoas confirmadas.
        /// </summary>
        public IEnumerable<ReservasDiaDTO> GetReservasRestaurante(int month, int year, Guid estabelecimentoId)
        {
            var reservas = _reservasRepository
                .GetReservasRestaurante(month, year, estabelecimentoId)
                ?.ToList() ?? new List<dynamic>();

            var grupos = reservas.GroupBy(ExtractDataReserva);
            var resultado = new List<ReservasDiaDTO>();

            foreach (var grupo in grupos.OrderBy(g => g.Key))
            {
                if (grupo.Key == DateTime.MinValue)
                {
                    continue;
                }

                var reservasDia = grupo.Cast<dynamic>().ToList();
                var totalPessoasDia = 0;

                foreach (var reserva in reservasDia)
                {
                    if (EhConfirmada(reserva))
                    {
                        totalPessoasDia += ExtrairQuantidadePessoas(reserva);
                    }
                }

                resultado.Add(new ReservasDiaDTO
                {
                    DataReserva = grupo.Key,
                    Reservas = reservasDia,
                    TotalPessoasDia = totalPessoasDia
                });
            }

            return resultado;
        }

        /// <summary>
        /// Obtem reservas de barbearia + lista de barbeiros ativos.
        /// </summary>
        public object GetReservasBarbearia(int month, int year, Guid estabelecimentoId)
        {
            var (reservations, barbers) = _reservasRepository.GetReservasBarbearia(month, year, estabelecimentoId);

            return new
            {
                reservations,
                barbers
            };
        }

        /// <summary>
        /// Obtem metricas consolidadas de reservas confirmadas em uma data especifica.
        /// </summary>
        public MetricasDiaDTO GetMetricasDia(DateTime data)
        {
            var metricas = _reservasRepository.GetMetricasDia(data.Date);
            metricas.TaxaOcupacao = CalcularTaxaOcupacao(metricas.TotalPessoas, CapacidadePadraoPorDia);
            return metricas;
        }

        /// <summary>
        /// Obtém métricas agregadas por períodos (dia, semana, quinzena, mês).
        /// </summary>
        public MetricasMesDTO GetMetricasMes(int month, int year, Guid estabelecimentoId)
        {
            var hoje = DateTime.Today;
            var isMesAtual = hoje.Year == year && hoje.Month == month;

            if (!isMesAtual)
            {
                return CalcularMetricasMesCompleto(month, year, estabelecimentoId);
            }

            return CalcularMetricasPeriodos(month, year, estabelecimentoId, hoje);
        }

        /// <summary>
        /// Obtém métricas agregadas para um período arbitrário.
        /// </summary>
        public MetricasPeriodoDTO GetMetricasPeriodo(DateTime inicio, DateTime fim, Guid estabelecimentoId, long? barbeiroId)
        {
            if (fim < inicio)
            {
                throw new ArgumentException("Data final não pode ser anterior à data inicial.");
            }

            var diasPeriodo = (int)(fim.Date - inicio.Date).TotalDays + 1;
            if (diasPeriodo <= 0)
            {
                diasPeriodo = 1;
            }

            var capacidadeTotal = CapacidadePadraoPorDia * diasPeriodo;
            var (quantidadeReservas, totalPessoas) = _reservasRepository.ContarReservasPorPeriodo(inicio, fim, estabelecimentoId, barbeiroId);
            var taxaOcupacao = CalcularTaxaOcupacao(totalPessoas, capacidadeTotal);

            return new MetricasPeriodoDTO
            {
                QuantidadeReservas = quantidadeReservas,
                TotalPessoas = totalPessoas,
                TaxaOcupacao = taxaOcupacao,
                Periodo = new PeriodoDTO
                {
                    Inicio = inicio,
                    Fim = fim
                }
            };
        }

        /// <summary>
        /// Atualiza o status da reserva para concluido.
        /// </summary>
        public async Task MarcarChegadaAsync(int id)
        {
            var sucesso = await _reservasRepository.AtualizarStatusAsync(id, StatusConcluido);

            if (!sucesso)
            {
                throw new InvalidOperationException($"Reserva {id} não encontrada para atualização de status.");
            }
        }

        /// <summary>
        /// Exporta as reservas do dia especificado em arquivo Excel.
        /// </summary>
        public async Task<byte[]> ExportarDiaAsync(DateOnly data)
        {
            var reservas = (await _reservasRepository.ListarPorDiaAsync(data)).ToList();

            ExcelPackage.License.SetNonCommercialOrganization("Zippy");
            using var package = new ExcelPackage();
            var worksheet = package.Workbook.Worksheets.Add("Reservas");

            var headers = new[]
            {
                "ID", "Data", "Hora Inicio", "Hora Fim", "Cliente", "Pessoas", "Status", "Telefone", "Observações"
            };

            for (var col = 0; col < headers.Length; col++)
            {
                var cell = worksheet.Cells[1, col + 1];
                cell.Value = headers[col];
                cell.Style.Font.Bold = true;
            }

            var row = 2;

            foreach (var item in reservas)
            {
                var registro = (IDictionary<string, object?>)item;

                worksheet.Cells[row, 1].Value = ExtractInt(registro, "id");

                var dataReserva = ExtractDate(registro, "data_reserva");
                worksheet.Cells[row, 2].Value = dataReserva;
                worksheet.Cells[row, 2].Style.Numberformat.Format = "yyyy-MM-dd";

                var horaInicio = ExtractTime(registro, "hora_inicio");
                if (horaInicio.HasValue)
                {
                    worksheet.Cells[row, 3].Value = DateTime.Today.Add(horaInicio.Value);
                    worksheet.Cells[row, 3].Style.Numberformat.Format = "HH:mm";
                }

                var horaFim = ExtractTime(registro, "hora_fim");
                if (horaFim.HasValue)
                {
                    worksheet.Cells[row, 4].Value = DateTime.Today.Add(horaFim.Value);
                    worksheet.Cells[row, 4].Style.Numberformat.Format = "HH:mm";
                }

                worksheet.Cells[row, 5].Value = SafeToString(registro, "nome_cliente_reserva");
                worksheet.Cells[row, 6].Value = ExtractInt(registro, "qtd_pessoas");
                worksheet.Cells[row, 7].Value = SafeToString(registro, "status");
                worksheet.Cells[row, 8].Value = SafeToString(registro, "telefone_cliente");
                worksheet.Cells[row, 9].Value = SafeToString(registro, "observacoes");

                row++;
            }

            if (row > 2)
            {
                worksheet.Cells[1, 1, row - 1, headers.Length].AutoFitColumns();
            }
            else
            {
                worksheet.Cells[1, 1, 1, headers.Length].AutoFitColumns();
            }

            return package.GetAsByteArray();
        }

        private static DateTime ExtractDataReserva(dynamic reserva)
        {
            if (reserva == null)
            {
                return DateTime.MinValue;
            }

            var value = reserva.data_reserva;

            if (value is DateTime dt)
            {
                return dt.Date;
            }

            if (DateTime.TryParse(Convert.ToString(value), out DateTime parsedData))
            {
                return parsedData.Date;
            }

            return DateTime.MinValue;
        }

        private static bool EhConfirmada(dynamic reserva)
        {
            var status = Convert.ToString(reserva?.status) ?? string.Empty;
            return StatusConfirmados.Contains(status);
        }

        private static int ExtrairQuantidadePessoas(dynamic reserva)
        {
            if (reserva == null)
            {
                return 0;
            }

            var value = reserva.qtd_pessoas;

            if (value == null || value is DBNull)
            {
                return 0;
            }

            if (value is int i)
            {
                return i;
            }

            if (value is long l)
            {
                return (int)l;
            }

            return int.TryParse(Convert.ToString(value), out int parsedQuantidade) ? parsedQuantidade : 0;
        }

        private static DateTime ExtractDate(IDictionary<string, object?> registro, string key)
        {
            if (!registro.TryGetValue(key, out var value) || value is null || value is DBNull)
            {
                return DateTime.MinValue;
            }

            if (value is DateTime dt)
            {
                return dt;
            }

            if (DateTime.TryParse(Convert.ToString(value), out DateTime parsedData))
            {
                return parsedData;
            }

            return DateTime.MinValue;
        }

        private static TimeSpan? ExtractTime(IDictionary<string, object?> registro, string key)
        {
            if (!registro.TryGetValue(key, out var value) || value is null || value is DBNull)
            {
                return null;
            }

            if (value is TimeSpan ts)
            {
                return ts;
            }

            if (value is DateTime dt)
            {
                return dt.TimeOfDay;
            }

            return TimeSpan.TryParse(Convert.ToString(value), out TimeSpan parsedHora) ? parsedHora : null;
        }

        private static string SafeToString(IDictionary<string, object?> registro, string key)
        {
            if (!registro.TryGetValue(key, out var value) || value is null || value is DBNull)
            {
                return string.Empty;
            }

            return Convert.ToString(value) ?? string.Empty;
        }

        private static int ExtractInt(IDictionary<string, object?> registro, string key)
        {
            if (!registro.TryGetValue(key, out var value) || value is null || value is DBNull)
            {
                return 0;
            }

            if (value is int i)
            {
                return i;
            }

            if (value is long l)
            {
                return (int)l;
            }

            return int.TryParse(Convert.ToString(value), out int parsedQuantidade) ? parsedQuantidade : 0;
        }

        private MetricasMesDTO CalcularMetricasMesCompleto(int month, int year, Guid estabelecimentoId)
        {
            var inicioMes = new DateTime(year, month, 1);
            var fimMes = new DateTime(year, month, DateTime.DaysInMonth(year, month), 23, 59, 59);

            var (totalReservas, totalPessoas) = _reservasRepository.ContarReservasPorPeriodo(inicioMes, fimMes, estabelecimentoId, null);

            return new MetricasMesDTO
            {
                IsMesAtual = false,
                MesSelecionado = new PeriodoMetricaDTO
                {
                    TotalReservas = totalReservas,
                    TotalPessoas = totalPessoas,
                    DataInicio = inicioMes,
                    DataFim = fimMes
                }
            };
        }

        private MetricasMesDTO CalcularMetricasPeriodos(int month, int year, Guid estabelecimentoId, DateTime hoje)
        {
            var inicioHoje = hoje.Date;
            var fimHoje = hoje.Date.AddDays(1).AddTicks(-1);
            var (reservasHoje, pessoasHoje) = _reservasRepository.ContarReservasPorPeriodo(inicioHoje, fimHoje, estabelecimentoId, null);

            var inicioSemana = GetStartOfWeek(hoje);
            var fimSemana = inicioSemana.AddDays(7).AddTicks(-1);
            var (reservasSemana, pessoasSemana) = _reservasRepository.ContarReservasPorPeriodo(inicioSemana, fimSemana, estabelecimentoId, null);

            var (inicioQuinzena, fimQuinzena, numeroQuinzena) = GetQuinzenaAtual(hoje);
            var (reservasQuinzena, pessoasQuinzena) = _reservasRepository.ContarReservasPorPeriodo(inicioQuinzena, fimQuinzena, estabelecimentoId, null);

            var inicioMes = new DateTime(year, month, 1);
            var fimMes = new DateTime(year, month, DateTime.DaysInMonth(year, month), 23, 59, 59);
            var (reservasMes, pessoasMes) = _reservasRepository.ContarReservasPorPeriodo(inicioMes, fimMes, estabelecimentoId, null);

            return new MetricasMesDTO
            {
                IsMesAtual = true,
                Hoje = new PeriodoMetricaDTO
                {
                    TotalReservas = reservasHoje,
                    TotalPessoas = pessoasHoje,
                    DataInicio = inicioHoje,
                    DataFim = fimHoje
                },
                SemanaVigente = new PeriodoMetricaDTO
                {
                    TotalReservas = reservasSemana,
                    TotalPessoas = pessoasSemana,
                    DataInicio = inicioSemana,
                    DataFim = fimSemana
                },
                QuinzenaVigente = new PeriodoMetricaDTO
                {
                    TotalReservas = reservasQuinzena,
                    TotalPessoas = pessoasQuinzena,
                    DataInicio = inicioQuinzena,
                    DataFim = fimQuinzena,
                    Numero = numeroQuinzena
                },
                MesVigente = new PeriodoMetricaDTO
                {
                    TotalReservas = reservasMes,
                    TotalPessoas = pessoasMes,
                    DataInicio = inicioMes,
                    DataFim = fimMes
                }
            };
        }

        private static DateTime GetStartOfWeek(DateTime date)
        {
            var diff = (7 + (date.DayOfWeek - DayOfWeek.Sunday)) % 7;
            return date.AddDays(-1 * diff).Date;
        }

        private static (DateTime inicio, DateTime fim, int numero) GetQuinzenaAtual(DateTime date)
        {
            if (date.Day <= 15)
            {
                var inicio = new DateTime(date.Year, date.Month, 1);
                var fim = new DateTime(date.Year, date.Month, 15, 23, 59, 59);
                return (inicio, fim, 1);
            }

            var inicioSegunda = new DateTime(date.Year, date.Month, 16);
            var ultimoDia = DateTime.DaysInMonth(date.Year, date.Month);
            var fimSegunda = new DateTime(date.Year, date.Month, ultimoDia, 23, 59, 59);
            return (inicioSegunda, fimSegunda, 2);
        }

        private static int CalcularTaxaOcupacao(int totalPessoas, int capacidadeTotal)
        {
            if (capacidadeTotal <= 0)
            {
                return 0;
            }

            var taxa = (double)totalPessoas / capacidadeTotal * 100;
            var arredondado = (int)Math.Round(taxa, MidpointRounding.AwayFromZero);
            return Math.Clamp(arredondado, 0, 100);
        }
    }
}
