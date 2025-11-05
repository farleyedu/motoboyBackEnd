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
            return _reservasRepository.GetMetricasDia(data.Date);
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

            ExcelPackage.LicenseContext = LicenseContext.NonCommercial;

            using var package = new ExcelPackage();
            var worksheet = package.Workbook.Worksheets.Add("Reservas");

            var headers = new[]
            {
                "ID", "Data", "Hora In\u00EDcio", "Hora Fim", "Cliente", "Pessoas", "Status", "Telefone", "Observa\u00E7\u00F5es"
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
    }
}
