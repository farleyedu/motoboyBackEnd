using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using APIBack.DTOs.Delivery;
using APIBack.Repository.Interface;
using APIBack.Service;
using Dapper;
using Npgsql;

namespace APIBack.Repository
{
    public sealed class HorarioOperacaoRepository : IHorarioOperacaoRepository
    {
        private readonly NpgsqlDataSource _dataSource;

        public HorarioOperacaoRepository(NpgsqlDataSource dataSource)
        {
            _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        }

        private static async Task<bool> TabelasExistemAsync(NpgsqlConnection connection) =>
            await connection.ExecuteScalarAsync<bool>(
                "SELECT to_regclass('estabelecimento_horario') IS NOT NULL AND to_regclass('estabelecimento_horario_especial') IS NOT NULL;");

        private static TimeZoneInfo ResolveTimeZone(string? timezoneIana)
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(string.IsNullOrWhiteSpace(timezoneIana) ? "America/Sao_Paulo" : timezoneIana);
            }
            catch (TimeZoneNotFoundException)
            {
                return TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");
            }
            catch (InvalidTimeZoneException)
            {
                return TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");
            }
        }

        public async Task<bool> EstaAbertoAgoraAsync(Guid estabelecimentoId, DateTimeOffset agoraUtc, string timezoneIana)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            if (!await TabelasExistemAsync(connection))
            {
                return true;
            }

            var tz = ResolveTimeZone(timezoneIana);
            var local = TimeZoneInfo.ConvertTimeFromUtc(agoraUtc.UtcDateTime, tz);
            var horaAtual = local.TimeOfDay;

            var especial = await connection.QuerySingleOrDefaultAsync<HorarioRow>(@"
SELECT fechado AS Fechado, abre_as AS Abre, fecha_as AS Fecha
  FROM estabelecimento_horario_especial
 WHERE estabelecimento_id = @Id AND data = @Data;",
                new { Id = estabelecimentoId, Data = DateOnly.FromDateTime(local) });
            if (especial != null)
            {
                return !especial.Fechado && especial.Abre.HasValue && especial.Fecha.HasValue
                    && horaAtual >= especial.Abre.Value && horaAtual <= especial.Fecha.Value;
            }

            var diaSemana = ((int)local.DayOfWeek + 6) % 7; // .NET: 0=domingo -> nosso indice 0=segunda
            var dia = await connection.QuerySingleOrDefaultAsync<HorarioRow>(@"
SELECT fechado AS Fechado, abre_as AS Abre, fecha_as AS Fecha
  FROM estabelecimento_horario
 WHERE estabelecimento_id = @Id AND dia_semana = @Dia;",
                new { Id = estabelecimentoId, Dia = diaSemana });
            // Sem horario configurado ainda: nao bloqueia (o estabelecimento so passa a ter esse
            // controle depois de preencher a aba Horarios, igual ao checklist de completude do perfil).
            if (dia == null) return true;

            return !dia.Fechado && dia.Abre.HasValue && dia.Fecha.HasValue
                && horaAtual >= dia.Abre.Value && horaAtual <= dia.Fecha.Value;
        }

        public async Task<TimeSpan?> ObterHoraFechamentoAsync(Guid estabelecimentoId, DateOnly dia)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            if (!await TabelasExistemAsync(connection)) return null;

            var especial = await connection.QuerySingleOrDefaultAsync<HorarioRow>(@"
SELECT fechado AS Fechado, abre_as AS Abre, fecha_as AS Fecha
  FROM estabelecimento_horario_especial
 WHERE estabelecimento_id = @Id AND data = @Data;", new { Id = estabelecimentoId, Data = dia });
            if (especial != null) return especial.Fechado ? null : especial.Fecha;

            var diaSemana = ((int)dia.DayOfWeek + 6) % 7; // .NET: 0=domingo -> nosso indice 0=segunda
            var semanal = await connection.QuerySingleOrDefaultAsync<HorarioRow>(@"
SELECT fechado AS Fechado, abre_as AS Abre, fecha_as AS Fecha
  FROM estabelecimento_horario
 WHERE estabelecimento_id = @Id AND dia_semana = @Dia;", new { Id = estabelecimentoId, Dia = diaSemana });
            return semanal == null || semanal.Fechado ? null : semanal.Fecha;
        }

        public async Task<IReadOnlyList<HorarioEspecialDto>> ListarEspeciaisAsync(Guid estabelecimentoId)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            var rows = await connection.QueryAsync<(long Id, DateOnly Data, bool Fechado, TimeSpan? Abre, TimeSpan? Fecha, string? Motivo)>(@"
SELECT id, data, fechado, abre_as, fecha_as, motivo FROM estabelecimento_horario_especial
 WHERE estabelecimento_id = @Id ORDER BY data;", new { Id = estabelecimentoId });
            return rows.Select(r => new HorarioEspecialDto
            {
                Id = r.Id,
                Data = r.Data.ToString("yyyy-MM-dd"),
                Fechado = r.Fechado,
                AbreAs = r.Abre?.ToString(@"hh\:mm"),
                FechaAs = r.Fecha?.ToString(@"hh\:mm"),
                Motivo = r.Motivo
            }).ToList();
        }

        public async Task<HorarioEspecialDto> SalvarEspecialAsync(Guid estabelecimentoId, SalvarHorarioEspecialRequest request)
        {
            if (!DateOnly.TryParse(request.Data, out var data))
            {
                throw new DeliveryDomainException(422, "INVALID_REQUEST", "Data invalida (use AAAA-MM-DD).");
            }
            TimeSpan? abre = null, fecha = null;
            if (!request.Fechado)
            {
                if (!TimeSpan.TryParse(request.AbreAs, out var abreValue) || !TimeSpan.TryParse(request.FechaAs, out var fechaValue) || fechaValue <= abreValue)
                {
                    throw new DeliveryDomainException(422, "INVALID_REQUEST", "Informe abertura e fechamento, com fechamento depois da abertura.");
                }
                abre = abreValue;
                fecha = fechaValue;
            }

            await using var connection = await _dataSource.OpenConnectionAsync();
            var row = await connection.QuerySingleAsync<(long Id, DateOnly Data, bool Fechado, TimeSpan? Abre, TimeSpan? Fecha, string? Motivo)>(@"
INSERT INTO estabelecimento_horario_especial (estabelecimento_id, data, fechado, abre_as, fecha_as, motivo)
VALUES (@EstabelecimentoId, @Data, @Fechado, @Abre, @Fecha, @Motivo)
ON CONFLICT (estabelecimento_id, data) DO UPDATE SET
    fechado = EXCLUDED.fechado, abre_as = EXCLUDED.abre_as, fecha_as = EXCLUDED.fecha_as, motivo = EXCLUDED.motivo
RETURNING id, data, fechado, abre_as, fecha_as, motivo;", new
            {
                EstabelecimentoId = estabelecimentoId,
                Data = data,
                request.Fechado,
                Abre = abre,
                Fecha = fecha,
                Motivo = string.IsNullOrWhiteSpace(request.Motivo) ? null : request.Motivo!.Trim()
            });
            return new HorarioEspecialDto
            {
                Id = row.Id,
                Data = row.Data.ToString("yyyy-MM-dd"),
                Fechado = row.Fechado,
                AbreAs = row.Abre?.ToString(@"hh\:mm"),
                FechaAs = row.Fecha?.ToString(@"hh\:mm"),
                Motivo = row.Motivo
            };
        }

        public async Task<bool> ExcluirEspecialAsync(Guid estabelecimentoId, long id)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            var affected = await connection.ExecuteAsync(
                "DELETE FROM estabelecimento_horario_especial WHERE id = @Id AND estabelecimento_id = @EstabelecimentoId;",
                new { Id = id, EstabelecimentoId = estabelecimentoId });
            return affected > 0;
        }

        private sealed class HorarioRow
        {
            public bool Fechado { get; set; }
            public TimeSpan? Abre { get; set; }
            public TimeSpan? Fecha { get; set; }
        }
    }
}
