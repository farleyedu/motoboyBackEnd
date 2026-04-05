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
    public class EstabelecimentoFaqService : IEstabelecimentoFaqService
    {
        private readonly IEstabelecimentoFaqRepository _repository;

        public EstabelecimentoFaqService(IEstabelecimentoFaqRepository repository)
        {
            _repository = repository;
        }

        public async Task<PagedResultDto<EstabelecimentoFaqDto>> ListarAsync(
            Guid idEstabelecimento,
            string? busca,
            bool? ativo,
            string? categoria,
            int page,
            int pageSize)
        {
            var normalizedPage = ValidationUtils.NormalizePage(page);
            var normalizedPageSize = ValidationUtils.NormalizePageSize(pageSize);
            var result = await _repository.ListarAsync(
                idEstabelecimento,
                ValidationUtils.TrimToNull(busca),
                ativo,
                ValidationUtils.TrimToNull(categoria),
                normalizedPage,
                normalizedPageSize);

            return new PagedResultDto<EstabelecimentoFaqDto>
            {
                Itens = result.Itens.Select(Map).ToArray(),
                Total = result.Total
            };
        }

        public async Task<IReadOnlyCollection<EstabelecimentoFaqDto>> ListarTodosAsync(Guid idEstabelecimento)
        {
            var itens = await _repository.ListarTodosAsync(idEstabelecimento);
            return itens.Select(Map).ToArray();
        }

        public async Task<EstabelecimentoFaqDto?> ObterPorIdAsync(Guid idEstabelecimento, Guid id)
        {
            var item = await _repository.ObterPorIdAsync(idEstabelecimento, id);
            return item == null ? null : Map(item);
        }

        public async Task<EstabelecimentoFaqDto> CriarAsync(Guid idEstabelecimento, SalvarEstabelecimentoFaqRequest request)
        {
            var entity = BuildEntity(idEstabelecimento, request);
            entity.Id = await _repository.CriarAsync(entity);
            return Map(entity);
        }

        public async Task<EstabelecimentoFaqDto> AtualizarAsync(Guid idEstabelecimento, Guid id, SalvarEstabelecimentoFaqRequest request)
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

        private static EstabelecimentoFaq BuildEntity(Guid idEstabelecimento, SalvarEstabelecimentoFaqRequest request)
        {
            var errors = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var pergunta = ValidationUtils.TrimToNull(request.Pergunta);
            var resposta = ValidationUtils.TrimToNull(request.Resposta);
            var categoria = ValidationUtils.TrimToNull(request.Categoria);
            var palavrasChave = ValidationUtils.NormalizeStringList(request.PalavrasChave);

            if (string.IsNullOrWhiteSpace(pergunta) || pergunta.Length < 4 || pergunta.Length > 200)
            {
                ValidationUtils.AddError(errors, "pergunta", "Pergunta deve ter entre 4 e 200 caracteres.");
            }

            if (string.IsNullOrWhiteSpace(resposta) || resposta.Length < 4 || resposta.Length > 5000)
            {
                ValidationUtils.AddError(errors, "resposta", "Resposta deve ter entre 4 e 5000 caracteres.");
            }

            if (string.IsNullOrWhiteSpace(categoria))
            {
                ValidationUtils.AddError(errors, "categoria", "Categoria e obrigatoria.");
            }

            if (request.Ordem < 1)
            {
                ValidationUtils.AddError(errors, "ordem", "Ordem deve ser maior ou igual a 1.");
            }

            ValidationUtils.ThrowIfAny(errors);

            return new EstabelecimentoFaq
            {
                IdEstabelecimento = idEstabelecimento,
                Pergunta = pergunta!,
                Resposta = resposta!,
                Categoria = categoria!,
                Ordem = request.Ordem,
                Ativo = request.Ativo,
                PalavrasChave = palavrasChave
            };
        }

        private static EstabelecimentoFaqDto Map(EstabelecimentoFaq entity)
        {
            return new EstabelecimentoFaqDto
            {
                Id = entity.Id,
                EstabelecimentoId = entity.IdEstabelecimento,
                Pergunta = entity.Pergunta,
                Resposta = entity.Resposta,
                Categoria = entity.Categoria,
                Ordem = entity.Ordem,
                Ativo = entity.Ativo,
                PalavrasChave = entity.PalavrasChave,
                CreatedAt = entity.CreatedAt,
                UpdatedAt = entity.UpdatedAt,
                DeletedAt = entity.DeletedAt
            };
        }
    }
}
