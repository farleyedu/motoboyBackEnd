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
        private readonly IConfiguracaoCarroRepository _carroRepository;

        public EstabelecimentoServicoService(
            IEstabelecimentoServicoRepository repository,
            IConfiguracaoCarroRepository carroRepository)
        {
            _repository = repository;
            _carroRepository = carroRepository;
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
            var entity = await BuildEntityAsync(idEstabelecimento, request);
            entity.Id = await _repository.CriarAsync(entity);
            return Map(entity);
        }

        public async Task<EstabelecimentoServicoDto> AtualizarAsync(Guid idEstabelecimento, Guid id, SalvarEstabelecimentoServicoRequest request)
        {
            var current = await _repository.ObterPorIdAsync(idEstabelecimento, id)
                ?? throw new KeyNotFoundException("Registro nao encontrado.");

            var entity = await BuildEntityAsync(idEstabelecimento, request);
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

        private async Task<EstabelecimentoServico> BuildEntityAsync(Guid idEstabelecimento, SalvarEstabelecimentoServicoRequest request)
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

            var veiculoConfigs = request.DiferePorVeiculo
                ? await BuildVeiculoConfigsAsync(idEstabelecimento, request.VeiculoConfigs, errors)
                : new List<EstabelecimentoServicoVeiculoConfig>();

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
                PalavrasChave = palavrasChave,
                DiferePorVeiculo = request.DiferePorVeiculo,
                VeiculoConfigs = veiculoConfigs
            };
        }

        private async Task<List<EstabelecimentoServicoVeiculoConfig>> BuildVeiculoConfigsAsync(
            Guid idEstabelecimento,
            IReadOnlyCollection<SalvarServicoVeiculoConfigRequest>? requestConfigs,
            Dictionary<string, List<string>> errors)
        {
            var carrosDisponiveis = await _carroRepository.ListarPorEstabelecimentoAsync(idEstabelecimento);
            var carrosPorId = carrosDisponiveis.ToDictionary(item => item.Id, item => item);
            var configuracoes = new List<EstabelecimentoServicoVeiculoConfig>();
            var idsUtilizados = new HashSet<Guid>();

            foreach (var raw in requestConfigs ?? Array.Empty<SalvarServicoVeiculoConfigRequest>())
            {
                if (!TryParseGuid(raw.CarroId, out var carroId))
                {
                    ValidationUtils.AddError(errors, "veiculoConfigs", "Carro invalido.");
                    continue;
                }

                if (!carrosPorId.ContainsKey(carroId))
                {
                    ValidationUtils.AddError(errors, "veiculoConfigs", $"Carro {carroId} nao pertence ao estabelecimento atual.");
                    continue;
                }

                if (!idsUtilizados.Add(carroId))
                {
                    ValidationUtils.AddError(errors, "veiculoConfigs", $"Carro {carroId} informado mais de uma vez.");
                    continue;
                }

                if (raw.ValorCentavos.HasValue && raw.ValorCentavos.Value < 0)
                {
                    ValidationUtils.AddError(errors, "veiculoConfigs", $"Valor invalido para o carro {carroId}.");
                    continue;
                }

                var marcasPeca = raw.Compativel
                    ? BuildMarcasPeca(raw.MarcasPeca, errors, carroId)
                    : new List<EstabelecimentoServicoMarcaPeca>();

                configuracoes.Add(new EstabelecimentoServicoVeiculoConfig
                {
                    CarroId = carroId,
                    Compativel = raw.Compativel,
                    ValorCentavos = raw.Compativel ? raw.ValorCentavos : null,
                    MarcasPeca = marcasPeca
                });
            }

            return configuracoes;
        }

        private static List<EstabelecimentoServicoMarcaPeca> BuildMarcasPeca(
            IReadOnlyCollection<SalvarMarcaPecaRequest>? requestItems,
            Dictionary<string, List<string>> errors,
            Guid carroId)
        {
            var result = new List<EstabelecimentoServicoMarcaPeca>();
            var nomes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var rawItem in requestItems ?? Array.Empty<SalvarMarcaPecaRequest>())
            {
                var nome = ValidationUtils.TrimToNull(rawItem.Nome);
                if (string.IsNullOrWhiteSpace(nome) || nome.Length > 120)
                {
                    ValidationUtils.AddError(errors, "veiculoConfigs", $"Cada marca de peca do carro {carroId} deve ter nome entre 1 e 120 caracteres.");
                    continue;
                }

                var nomeNormalizado = ValidationUtils.NormalizeToken(nome);
                if (!nomes.Add(nomeNormalizado))
                {
                    ValidationUtils.AddError(errors, "veiculoConfigs", $"Marca de peca duplicada no carro {carroId}: {nome}.");
                    continue;
                }

                if (rawItem.ValorCentavos.HasValue && rawItem.ValorCentavos.Value < 0)
                {
                    ValidationUtils.AddError(errors, "veiculoConfigs", $"Valor invalido para a marca de peca {nome} no carro {carroId}.");
                    continue;
                }

                result.Add(new EstabelecimentoServicoMarcaPeca
                {
                    Id = ParseGuidOrNew(rawItem.Id),
                    Nome = nome,
                    ValorCentavos = rawItem.ValorCentavos
                });
            }

            return result;
        }

        private static EstabelecimentoServicoDto Map(EstabelecimentoServico entity)
        {
            var overallRange = ComputeRange(BuildOverallPriceCandidates(entity));

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
                DiferePorVeiculo = entity.DiferePorVeiculo,
                VeiculoConfigs = entity.DiferePorVeiculo
                    ? entity.VeiculoConfigs.Select(config => MapVeiculoConfig(entity, config)).ToList()
                    : new List<ServicoVeiculoConfigDto>(),
                ValorMinimoCentavos = overallRange.Min,
                ValorMaximoCentavos = overallRange.Max,
                CreatedAt = entity.CreatedAt,
                UpdatedAt = entity.UpdatedAt,
                DeletedAt = entity.DeletedAt
            };
        }

        private static ServicoVeiculoConfigDto MapVeiculoConfig(
            EstabelecimentoServico entity,
            EstabelecimentoServicoVeiculoConfig config)
        {
            if (!config.Compativel)
            {
                return new ServicoVeiculoConfigDto
                {
                    CarroId = config.CarroId.ToString(),
                    Compativel = false,
                    ValorCentavos = null,
                    MarcasPeca = new List<MarcaPecaDto>()
                };
            }

            var candidates = new List<long>();
            var resolvedVehiclePrice = ResolveVehiclePrice(entity, config);
            if (resolvedVehiclePrice.HasValue)
            {
                candidates.Add(resolvedVehiclePrice.Value);
            }

            candidates.AddRange(
                config.MarcasPeca
                    .Select(piece => ResolvePiecePrice(entity, config, piece))
                    .Where(value => value.HasValue)
                    .Select(value => value!.Value));

            var range = ComputeRange(candidates);

            return new ServicoVeiculoConfigDto
            {
                CarroId = config.CarroId.ToString(),
                Compativel = true,
                ValorCentavos = config.ValorCentavos,
                MarcasPeca = config.MarcasPeca.Select(piece => MapMarcaPeca(entity, config, piece)).ToList(),
                ValorMinimoCentavos = range.Min,
                ValorMaximoCentavos = range.Max
            };
        }

        private static MarcaPecaDto MapMarcaPeca(
            EstabelecimentoServico entity,
            EstabelecimentoServicoVeiculoConfig config,
            EstabelecimentoServicoMarcaPeca piece)
        {
            var resolvedPrice = ResolvePiecePrice(entity, config, piece);

            return new MarcaPecaDto
            {
                Id = piece.Id.ToString(),
                Nome = piece.Nome,
                ValorCentavos = piece.ValorCentavos,
                ValorMinimoCentavos = resolvedPrice,
                ValorMaximoCentavos = resolvedPrice
            };
        }

        private static IReadOnlyCollection<long> BuildOverallPriceCandidates(EstabelecimentoServico entity)
        {
            if (!entity.DiferePorVeiculo)
            {
                return entity.ValorCentavos.HasValue
                    ? new[] { entity.ValorCentavos.Value }
                    : Array.Empty<long>();
            }

            var values = new List<long>();

            foreach (var config in entity.VeiculoConfigs.Where(item => item.Compativel))
            {
                var resolvedVehiclePrice = ResolveVehiclePrice(entity, config);
                if (resolvedVehiclePrice.HasValue)
                {
                    values.Add(resolvedVehiclePrice.Value);
                }

                foreach (var piece in config.MarcasPeca)
                {
                    var resolvedPiecePrice = ResolvePiecePrice(entity, config, piece);
                    if (resolvedPiecePrice.HasValue)
                    {
                        values.Add(resolvedPiecePrice.Value);
                    }
                }
            }

            return DistinctAndOrder(values);
        }

        private static long? ResolveVehiclePrice(EstabelecimentoServico entity, EstabelecimentoServicoVeiculoConfig config)
        {
            if (!config.Compativel)
            {
                return null;
            }

            return config.ValorCentavos ?? entity.ValorCentavos;
        }

        private static long? ResolvePiecePrice(
            EstabelecimentoServico entity,
            EstabelecimentoServicoVeiculoConfig config,
            EstabelecimentoServicoMarcaPeca piece)
            => piece.ValorCentavos ?? config.ValorCentavos ?? entity.ValorCentavos;

        private static (long? Min, long? Max) ComputeRange(IEnumerable<long> values)
        {
            var ordered = values
                .Distinct()
                .OrderBy(value => value)
                .ToArray();

            if (ordered.Length == 0)
            {
                return (null, null);
            }

            return (ordered[0], ordered[^1]);
        }

        private static IReadOnlyCollection<long> DistinctAndOrder(IEnumerable<long> values)
        {
            return values
                .Distinct()
                .OrderBy(value => value)
                .ToArray();
        }

        private static Guid ParseGuidOrNew(string? value)
            => TryParseGuid(value, out var parsed) ? parsed : Guid.NewGuid();

        private static bool TryParseGuid(string? value, out Guid parsed)
            => Guid.TryParse(ValidationUtils.TrimToNull(value), out parsed);
    }
}
