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

            var marcasPeca = request.DiferePorMarcaPeca
                ? BuildMarcaPecaNodes(request.MarcasPeca, errors)
                : new List<EstabelecimentoServicoMarcaPecaNode>();

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
                VeiculoConfigs = veiculoConfigs,
                DiferePorMarcaPeca = request.DiferePorMarcaPeca,
                MarcasPeca = marcasPeca
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

                configuracoes.Add(new EstabelecimentoServicoVeiculoConfig
                {
                    CarroId = carroId,
                    Compativel = raw.Compativel,
                    ValorCentavos = raw.Compativel ? raw.ValorCentavos : null
                });
            }

            return configuracoes;
        }

        private static List<EstabelecimentoServicoMarcaPecaNode> BuildMarcaPecaNodes(
            IReadOnlyCollection<SalvarMarcaPecaNodeRequest>? requestNodes,
            Dictionary<string, List<string>> errors)
        {
            var result = new List<EstabelecimentoServicoMarcaPecaNode>();
            var nomesRaiz = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var rawNode in requestNodes ?? Array.Empty<SalvarMarcaPecaNodeRequest>())
            {
                var nome = ValidationUtils.TrimToNull(rawNode.Nome);
                if (string.IsNullOrWhiteSpace(nome) || nome.Length > 120)
                {
                    ValidationUtils.AddError(errors, "marcasPeca", "Cada marca de peca deve ter nome entre 1 e 120 caracteres.");
                    continue;
                }

                var nomeNormalizado = ValidationUtils.NormalizeToken(nome);
                if (!nomesRaiz.Add(nomeNormalizado))
                {
                    ValidationUtils.AddError(errors, "marcasPeca", $"Marca de peca duplicada: {nome}.");
                    continue;
                }

                if (rawNode.ValorCentavos.HasValue && rawNode.ValorCentavos.Value < 0)
                {
                    ValidationUtils.AddError(errors, "marcasPeca", $"Valor invalido para a marca de peca {nome}.");
                    continue;
                }

                var variantes = new List<EstabelecimentoServicoMarcaPecaVariante>();
                var nomesVariantes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var rawVariante in rawNode.Variantes ?? new List<SalvarMarcaPecaVarianteRequest>())
                {
                    var nomeVariante = ValidationUtils.TrimToNull(rawVariante.Nome);
                    if (string.IsNullOrWhiteSpace(nomeVariante) || nomeVariante.Length > 120)
                    {
                        ValidationUtils.AddError(errors, "marcasPeca", $"Cada variante da marca {nome} deve ter nome entre 1 e 120 caracteres.");
                        continue;
                    }

                    var varianteNormalizada = ValidationUtils.NormalizeToken(nomeVariante);
                    if (!nomesVariantes.Add(varianteNormalizada))
                    {
                        ValidationUtils.AddError(errors, "marcasPeca", $"Variante duplicada na marca {nome}: {nomeVariante}.");
                        continue;
                    }

                    if (rawVariante.ValorCentavos.HasValue && rawVariante.ValorCentavos.Value < 0)
                    {
                        ValidationUtils.AddError(errors, "marcasPeca", $"Valor invalido para a variante {nomeVariante}.");
                        continue;
                    }

                    variantes.Add(new EstabelecimentoServicoMarcaPecaVariante
                    {
                        Id = ParseGuidOrNew(rawVariante.Id),
                        Nome = nomeVariante,
                        ValorCentavos = rawVariante.ValorCentavos
                    });
                }

                result.Add(new EstabelecimentoServicoMarcaPecaNode
                {
                    Id = ParseGuidOrNew(rawNode.Id),
                    Nome = nome,
                    ValorCentavos = rawNode.ValorCentavos,
                    Variantes = variantes
                });
            }

            return result;
        }

        private static EstabelecimentoServicoDto Map(EstabelecimentoServico entity)
        {
            var overallRange = ComputeRange(BuildOverallPriceCandidates(entity));
            var piecePrices = BuildPieceEffectivePrices(entity);
            var vehiclePrices = BuildVehicleEffectivePrices(entity);

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
                    ? entity.VeiculoConfigs.Select(config => MapVeiculoConfig(entity, config, piecePrices)).ToList()
                    : new List<ServicoVeiculoConfigDto>(),
                DiferePorMarcaPeca = entity.DiferePorMarcaPeca,
                MarcasPeca = entity.DiferePorMarcaPeca
                    ? entity.MarcasPeca.Select(node => MapMarcaPecaNode(entity, node, vehiclePrices)).ToList()
                    : new List<MarcaPecaNodeDto>(),
                ValorMinimoCentavos = overallRange.Min,
                ValorMaximoCentavos = overallRange.Max,
                CreatedAt = entity.CreatedAt,
                UpdatedAt = entity.UpdatedAt,
                DeletedAt = entity.DeletedAt
            };
        }

        private static ServicoVeiculoConfigDto MapVeiculoConfig(
            EstabelecimentoServico entity,
            EstabelecimentoServicoVeiculoConfig config,
            IReadOnlyCollection<long> piecePrices)
        {
            if (!config.Compativel)
            {
                return new ServicoVeiculoConfigDto
                {
                    CarroId = config.CarroId.ToString(),
                    Compativel = false,
                    ValorCentavos = null
                };
            }

            var candidates = new List<long>(piecePrices);
            var resolvedVehiclePrice = ResolveVehiclePrice(entity, config);
            if (resolvedVehiclePrice.HasValue)
            {
                candidates.Add(resolvedVehiclePrice.Value);
            }

            var range = ComputeRange(candidates);

            return new ServicoVeiculoConfigDto
            {
                CarroId = config.CarroId.ToString(),
                Compativel = true,
                ValorCentavos = config.ValorCentavos,
                ValorMinimoCentavos = range.Min,
                ValorMaximoCentavos = range.Max
            };
        }

        private static MarcaPecaNodeDto MapMarcaPecaNode(
            EstabelecimentoServico entity,
            EstabelecimentoServicoMarcaPecaNode node,
            IReadOnlyCollection<long> vehiclePrices)
        {
            var candidates = new List<long>(vehiclePrices);
            var resolvedNodePrice = ResolveNodePrice(entity, node);
            if (resolvedNodePrice.HasValue)
            {
                candidates.Add(resolvedNodePrice.Value);
            }

            var range = ComputeRange(candidates);

            return new MarcaPecaNodeDto
            {
                Id = node.Id.ToString(),
                Nome = node.Nome,
                ValorCentavos = node.ValorCentavos,
                Variantes = node.Variantes.Select(variante => MapMarcaPecaVariante(entity, node, variante, vehiclePrices)).ToList(),
                ValorMinimoCentavos = range.Min,
                ValorMaximoCentavos = range.Max
            };
        }

        private static MarcaPecaVarianteDto MapMarcaPecaVariante(
            EstabelecimentoServico entity,
            EstabelecimentoServicoMarcaPecaNode node,
            EstabelecimentoServicoMarcaPecaVariante variante,
            IReadOnlyCollection<long> vehiclePrices)
        {
            var candidates = new List<long>(vehiclePrices);
            var resolvedVariantPrice = ResolveVariantPrice(entity, node, variante);
            if (resolvedVariantPrice.HasValue)
            {
                candidates.Add(resolvedVariantPrice.Value);
            }

            var range = ComputeRange(candidates);

            return new MarcaPecaVarianteDto
            {
                Id = variante.Id.ToString(),
                Nome = variante.Nome,
                ValorCentavos = variante.ValorCentavos,
                ValorMinimoCentavos = range.Min,
                ValorMaximoCentavos = range.Max
            };
        }

        private static IReadOnlyCollection<long> BuildOverallPriceCandidates(EstabelecimentoServico entity)
        {
            var values = new List<long>();

            if (entity.ValorCentavos.HasValue)
            {
                values.Add(entity.ValorCentavos.Value);
            }

            values.AddRange(BuildVehicleEffectivePrices(entity));
            values.AddRange(BuildPieceEffectivePrices(entity));

            return DistinctAndOrder(values);
        }

        private static IReadOnlyCollection<long> BuildVehicleEffectivePrices(EstabelecimentoServico entity)
        {
            var values = entity.VeiculoConfigs
                .Select(config => ResolveVehiclePrice(entity, config))
                .Where(value => value.HasValue)
                .Select(value => value!.Value);

            return DistinctAndOrder(values);
        }

        private static IReadOnlyCollection<long> BuildPieceEffectivePrices(EstabelecimentoServico entity)
        {
            var values = new List<long>();

            foreach (var node in entity.MarcasPeca)
            {
                var nodePrice = ResolveNodePrice(entity, node);
                if (nodePrice.HasValue)
                {
                    values.Add(nodePrice.Value);
                }

                foreach (var variante in node.Variantes)
                {
                    var variantPrice = ResolveVariantPrice(entity, node, variante);
                    if (variantPrice.HasValue)
                    {
                        values.Add(variantPrice.Value);
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

        private static long? ResolveNodePrice(EstabelecimentoServico entity, EstabelecimentoServicoMarcaPecaNode node)
            => node.ValorCentavos ?? entity.ValorCentavos;

        private static long? ResolveVariantPrice(
            EstabelecimentoServico entity,
            EstabelecimentoServicoMarcaPecaNode node,
            EstabelecimentoServicoMarcaPecaVariante variante)
            => variante.ValorCentavos ?? node.ValorCentavos ?? entity.ValorCentavos;

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
