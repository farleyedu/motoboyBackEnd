using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using APIBack.Automation.Helpers;
using APIBack.DTOs.Agendamentos;
using APIBack.Model;
using APIBack.Repository.Interface;
using APIBack.Service.Interface;
using Microsoft.Extensions.Logging;

namespace APIBack.Service
{
    public class OficinaAgendamentoService : IOficinaAgendamentoService
    {
        private readonly IOficinaAgendamentoRepository _repository;
        private readonly IEstabelecimentoAgendamentoConfigService _configService;
        private readonly IAgendaDisponibilidadeService _disponibilidadeService;
        private readonly ILogger<OficinaAgendamentoService> _logger;

        public OficinaAgendamentoService(
            IOficinaAgendamentoRepository repository,
            IEstabelecimentoAgendamentoConfigService configService,
            IAgendaDisponibilidadeService disponibilidadeService,
            ILogger<OficinaAgendamentoService> logger)
        {
            _repository = repository;
            _configService = configService;
            _disponibilidadeService = disponibilidadeService;
            _logger = logger;
        }

        public async Task<IReadOnlyCollection<OficinaSlotDto>> BuscarSlotsAsync(Guid idEstabelecimento, Guid? idServico, DateTime data, int duracaoMinutos, long? idProfissional = null, int limite = 6)
        {
            var config = await _configService.ObterAsync(idEstabelecimento);
            _logger.FlowInfo("AGENDA_CONFIG_LOADED", estabelecimentoId: idEstabelecimento, resultado: config.AgendaAtiva ? "ativa" : "inativa",
                extra: new[] { ("duracaoSlot", (object?)config.DuracaoSlotMinutos), ("antecedenciaMinimaHoras", config.AntecedenciaMinimaHoras), ("antecedenciaMaximaDias", config.AntecedenciaMaximaDias) });

            if (!config.AgendaAtiva)
            {
                _logger.FlowWarning("AGENDA_SLOT_EMPTY", estabelecimentoId: idEstabelecimento, resultado: "empty", motivo: "agenda_inativa");
                return Array.Empty<OficinaSlotDto>();
            }

            var agora = DateTime.UtcNow;
            var dataLimiteMinima = agora.AddHours(config.AntecedenciaMinimaHoras);
            var dataLimiteMaxima = agora.Date.AddDays(config.AntecedenciaMaximaDias);
            if (data.Date < dataLimiteMinima.Date || data.Date > dataLimiteMaxima.Date)
            {
                _logger.FlowWarning("AGENDA_SLOT_EMPTY", estabelecimentoId: idEstabelecimento, resultado: "empty", motivo: "fora_antecedencia",
                    extra: new[] { ("data", (object?)data.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) });
                return Array.Empty<OficinaSlotDto>();
            }

            var regras = (await _disponibilidadeService.ListarTodasAsync(idEstabelecimento)).Where(r => r.Ativo).ToList();
            _logger.FlowInfo("AGENDA_RULES_LOADED", estabelecimentoId: idEstabelecimento, resultado: "ok",
                extra: new[] { ("regrasAtivas", (object?)regras.Count), ("bloqueios", regras.Count(r => r.Tipo == "bloqueio_data")) });

            _logger.FlowInfo("AGENDA_SLOT_SEARCH", estabelecimentoId: idEstabelecimento, acao: "buscar_slots",
                extra: new[] { ("data", (object?)data.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), ("duracao", duracaoMinutos), ("profissionalId", idProfissional) });

            var slots = new List<OficinaSlotDto>();
            var diaIndice = ToContratoDiaSemana(data.DayOfWeek);
            var disponibilidadeSemanal = regras
                .Where(r => r.Tipo == "disponibilidade_semanal" && r.DiasSemana.Contains(diaIndice))
                .Where(r => RegraCompativelComProfissional(r, idProfissional))
                .ToList();
            var disponibilidadeData = regras
                .Where(r => r.Tipo == "disponibilidade_data" && DataDentro(r, data))
                .Where(r => RegraCompativelComProfissional(r, idProfissional))
                .ToList();
            var bloqueios = regras
                .Where(r => r.Tipo == "bloqueio_data" && DataDentro(r, data))
                .Where(r => RegraCompativelComProfissional(r, idProfissional))
                .ToList();

            var bases = disponibilidadeData.Count > 0 ? disponibilidadeData : disponibilidadeSemanal;
            var duracao = TimeSpan.FromMinutes(Math.Max(1, duracaoMinutos));
            var incremento = TimeSpan.FromMinutes(Math.Max(1, config.DuracaoSlotMinutos + config.IntervaloEntreSlotsMinutos));

            foreach (var regra in bases)
            {
                if (!regra.HoraInicio.HasValue || !regra.HoraFim.HasValue)
                {
                    continue;
                }

                var inicio = regra.HoraInicio.Value.ToTimeSpan();
                var fim = regra.HoraFim.Value.ToTimeSpan();
                for (var cursor = inicio; cursor + duracao <= fim; cursor = cursor.Add(incremento))
                {
                    var horaFim = cursor + duracao;
                    if (data.Date == dataLimiteMinima.Date && data.Date.Add(cursor) < dataLimiteMinima)
                    {
                        continue;
                    }

                    if (Bloqueado(bloqueios, cursor, horaFim))
                    {
                        continue;
                    }

                    var conflitos = await _repository.ContarConflitosAsync(idEstabelecimento, data, cursor, horaFim, idProfissional);
                    if (conflitos >= Math.Max(1, config.LimitePorSlot))
                    {
                        continue;
                    }

                    slots.Add(new OficinaSlotDto
                    {
                        Data = data.Date,
                        HoraInicio = cursor.ToString(@"hh\:mm", CultureInfo.InvariantCulture),
                        HoraFim = horaFim.ToString(@"hh\:mm", CultureInfo.InvariantCulture),
                        ProfissionalId = idProfissional
                    });

                    if (slots.Count >= Math.Max(1, limite))
                    {
                        break;
                    }
                }

                if (slots.Count >= Math.Max(1, limite))
                {
                    break;
                }
            }

            _logger.FlowInfo(slots.Count == 0 ? "AGENDA_SLOT_EMPTY" : "AGENDA_SLOT_RESULT", estabelecimentoId: idEstabelecimento, resultado: slots.Count == 0 ? "empty" : "ok",
                motivo: slots.Count == 0 ? "sem_disponibilidade" : null,
                extra: new[] { ("slotsEncontrados", (object?)slots.Count), ("primeiroSlot", slots.FirstOrDefault()?.HoraInicio), ("ultimoSlot", slots.LastOrDefault()?.HoraInicio) });

            return slots;
        }

        public async Task<OficinaAgendamentoDto> CriarAsync(Guid idEstabelecimento, Guid idCliente, string telefoneE164, CriarOficinaAgendamentoRequest request)
        {
            if (!TryParseHora(request.HoraInicio, out var horaInicio))
            {
                throw new InvalidOperationException("Horario de inicio invalido.");
            }

            var duracaoMinutos = Math.Max(1, request.DuracaoMinutos ?? 60);
            var horaFim = horaInicio.Add(TimeSpan.FromMinutes(duracaoMinutos));
            var slots = await BuscarSlotsAsync(idEstabelecimento, request.ServicoId, request.DataAgendamento, duracaoMinutos, request.ProfissionalId, limite: 32);
            var slotExiste = slots.Any(s => string.Equals(s.HoraInicio, horaInicio.ToString(@"hh\:mm", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase));
            if (!slotExiste)
            {
                _logger.FlowWarning("AGENDAMENTO_CREATE_FAILED", conversationId: request.ConversaId, estabelecimentoId: idEstabelecimento, clienteId: idCliente, resultado: "failed", motivo: "slot_indisponivel");
                throw new InvalidOperationException("Horario indisponivel para agendamento.");
            }

            var conflitos = await _repository.ContarConflitosAsync(idEstabelecimento, request.DataAgendamento, horaInicio, horaFim, request.ProfissionalId);
            if (conflitos > 0)
            {
                _logger.FlowWarning("AGENDAMENTO_CREATE_FAILED", conversationId: request.ConversaId, estabelecimentoId: idEstabelecimento, clienteId: idCliente, resultado: "failed", motivo: "slot_ocupado");
                throw new InvalidOperationException("Esse horario acabou de ser ocupado. Escolha outro horario.");
            }

            var agora = DateTime.UtcNow;
            var agendamento = new OficinaAgendamento
            {
                Id = Guid.NewGuid(),
                IdEstabelecimento = idEstabelecimento,
                IdCliente = idCliente,
                IdConversa = request.ConversaId,
                IdAtendimentoServico = request.AtendimentoServicoId,
                IdServico = request.ServicoId,
                IdProfissional = request.ProfissionalId,
                NomeCliente = request.NomeCliente,
                TelefoneE164 = telefoneE164,
                NomeServico = request.NomeServico.Trim(),
                VeiculoMarca = request.VeiculoMarca,
                VeiculoModelo = request.VeiculoModelo,
                MarcaPeca = request.MarcaPeca,
                DataAgendamento = request.DataAgendamento.Date,
                HoraInicio = horaInicio,
                HoraFim = horaFim,
                Status = "confirmado",
                Codigo = await _repository.GerarCodigoUnicoAsync(idEstabelecimento),
                Observacao = request.Observacao,
                DadosExtras = request.DadosExtras ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase),
                DataCriacao = agora,
                DataAtualizacao = agora
            };

            await _repository.CriarAsync(agendamento);
            _logger.FlowInfo("AGENDAMENTO_CREATED", conversationId: request.ConversaId, estabelecimentoId: idEstabelecimento, clienteId: idCliente, resultado: "ok",
                extra: new[] { ("agendamentoId", (object?)agendamento.Id), ("codigo", agendamento.Codigo), ("data", agendamento.DataAgendamento.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), ("hora", agendamento.HoraInicio.ToString(@"hh\:mm", CultureInfo.InvariantCulture)) });

            return Map(agendamento);
        }

        public async Task<IReadOnlyCollection<OficinaAgendamentoDto>> ListarAtivosPorClienteAsync(Guid idEstabelecimento, Guid idCliente, string? telefoneE164)
            => (await _repository.ListarAtivosPorClienteAsync(idEstabelecimento, idCliente, telefoneE164)).Select(Map).ToList();

        public async Task<IReadOnlyCollection<OficinaAgendamentoDto>> ListarPorPeriodoAsync(Guid idEstabelecimento, DateTime dataInicio, DateTime dataFim, string? status, long? idProfissional, Guid? idServico)
            => (await _repository.ListarPorPeriodoAsync(idEstabelecimento, dataInicio, dataFim, status, idProfissional, idServico)).Select(Map).ToList();

        public async Task<OficinaAgendamentoDto> RemarcarAsync(Guid idEstabelecimento, Guid id, RemarcarOficinaAgendamentoRequest request)
        {
            var agendamento = await _repository.ObterPorIdAsync(idEstabelecimento, id)
                ?? throw new KeyNotFoundException("Agendamento nao encontrado.");

            if (!TryParseHora(request.HoraInicio, out var horaInicio))
            {
                throw new InvalidOperationException("Horario de inicio invalido.");
            }

            var duracaoMinutos = Math.Max(1, request.DuracaoMinutos ?? (int)Math.Max(1, (agendamento.HoraFim - agendamento.HoraInicio).TotalMinutes));
            var horaFim = horaInicio.Add(TimeSpan.FromMinutes(duracaoMinutos));
            var conflitos = await _repository.ContarConflitosAsync(idEstabelecimento, request.DataAgendamento, horaInicio, horaFim, request.ProfissionalId ?? agendamento.IdProfissional, ignorarId: id);
            if (conflitos > 0)
            {
                _logger.FlowWarning("AGENDAMENTO_OPERATION_FAILED", estabelecimentoId: idEstabelecimento, clienteId: agendamento.IdCliente, resultado: "failed", motivo: "slot_ocupado",
                    extra: new[] { ("agendamentoId", (object?)id) });
                throw new InvalidOperationException("Horario indisponivel para remarcacao.");
            }

            agendamento.DataAgendamento = request.DataAgendamento.Date;
            agendamento.HoraInicio = horaInicio;
            agendamento.HoraFim = horaFim;
            agendamento.IdProfissional = request.ProfissionalId ?? agendamento.IdProfissional;
            agendamento.Status = "remarcado";
            agendamento.DataAtualizacao = DateTime.UtcNow;
            await _repository.AtualizarAsync(agendamento);

            _logger.FlowInfo("AGENDAMENTO_CHANGED", conversationId: agendamento.IdConversa, estabelecimentoId: idEstabelecimento, clienteId: agendamento.IdCliente, resultado: "ok",
                extra: new[] { ("agendamentoId", (object?)id), ("data", agendamento.DataAgendamento.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), ("hora", agendamento.HoraInicio.ToString(@"hh\:mm", CultureInfo.InvariantCulture)) });

            return Map(agendamento);
        }

        public async Task<OficinaAgendamentoDto> CancelarAsync(Guid idEstabelecimento, Guid id, string? motivo)
        {
            var agendamento = await _repository.ObterPorIdAsync(idEstabelecimento, id)
                ?? throw new KeyNotFoundException("Agendamento nao encontrado.");

            agendamento.Status = "cancelado";
            agendamento.DataCancelamento = DateTime.UtcNow;
            agendamento.DataAtualizacao = DateTime.UtcNow;
            if (!string.IsNullOrWhiteSpace(motivo))
            {
                agendamento.DadosExtras["motivo_cancelamento"] = motivo.Trim();
            }

            await _repository.AtualizarAsync(agendamento);
            _logger.FlowInfo("AGENDAMENTO_CANCELLED", conversationId: agendamento.IdConversa, estabelecimentoId: idEstabelecimento, clienteId: agendamento.IdCliente, resultado: "ok",
                extra: new[] { ("agendamentoId", (object?)id), ("motivoCancelamento", motivo) });
            return Map(agendamento);
        }

        public async Task<OficinaAgendamentoDto?> ObterPorCodigoAsync(Guid idEstabelecimento, string codigo)
        {
            var item = await _repository.ObterPorCodigoAsync(idEstabelecimento, codigo);
            return item == null ? null : Map(item);
        }

        private static bool DataDentro(AgendaDisponibilidadeDto regra, DateTime data)
        {
            var date = DateOnly.FromDateTime(data.Date);
            return (!regra.DataInicio.HasValue || regra.DataInicio.Value <= date) &&
                   (!regra.DataFim.HasValue || regra.DataFim.Value >= date);
        }

        private static bool RegraCompativelComProfissional(AgendaDisponibilidadeDto regra, long? idProfissional)
        {
            if (string.Equals(regra.Escopo, "estabelecimento", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!idProfissional.HasValue)
            {
                return true;
            }

            return string.Equals(regra.ProfissionalId, idProfissional.Value.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
        }

        private static bool Bloqueado(IEnumerable<AgendaDisponibilidadeDto> bloqueios, TimeSpan horaInicio, TimeSpan horaFim)
        {
            foreach (var bloqueio in bloqueios)
            {
                if (bloqueio.DiaInteiro)
                {
                    return true;
                }

                if (!bloqueio.HoraInicio.HasValue || !bloqueio.HoraFim.HasValue)
                {
                    continue;
                }

                var bloqueioInicio = bloqueio.HoraInicio.Value.ToTimeSpan();
                var bloqueioFim = bloqueio.HoraFim.Value.ToTimeSpan();
                if (bloqueioFim > horaInicio && horaFim > bloqueioInicio)
                {
                    return true;
                }
            }

            return false;
        }

        private static int ToContratoDiaSemana(DayOfWeek dayOfWeek)
            => dayOfWeek switch
            {
                DayOfWeek.Monday => 0,
                DayOfWeek.Tuesday => 1,
                DayOfWeek.Wednesday => 2,
                DayOfWeek.Thursday => 3,
                DayOfWeek.Friday => 4,
                DayOfWeek.Saturday => 5,
                DayOfWeek.Sunday => 6,
                _ => 0
            };

        private static bool TryParseHora(string? value, out TimeSpan hora)
        {
            value = value?.Trim() ?? string.Empty;
            return TimeSpan.TryParseExact(value, new[] { @"hh\:mm", @"h\:mm", @"hh\:mm\:ss" }, CultureInfo.InvariantCulture, out hora);
        }

        private static OficinaAgendamentoDto Map(OficinaAgendamento item)
        {
            return new OficinaAgendamentoDto
            {
                Id = item.Id,
                EstabelecimentoId = item.IdEstabelecimento,
                ClienteId = item.IdCliente,
                ConversaId = item.IdConversa,
                AtendimentoServicoId = item.IdAtendimentoServico,
                ServicoId = item.IdServico,
                ProfissionalId = item.IdProfissional,
                NomeCliente = item.NomeCliente,
                TelefoneE164 = item.TelefoneE164,
                NomeServico = item.NomeServico,
                VeiculoMarca = item.VeiculoMarca,
                VeiculoModelo = item.VeiculoModelo,
                MarcaPeca = item.MarcaPeca,
                DataAgendamento = item.DataAgendamento,
                HoraInicio = item.HoraInicio.ToString(@"hh\:mm", CultureInfo.InvariantCulture),
                HoraFim = item.HoraFim.ToString(@"hh\:mm", CultureInfo.InvariantCulture),
                Status = item.Status,
                Codigo = item.Codigo,
                Observacao = item.Observacao,
                DadosExtras = item.DadosExtras,
                DataCriacao = item.DataCriacao,
                DataAtualizacao = item.DataAtualizacao,
                DataCancelamento = item.DataCancelamento
            };
        }
    }
}
