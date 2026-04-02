using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using APIBack.DTOs.Agendamentos;
using APIBack.DTOs.Common;
using APIBack.Model;
using APIBack.Repository.Interface;
using APIBack.Service.Interface;

namespace APIBack.Service
{
    public class AgendaDisponibilidadeService : IAgendaDisponibilidadeService
    {
        private static readonly HashSet<string> EscoposPermitidos = new(StringComparer.OrdinalIgnoreCase)
        {
            "estabelecimento",
            "profissional"
        };

        private static readonly HashSet<string> TiposPermitidos = new(StringComparer.OrdinalIgnoreCase)
        {
            "disponibilidade_semanal",
            "disponibilidade_data",
            "bloqueio_data"
        };

        private readonly IAgendaDisponibilidadeRepository _repository;

        public AgendaDisponibilidadeService(IAgendaDisponibilidadeRepository repository)
        {
            _repository = repository;
        }

        public async Task<PagedResultDto<AgendaDisponibilidadeDto>> ListarAsync(
            Guid idEstabelecimento,
            bool? ativo,
            string? tipo,
            string? escopo,
            string? profissionalId,
            int page,
            int pageSize)
        {
            var normalizedPage = ValidationUtils.NormalizePage(page);
            var normalizedPageSize = ValidationUtils.NormalizePageSize(pageSize);
            var parsedProfissionalId = ParseNullableProfissionalId(profissionalId, false);
            var result = await _repository.ListarAsync(
                idEstabelecimento,
                ativo,
                ValidationUtils.TrimToNull(tipo),
                ValidationUtils.TrimToNull(escopo),
                parsedProfissionalId,
                normalizedPage,
                normalizedPageSize);

            return new PagedResultDto<AgendaDisponibilidadeDto>
            {
                Itens = result.Itens.Select(Map).ToArray(),
                Total = result.Total
            };
        }

        public async Task<IReadOnlyCollection<AgendaDisponibilidadeDto>> ListarTodasAsync(Guid idEstabelecimento)
        {
            var itens = await _repository.ListarTodasAsync(idEstabelecimento);
            return itens.Select(Map).ToArray();
        }

        public async Task<AgendaDisponibilidadeDto?> ObterPorIdAsync(Guid idEstabelecimento, Guid id)
        {
            var item = await _repository.ObterPorIdAsync(idEstabelecimento, id);
            return item == null ? null : Map(item);
        }

        public async Task<AgendaDisponibilidadeDto> CriarAsync(Guid idEstabelecimento, SalvarAgendaDisponibilidadeRequest request)
        {
            var entity = await BuildEntityAsync(idEstabelecimento, request);
            if (entity.Ativo && await _repository.ExisteConflitoAsync(idEstabelecimento, entity))
            {
                throw new InvalidOperationException("Ja existe uma regra conflitante para esse periodo.");
            }

            entity.Id = await _repository.CriarAsync(entity);
            return Map(entity);
        }

        public async Task<AgendaDisponibilidadeDto> AtualizarAsync(Guid idEstabelecimento, Guid id, SalvarAgendaDisponibilidadeRequest request)
        {
            var current = await _repository.ObterPorIdAsync(idEstabelecimento, id)
                ?? throw new KeyNotFoundException("Registro nao encontrado.");

            var entity = await BuildEntityAsync(idEstabelecimento, request);
            entity.Id = id;
            entity.CreatedAt = current.CreatedAt;
            entity.UpdatedAt = current.UpdatedAt;

            if (entity.Ativo && await _repository.ExisteConflitoAsync(idEstabelecimento, entity))
            {
                throw new InvalidOperationException("Ja existe uma regra conflitante para esse periodo.");
            }

            var updated = await _repository.AtualizarAsync(entity);
            if (!updated)
            {
                throw new KeyNotFoundException("Registro nao encontrado.");
            }

            return Map(entity);
        }

        public Task<bool> AtualizarStatusAsync(Guid idEstabelecimento, Guid id, bool ativo)
            => _repository.AtualizarStatusAsync(idEstabelecimento, id, ativo);

        public Task<bool> ExcluirAsync(Guid idEstabelecimento, Guid id)
            => _repository.ExcluirAsync(idEstabelecimento, id);

        public async Task<PagedResultDto<ProfissionalDisponibilidadeDto>> ListarProfissionaisAsync(Guid idEstabelecimento)
        {
            var rows = await _repository.ListarProfissionaisAsync(idEstabelecimento);
            return new PagedResultDto<ProfissionalDisponibilidadeDto>
            {
                Itens = rows.Select(x => new ProfissionalDisponibilidadeDto
                {
                    Id = x.Id.ToString(),
                    Nome = x.Nome,
                    Ativo = x.Ativo
                }).ToArray(),
                Total = rows.Count
            };
        }

        private async Task<AgendaDisponibilidade> BuildEntityAsync(Guid idEstabelecimento, SalvarAgendaDisponibilidadeRequest request)
        {
            var errors = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var escopo = ValidationUtils.NormalizeToken(request.Escopo);
            var tipo = ValidationUtils.NormalizeToken(request.Tipo);
            var parsedProfissionalId = ParseNullableProfissionalId(request.ProfissionalId, true);
            var diasSemana = (request.DiasSemana ?? new List<int>())
                .Distinct()
                .OrderBy(x => x)
                .ToList();

            if (!EscoposPermitidos.Contains(escopo))
            {
                ValidationUtils.AddError(errors, "escopo", "Escopo invalido.");
            }

            if (!TiposPermitidos.Contains(tipo))
            {
                ValidationUtils.AddError(errors, "tipo", "Tipo invalido.");
            }

            if (string.Equals(escopo, "profissional", StringComparison.OrdinalIgnoreCase))
            {
                if (!parsedProfissionalId.HasValue)
                {
                    ValidationUtils.AddError(errors, "profissionalId", "Profissional e obrigatorio para escopo profissional.");
                }
                else if (!await _repository.ProfissionalExisteNoEstabelecimentoAsync(idEstabelecimento, parsedProfissionalId.Value))
                {
                    ValidationUtils.AddError(errors, "profissionalId", "Profissional nao pertence ao estabelecimento atual.");
                }
            }
            else if (!string.IsNullOrWhiteSpace(request.ProfissionalId))
            {
                ValidationUtils.AddError(errors, "profissionalId", "Profissional so pode ser informado no escopo profissional.");
            }

            if (tipo == "disponibilidade_semanal")
            {
                if (diasSemana.Count == 0)
                {
                    ValidationUtils.AddError(errors, "diasSemana", "Informe ao menos um dia da semana.");
                }

                if (diasSemana.Any(x => x < 0 || x > 6))
                {
                    ValidationUtils.AddError(errors, "diasSemana", "Dias da semana devem estar entre 0 e 6.");
                }
            }

            if (tipo == "disponibilidade_data" || tipo == "bloqueio_data")
            {
                if (!request.DataInicio.HasValue || !request.DataFim.HasValue)
                {
                    ValidationUtils.AddError(errors, "dataInicio", "Data inicio e data fim sao obrigatorias.");
                }
            }

            if (request.DataInicio.HasValue && request.DataFim.HasValue && request.DataFim.Value < request.DataInicio.Value)
            {
                ValidationUtils.AddError(errors, "dataFim", "Data fim deve ser maior ou igual a data inicio.");
            }

            var horasObrigatorias = tipo != "bloqueio_data" || !request.DiaInteiro;
            if (horasObrigatorias)
            {
                if (!request.HoraInicio.HasValue)
                {
                    ValidationUtils.AddError(errors, "horaInicio", "Hora inicio e obrigatoria.");
                }

                if (!request.HoraFim.HasValue)
                {
                    ValidationUtils.AddError(errors, "horaFim", "Hora fim e obrigatoria.");
                }
            }

            if (request.HoraInicio.HasValue ^ request.HoraFim.HasValue)
            {
                ValidationUtils.AddError(errors, "horaInicio", "Hora inicio e hora fim devem ser informadas juntas.");
            }

            if (request.HoraInicio.HasValue && request.HoraFim.HasValue && request.HoraFim.Value <= request.HoraInicio.Value)
            {
                ValidationUtils.AddError(errors, "horaFim", "Hora fim deve ser maior que hora inicio.");
            }

            ValidationUtils.ThrowIfAny(errors);

            return new AgendaDisponibilidade
            {
                IdEstabelecimento = idEstabelecimento,
                Escopo = escopo,
                ProfissionalId = escopo == "profissional" ? parsedProfissionalId : null,
                Tipo = tipo,
                DiasSemana = tipo == "disponibilidade_semanal" ? diasSemana : new List<int>(),
                DataInicio = tipo == "disponibilidade_semanal" ? null : request.DataInicio,
                DataFim = tipo == "disponibilidade_semanal" ? null : request.DataFim,
                HoraInicio = request.DiaInteiro ? null : request.HoraInicio,
                HoraFim = request.DiaInteiro ? null : request.HoraFim,
                DiaInteiro = request.DiaInteiro,
                Ativo = request.Ativo,
                Observacao = ValidationUtils.TrimToNull(request.Observacao)
            };
        }

        private static long? ParseNullableProfissionalId(string? value, bool addValidation)
        {
            var normalized = ValidationUtils.TrimToNull(value);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return null;
            }

            if (long.TryParse(normalized, out var parsed) && parsed > 0)
            {
                return parsed;
            }

            if (addValidation)
            {
                throw new RequestValidationException("Dados invalidos.", new Dictionary<string, List<string>>
                {
                    ["profissionalId"] = new List<string> { "Profissional invalido." }
                });
            }

            return null;
        }

        private static AgendaDisponibilidadeDto Map(AgendaDisponibilidade entity)
        {
            return new AgendaDisponibilidadeDto
            {
                Id = entity.Id,
                EstabelecimentoId = entity.IdEstabelecimento,
                Escopo = entity.Escopo,
                ProfissionalId = entity.ProfissionalId?.ToString(),
                Tipo = entity.Tipo,
                DiasSemana = entity.DiasSemana,
                DataInicio = entity.DataInicio,
                DataFim = entity.DataFim,
                HoraInicio = entity.HoraInicio,
                HoraFim = entity.HoraFim,
                DiaInteiro = entity.DiaInteiro,
                Ativo = entity.Ativo,
                Observacao = entity.Observacao,
                CreatedAt = entity.CreatedAt,
                UpdatedAt = entity.UpdatedAt,
                DeletedAt = entity.DeletedAt
            };
        }
    }
}
