using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using APIBack.DTOs.Common;
using APIBack.DTOs.Configuracoes;
using APIBack.Model;
using APIBack.Repository.Interface;
using APIBack.Service.Interface;

namespace APIBack.Service
{
    public class EstabelecimentoServicoService : IEstabelecimentoServicoService
    {
        private readonly IEstabelecimentoServicoRepository _repository;

        public EstabelecimentoServicoService(IEstabelecimentoServicoRepository repository)
        {
            _repository = repository;
        }

        public async Task<PagedResultDto<EstabelecimentoServicoDto>> ListarAsync(
            Guid idEstabelecimento,
            string? busca,
            bool? ativo,
            bool? agendavel,
            string? tipo,
            int page,
            int pageSize)
        {
            var normalizedPage = ValidationUtils.NormalizePage(page);
            var normalizedPageSize = ValidationUtils.NormalizePageSize(pageSize);
            var result = await _repository.ListarAsync(
                idEstabelecimento,
                ValidationUtils.TrimToNull(busca),
                ativo,
                agendavel,
                ValidationUtils.TrimToNull(tipo),
                normalizedPage,
                normalizedPageSize);

            return new PagedResultDto<EstabelecimentoServicoDto>
            {
                Itens = result.Itens.Select(Map).ToArray(),
                Total = result.Total
            };
        }

        public async Task<IReadOnlyCollection<EstabelecimentoServicoDto>> ListarTodosAsync(Guid idEstabelecimento)
        {
            var itens = await _repository.ListarTodosAsync(idEstabelecimento);
            return itens.Select(Map).ToArray();
        }

        public async Task<EstabelecimentoServicoDto?> ObterPorIdAsync(Guid idEstabelecimento, Guid id)
        {
            var item = await _repository.ObterPorIdAsync(idEstabelecimento, id);
            return item == null ? null : Map(item);
        }

        public async Task<EstabelecimentoServicoDto> CriarAsync(Guid idEstabelecimento, SalvarEstabelecimentoServicoRequest request)
        {
            var entity = BuildEntity(idEstabelecimento, request);
            entity.Id = await _repository.CriarAsync(entity);
            return Map(entity);
        }

        public async Task<EstabelecimentoServicoDto> AtualizarAsync(Guid idEstabelecimento, Guid id, SalvarEstabelecimentoServicoRequest request)
        {
            var current = await _repository.ObterPorIdAsync(idEstabelecimento, id)
                ?? throw new KeyNotFoundException("Registro nao encontrado.");

            var entity = BuildEntity(idEstabelecimento, request);
            entity.Id = id;
            entity.CreatedAt = current.CreatedAt;
            entity.UpdatedAt = current.UpdatedAt;

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

        private static EstabelecimentoServico BuildEntity(Guid idEstabelecimento, SalvarEstabelecimentoServicoRequest request)
        {
            var errors = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var nome = ValidationUtils.TrimToNull(request.Nome);
            var descricao = ValidationUtils.TrimToNull(request.Descricao);
            var tipo = ValidationUtils.TrimToNull(request.Tipo);
            var palavrasChave = ValidationUtils.NormalizeStringList(request.PalavrasChave);

            if (string.IsNullOrWhiteSpace(nome) || nome.Length < 2 || nome.Length > 120)
            {
                ValidationUtils.AddError(errors, "nome", "Nome deve ter entre 2 e 120 caracteres.");
            }

            if (string.IsNullOrWhiteSpace(tipo))
            {
                ValidationUtils.AddError(errors, "tipo", "Tipo e obrigatorio.");
            }

            if (request.DuracaoMinutos <= 0 || request.DuracaoMinutos > 1440)
            {
                ValidationUtils.AddError(errors, "duracaoMinutos", "Duracao deve ser maior que 0 e menor ou igual a 1440.");
            }

            if (request.ValorCentavos.HasValue && request.ValorCentavos.Value < 0)
            {
                ValidationUtils.AddError(errors, "valorCentavos", "Valor deve ser maior ou igual a zero.");
            }

            ValidationUtils.ThrowIfAny(errors);

            return new EstabelecimentoServico
            {
                IdEstabelecimento = idEstabelecimento,
                Nome = nome!,
                Descricao = descricao,
                Tipo = tipo!,
                DuracaoMinutos = request.DuracaoMinutos,
                ValorCentavos = request.ValorCentavos,
                Ativo = request.Ativo,
                ExibirNoBot = request.ExibirNoBot,
                PermiteAgendamento = request.PermiteAgendamento,
                PalavrasChave = palavrasChave
            };
        }

        private static EstabelecimentoServicoDto Map(EstabelecimentoServico entity)
        {
            return new EstabelecimentoServicoDto
            {
                Id = entity.Id,
                EstabelecimentoId = entity.IdEstabelecimento,
                Nome = entity.Nome,
                Descricao = entity.Descricao,
                Tipo = entity.Tipo,
                DuracaoMinutos = entity.DuracaoMinutos,
                ValorCentavos = entity.ValorCentavos,
                Ativo = entity.Ativo,
                ExibirNoBot = entity.ExibirNoBot,
                PermiteAgendamento = entity.PermiteAgendamento,
                PalavrasChave = entity.PalavrasChave,
                CreatedAt = entity.CreatedAt,
                UpdatedAt = entity.UpdatedAt,
                DeletedAt = entity.DeletedAt
            };
        }
    }
}
