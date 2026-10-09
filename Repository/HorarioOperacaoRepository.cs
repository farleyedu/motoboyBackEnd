using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Threading.Tasks;
using APIBack.DTOs.Delivery;
using APIBack.DTOs.Configuracoes;
using APIBack.Repository.Interface;
using APIBack.Service;
using Dapper;
using Npgsql;

namespace APIBack.Repository
{
    public sealed class HorarioOperacaoRepository : IHorarioOperacaoRepository
    {
        private readonly NpgsqlDataSource _dataSource;
        private readonly TimeProvider _clock;

        public HorarioOperacaoRepository(NpgsqlDataSource dataSource, TimeProvider? clock = null)
        {
            _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
            _clock = clock ?? TimeProvider.System;
        }

        private static async Task<bool> TabelasExistemAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction = null, CancellationToken cancellationToken = default) =>
            await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT to_regclass('estabelecimento_horario') IS NOT NULL AND to_regclass('estabelecimento_horario_especial') IS NOT NULL;", transaction: transaction, cancellationToken: cancellationToken));

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
                    && horaAtual >= especial.Abre.Value && horaAtual < especial.Fecha.Value;
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
                && horaAtual >= dia.Abre.Value && horaAtual < dia.Fecha.Value;
        }

        public async Task<StoreOperationDto> ObterEstadoAsync(Guid estabelecimentoId, CancellationToken cancellationToken = default)
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
            return await LerEstadoAsync(connection, estabelecimentoId, null, cancellationToken);
        }

        private async Task<StoreOperationDto> LerEstadoAsync(NpgsqlConnection connection, Guid id, NpgsqlTransaction? transaction, CancellationToken cancellationToken)
        {
            var aceita = await connection.QuerySingleOrDefaultAsync<bool?>(new CommandDefinition(
                "SELECT COALESCE(aceita_pedidos, TRUE) FROM estabelecimentos WHERE id = @Id;", new { Id = id }, transaction, cancellationToken: cancellationToken));
            if (!aceita.HasValue) throw new DeliveryDomainException(404, "ESTABELECIMENTO_NOT_FOUND", "Estabelecimento não encontrado.");
            var now = _clock.GetUtcNow();
            var local = TimeZoneInfo.ConvertTimeFromUtc(now.UtcDateTime, ResolveTimeZone("America/Sao_Paulo"));
            var state = new StoreOperationDto
            {
                EstabelecimentoId = id, AceitaPedidos = aceita.Value, AgoraUtc = now,
                DataLocal = local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), HoraLocal = local.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
            };
            var tables = await TabelasExistemAsync(connection, transaction, cancellationToken);
            HorarioRow? row = null;
            if (tables)
            {
                row = await connection.QuerySingleOrDefaultAsync<HorarioRow>(new CommandDefinition(@"
SELECT fechado AS Fechado, abre_as AS Abre, fecha_as AS Fecha, origem AS Origem
FROM (
 SELECT fechado, abre_as, fecha_as, 'especial' AS origem, 0 AS prioridade FROM estabelecimento_horario_especial WHERE estabelecimento_id = @Id AND data = @Data
 UNION ALL
 SELECT fechado, abre_as, fecha_as, 'semanal' AS origem, 1 AS prioridade FROM estabelecimento_horario WHERE estabelecimento_id = @Id AND dia_semana = @Dia
) h ORDER BY prioridade LIMIT 1;", new { Id = id, Data = DateOnly.FromDateTime(local), Dia = ((int)local.DayOfWeek + 6) % 7 }, transaction, cancellationToken: cancellationToken));
            }
            state.Origem = row?.Origem ?? "sem_horario";
            state.AbreAs = row?.Abre?.ToString(@"hh\:mm");
            state.FechaAs = row?.Fecha?.ToString(@"hh\:mm");
            state.DentroDoHorario = row == null || (!row.Fechado && row.Abre.HasValue && row.Fecha.HasValue && local.TimeOfDay >= row.Abre.Value && local.TimeOfDay < row.Fecha.Value);
            state.AbertoAgora = state.AceitaPedidos && state.DentroDoHorario;
            return state;
        }

        public async Task<StoreOperationDto> AbrirHojeAsync(Guid estabelecimentoId, int actorUserId, OpenStoreTodayRequest request, CancellationToken cancellationToken = default)
        {
            if (request.EstabelecimentoId != estabelecimentoId)
                throw new DeliveryDomainException(409, "ESTABLISHMENT_CHANGED", "A loja selecionada mudou. Reabra o modal e tente novamente.");
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            // Serializa duas aberturas da mesma loja. Um segundo clique não troca o horário que acabou de ser confirmado.
            await connection.ExecuteAsync(new CommandDefinition("SELECT id FROM estabelecimentos WHERE id = @Id FOR UPDATE;", new { Id = estabelecimentoId }, transaction, cancellationToken: cancellationToken));
            var state = await LerEstadoAsync(connection, estabelecimentoId, transaction, cancellationToken);
            var local = TimeZoneInfo.ConvertTimeFromUtc(state.AgoraUtc.UtcDateTime, ResolveTimeZone(state.Timezone));
            if (request.DataLocal != state.DataLocal)
                throw new DeliveryDomainException(409, "DAY_CHANGED", "O dia mudou. Atualize o horário de fechamento para hoje.");
            if (!TimeOnly.TryParseExact(request.FechaAs, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var close) || close.ToTimeSpan() <= local.TimeOfDay)
                throw new DeliveryDomainException(422, "INVALID_CLOSING_TIME", "Escolha um horário de fechamento depois de agora, ainda hoje.");
            if (state.AbertoAgora)
            {
                await transaction.CommitAsync(cancellationToken);
                return state;
            }
            if (!await TabelasExistemAsync(connection, transaction, cancellationToken))
                throw new DeliveryDomainException(503, "MIGRATION_PENDING", "Os horários de funcionamento ainda não foram habilitados neste ambiente.");
            // Reutiliza a exceção por data; a grade semanal permanece intacta.
            await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO estabelecimento_horario_especial(estabelecimento_id, data, fechado, abre_as, fecha_as, motivo)
VALUES(@Id, @Data, FALSE, @Abre, @Fecha, @Motivo)
ON CONFLICT(estabelecimento_id, data) DO UPDATE SET fechado = FALSE, abre_as = EXCLUDED.abre_as, fecha_as = EXCLUDED.fecha_as, motivo = EXCLUDED.motivo;
UPDATE estabelecimentos SET aceita_pedidos = TRUE, data_atualizacao = NOW() WHERE id = @Id;", new
            {
                Id = estabelecimentoId, Data = DateOnly.FromDateTime(local), Abre = new TimeSpan(local.Hour, local.Minute, 0), Fecha = close.ToTimeSpan(),
                Motivo = $"Abertura pelo painel (usuário {actorUserId})"
            }, transaction, cancellationToken: cancellationToken));
            var result = await LerEstadoAsync(connection, estabelecimentoId, transaction, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
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
            public string? Origem { get; set; }
            public bool Fechado { get; set; }
            public TimeSpan? Abre { get; set; }
            public TimeSpan? Fecha { get; set; }
        }
    }
}
