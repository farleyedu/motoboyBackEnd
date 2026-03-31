using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.DTOs.Agendamentos;
using APIBack.Model;
using APIBack.Repository.Interface;
using APIBack.Service.Interface;

namespace APIBack.Service
{
    public class EstabelecimentoAgendamentoConfigService : IEstabelecimentoAgendamentoConfigService
    {
        private readonly IEstabelecimentoAgendamentoConfigRepository _repository;

        public EstabelecimentoAgendamentoConfigService(IEstabelecimentoAgendamentoConfigRepository repository)
        {
            _repository = repository;
        }

        public async Task<EstabelecimentoAgendamentoConfigDto> ObterAsync(Guid idEstabelecimento)
        {
            var item = await _repository.ObterAsync(idEstabelecimento) ?? BuildDefault(idEstabelecimento);
            return Map(item);
        }

        public async Task<EstabelecimentoAgendamentoConfigDto> SalvarAsync(Guid idEstabelecimento, SalvarEstabelecimentoAgendamentoConfigRequest request)
        {
            var entity = BuildEntity(idEstabelecimento, request);
            await _repository.SalvarAsync(entity);
            return Map(entity);
        }

        private static EstabelecimentoAgendamentoConfig BuildDefault(Guid idEstabelecimento)
        {
            return new EstabelecimentoAgendamentoConfig
            {
                IdEstabelecimento = idEstabelecimento,
                AgendaAtiva = false,
                ExigeServico = false,
                ExigeProfissional = false,
                PermiteEncaixe = false,
                AgendaInformativo = false,
                DuracaoSlotMinutos = 30,
                IntervaloEntreSlotsMinutos = 0,
                LimitePorSlot = 1,
                AntecedenciaMinimaHoras = 0,
                AntecedenciaMaximaDias = 30,
                UpdatedAt = DateTime.UtcNow
            };
        }

        private static EstabelecimentoAgendamentoConfig BuildEntity(Guid idEstabelecimento, SalvarEstabelecimentoAgendamentoConfigRequest request)
        {
            var errors = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            if (request.DuracaoSlotMinutos <= 0)
            {
                ValidationUtils.AddError(errors, "duracaoSlotMinutos", "Duracao do slot deve ser maior que zero.");
            }

            if (request.IntervaloEntreSlotsMinutos < 0)
            {
                ValidationUtils.AddError(errors, "intervaloEntreSlotsMinutos", "Intervalo deve ser maior ou igual a zero.");
            }

            if (request.LimitePorSlot < 1)
            {
                ValidationUtils.AddError(errors, "limitePorSlot", "Limite por slot deve ser maior ou igual a 1.");
            }

            if (request.AntecedenciaMinimaHoras < 0)
            {
                ValidationUtils.AddError(errors, "antecedenciaMinimaHoras", "Antecedencia minima deve ser maior ou igual a zero.");
            }

            if (request.AntecedenciaMaximaDias < 0)
            {
                ValidationUtils.AddError(errors, "antecedenciaMaximaDias", "Antecedencia maxima deve ser maior ou igual a zero.");
            }

            ValidationUtils.ThrowIfAny(errors);

            return new EstabelecimentoAgendamentoConfig
            {
                IdEstabelecimento = idEstabelecimento,
                AgendaAtiva = request.AgendaAtiva,
                ExigeServico = request.ExigeServico,
                ExigeProfissional = request.ExigeProfissional,
                PermiteEncaixe = request.PermiteEncaixe,
                AgendaInformativo = request.AgendaInformativo,
                DuracaoSlotMinutos = request.DuracaoSlotMinutos,
                IntervaloEntreSlotsMinutos = request.IntervaloEntreSlotsMinutos,
                LimitePorSlot = request.LimitePorSlot,
                AntecedenciaMinimaHoras = request.AntecedenciaMinimaHoras,
                AntecedenciaMaximaDias = request.AntecedenciaMaximaDias
            };
        }

        private static EstabelecimentoAgendamentoConfigDto Map(EstabelecimentoAgendamentoConfig entity)
        {
            return new EstabelecimentoAgendamentoConfigDto
            {
                EstabelecimentoId = entity.IdEstabelecimento,
                AgendaAtiva = entity.AgendaAtiva,
                ExigeServico = entity.ExigeServico,
                ExigeProfissional = entity.ExigeProfissional,
                PermiteEncaixe = entity.PermiteEncaixe,
                AgendaInformativo = entity.AgendaInformativo,
                DuracaoSlotMinutos = entity.DuracaoSlotMinutos,
                IntervaloEntreSlotsMinutos = entity.IntervaloEntreSlotsMinutos,
                LimitePorSlot = entity.LimitePorSlot,
                AntecedenciaMinimaHoras = entity.AntecedenciaMinimaHoras,
                AntecedenciaMaximaDias = entity.AntecedenciaMaximaDias,
                UpdatedAt = entity.UpdatedAt
            };
        }
    }
}
