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

        /// <summary>Fuso da loja (estabelecimentos.timezone_iana). Coluna ausente ou vazia: o alternativo, ou America/Sao_Paulo.</summary>
        private static async Task<string> LerFusoAsync(NpgsqlConnection connection, Guid id, string? alternativo,
            NpgsqlTransaction? transaction = null, CancellationToken cancellationToken = default)
        {
            try
            {
                var tz = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
                    "SELECT timezone_iana FROM estabelecimentos WHERE id = @Id;", new { Id = id }, transaction, cancellationToken: cancellationToken));
                if (!string.IsNullOrWhiteSpace(tz)) return tz.Trim();
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedColumn && transaction == null)
            {
                // Ambiente anterior ao fuso por loja: vale o alternativo.
            }
            return string.IsNullOrWhiteSpace(alternativo) ? "America/Sao_Paulo" : alternativo.Trim();
        }

        /// <summary>Grade semanal e excecoes (de ontem ate daqui a 8 dias) no modelo da regra unica.</summary>
        private static async Task<HorarioSemana> LerSemanaAsync(NpgsqlConnection connection, Guid id, DateOnly hoje,
            NpgsqlTransaction? transaction = null, CancellationToken cancellationToken = default)
        {
            var semanal = await connection.QueryAsync<(int Dia, bool Fechado, TimeSpan? Abre, TimeSpan? Fecha)>(new CommandDefinition(
                "SELECT dia_semana, fechado, abre_as, fecha_as FROM estabelecimento_horario WHERE estabelecimento_id = @Id;",
                new { Id = id }, transaction, cancellationToken: cancellationToken));
            var especiais = await connection.QueryAsync<(DateOnly Data, bool Fechado, TimeSpan? Abre, TimeSpan? Fecha)>(new CommandDefinition(
                @"SELECT data, fechado, abre_as, fecha_as FROM estabelecimento_horario_especial
 WHERE estabelecimento_id = @Id AND data BETWEEN @De AND @Ate;",
                new { Id = id, De = hoje.AddDays(-1), Ate = hoje.AddDays(8) }, transaction, cancellationToken: cancellationToken));
            var linhas = semanal.ToList();
            return new HorarioSemana
            {
                TemSemanal = linhas.Count > 0,
                // Nosso indice 0 = segunda; DayOfWeek 0 = domingo.
                Semanal = linhas.ToDictionary(r => (DayOfWeek)((r.Dia + 1) % 7), r => new HorarioDia(r.Fechado, r.Abre, r.Fecha)),
                Especiais = especiais.ToDictionary(r => r.Data, r => new HorarioDia(r.Fechado, r.Abre, r.Fecha))
            };
        }

        public async Task<bool> EstaAbertoAgoraAsync(Guid estabelecimentoId, DateTimeOffset agoraUtc, string? timezoneIana = null) =>
            (await AvaliarAberturaAsync(estabelecimentoId, agoraUtc, timezoneIana)).Dentro;

        public async Task<AberturaLoja> AvaliarAberturaAsync(Guid estabelecimentoId, DateTimeOffset agoraUtc, string? timezoneIana = null)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            var fuso = await LerFusoAsync(connection, estabelecimentoId, timezoneIana);
            // Sem as tabelas de horario nao ha horario cadastrado: fechada (nunca "aberta por padrao").
            if (!await TabelasExistemAsync(connection))
                return new AberturaLoja(false, HorarioRegras.SemHorario, null, fuso);
            var local = HorarioRegras.ParaHorarioLocal(agoraUtc.UtcDateTime, fuso);
            var semana = await LerSemanaAsync(connection, estabelecimentoId, DateOnly.FromDateTime(local));
            var abertura = HorarioRegras.Avaliar(semana, local);
            return new AberturaLoja(abertura.Dentro, abertura.Motivo, abertura.ProximaAberturaTexto, fuso);
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
            var fuso = await LerFusoAsync(connection, id, null, transaction, cancellationToken);
            var local = HorarioRegras.ParaHorarioLocal(now.UtcDateTime, fuso);
            var state = new StoreOperationDto
            {
                EstabelecimentoId = id, AceitaPedidos = aceita.Value, AgoraUtc = now, Timezone = fuso,
                DataLocal = local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), HoraLocal = local.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
            };
            HorarioSemana? semana = null;
            if (await TabelasExistemAsync(connection, transaction, cancellationToken))
                semana = await LerSemanaAsync(connection, id, DateOnly.FromDateTime(local), transaction, cancellationToken);

            var data = DateOnly.FromDateTime(local);
            var hoje = HorarioRegras.DiaEfetivo(semana, data);
            var ontem = HorarioRegras.DiaEfetivo(semana, data.AddDays(-1));
            var madrugadaDeOntem = ontem is { Aberto: true } && ontem.Fecha < ontem.Abre && local.TimeOfDay < ontem.Fecha;
            var exibido = madrugadaDeOntem ? ontem : hoje;
            var dataExibida = madrugadaDeOntem ? data.AddDays(-1) : data;
            state.Origem = exibido == null ? "sem_horario" : semana!.Especiais.ContainsKey(dataExibida) ? "especial" : "semanal";
            state.AbreAs = exibido?.Abre?.ToString(@"hh\:mm");
            state.FechaAs = exibido?.Fecha?.ToString(@"hh\:mm");

            var abertura = HorarioRegras.Avaliar(semana, local);
            state.DentroDoHorario = abertura.Dentro;
            state.AbertoAgora = state.AceitaPedidos && state.DentroDoHorario;
            state.ProximaAbertura = abertura.ProximaAberturaTexto;
            state.PodeFechar = state.AbertoAgora;
            // Fechada: diz por que e como resolver (o servico de operacao refina "podeAbrir" com a situacao comercial e a prontidao).
            state.MotivoFechada = state.AbertoAgora ? null : !abertura.Dentro ? abertura.Motivo : HorarioRegras.Pausada;
            state.PodeAbrir = !state.AbertoAgora;
            if (state.MotivoFechada != null)
            {
                var erro = ErrosOperacionais.Obter(state.MotivoFechada switch
                {
                    HorarioRegras.SemHorario => ErrosOperacionais.LojaSemHorario,
                    HorarioRegras.ForaHorario => ErrosOperacionais.LojaForaHorario,
                    _ => ErrosOperacionais.StoreNotAccepting
                });
                state.ComoResolver = erro.ComoResolver;
                state.Correcao = erro.Correcao;
            }
            return state;
        }

        public static bool HorarioAbertoEm(TimeSpan? abreHoje, TimeSpan? fechaHoje, bool fechadoHoje,
            TimeSpan? abreOntem, TimeSpan? fechaOntem, bool fechadoOntem, TimeSpan hora) =>
            HorarioRegras.AbertoEm(new HorarioDia(fechadoHoje, abreHoje, fechaHoje), new HorarioDia(fechadoOntem, abreOntem, fechaOntem), hora);

        public async Task<StoreOperationDto> AbrirHojeAsync(Guid estabelecimentoId, int actorUserId, OpenStoreTodayRequest request, CancellationToken cancellationToken = default)
        {
            if (request.EstabelecimentoId != estabelecimentoId)
                throw new DeliveryDomainException(409, "ESTABLISHMENT_CHANGED", "A loja selecionada mudou. Reabra o modal e tente novamente.");
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            // Serializa duas aberturas da mesma loja. Um segundo clique não troca o horário que acabou de ser confirmado.
            await connection.ExecuteAsync(new CommandDefinition("SELECT id FROM estabelecimentos WHERE id = @Id FOR UPDATE;", new { Id = estabelecimentoId }, transaction, cancellationToken: cancellationToken));
            var state = await LerEstadoAsync(connection, estabelecimentoId, transaction, cancellationToken);
            var local = HorarioRegras.ParaHorarioLocal(state.AgoraUtc.UtcDateTime, state.Timezone);
            if (request.DataLocal != state.DataLocal)
                throw new DeliveryDomainException(409, "DAY_CHANGED", "O dia mudou. Atualize o horário de fechamento para hoje.");
            if (!TimeOnly.TryParseExact(request.FechaAs, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var close) || close.ToTimeSpan() == local.TimeOfDay)
                throw new DeliveryDomainException(422, "INVALID_CLOSING_TIME", "Escolha um horário de fechamento diferente de agora.");
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

        public async Task<StoreOperationDto> FecharAgoraAsync(Guid estabelecimentoId, int actorUserId, CancellationToken cancellationToken = default)
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            // Mesma trava da abertura: abrir e fechar da mesma loja nunca se cruzam.
            await connection.ExecuteAsync(new CommandDefinition("SELECT id FROM estabelecimentos WHERE id = @Id FOR UPDATE;", new { Id = estabelecimentoId }, transaction, cancellationToken: cancellationToken));
            var state = await LerEstadoAsync(connection, estabelecimentoId, transaction, cancellationToken);
            if (!state.DentroDoHorario)
            {
                // Ja fechada pelo horario: fechar de novo nao muda nada.
                await transaction.CommitAsync(cancellationToken);
                return state;
            }
            if (!await TabelasExistemAsync(connection, transaction, cancellationToken))
                throw new DeliveryDomainException(503, "MIGRATION_PENDING", "Os horários de funcionamento ainda não foram habilitados neste ambiente.");

            var local = HorarioRegras.ParaHorarioLocal(state.AgoraUtc.UtcDateTime, state.Timezone);
            var semana = await LerSemanaAsync(connection, estabelecimentoId, DateOnly.FromDateTime(local), transaction, cancellationToken);
            var plano = HorarioRegras.PlanejarFechamento(semana, local);
            if (plano != null)
            {
                await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO estabelecimento_horario_especial(estabelecimento_id, data, fechado, abre_as, fecha_as, motivo)
VALUES(@Id, @Data, @Fechado, @Abre, @Fecha, @Motivo)
ON CONFLICT(estabelecimento_id, data) DO UPDATE SET fechado = EXCLUDED.fechado, abre_as = EXCLUDED.abre_as, fecha_as = EXCLUDED.fecha_as, motivo = EXCLUDED.motivo;",
                    new { Id = estabelecimentoId, plano.Data, plano.Fechado, plano.Abre, plano.Fecha, Motivo = $"Fechamento pelo painel (usuário {actorUserId})" },
                    transaction, cancellationToken: cancellationToken));
            }
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
                if (!TimeSpan.TryParse(request.AbreAs, out var abreValue) || !TimeSpan.TryParse(request.FechaAs, out var fechaValue) || fechaValue == abreValue)
                {
                    throw new DeliveryDomainException(422, "INVALID_REQUEST", "Informe abertura e fechamento em horários diferentes.");
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
