using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using APIBack.Automation.Models;
using APIBack.Model;
using APIBack.Repository.Interface;
using APIBack.Service.Interface;
using Microsoft.Extensions.Caching.Memory;

namespace APIBack.Automation.Services
{
    public class ServicoCatalogProvider
    {
        private static readonly MemoryCacheEntryOptions CacheOptions = new()
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60),
            Priority = CacheItemPriority.Normal
        };

        private readonly IEstabelecimentoServicoService _servicoService;
        private readonly IConfiguracaoCarroRepository _carroRepository;
        private readonly IMemoryCache _cache;

        public ServicoCatalogProvider(
            IEstabelecimentoServicoService servicoService,
            IConfiguracaoCarroRepository carroRepository,
            IMemoryCache cache)
        {
            _servicoService = servicoService;
            _carroRepository = carroRepository;
            _cache = cache;
        }

        public async Task<IReadOnlyList<ServicoCatalogItem>> ObterCatalogoVisivelAsync(Guid idEstabelecimento)
        {
            var cacheKey = $"servicos:catalogo:{idEstabelecimento}";
            if (_cache.TryGetValue(cacheKey, out IReadOnlyList<ServicoCatalogItem>? cached) && cached != null)
            {
                return cached;
            }

            var servicos = await _servicoService.ListarTodosAsync(idEstabelecimento);
            var carros = await _carroRepository.ListarPorEstabelecimentoAsync(idEstabelecimento);
            var carrosPorId = carros.ToDictionary(item => item.Id, item => item);

            var catalogo = servicos
                .Where(item => item.Ativo && item.ExibirNoBot)
                .Select(item => Map(item, carrosPorId))
                .OrderBy(item => item.Nome)
                .ToArray();

            _cache.Set(cacheKey, catalogo, CacheOptions);
            return catalogo;
        }

        public async Task<string?> BuildCompactPromptAsync(Guid idEstabelecimento)
        {
            var catalogo = await ObterCatalogoVisivelAsync(idEstabelecimento);
            if (catalogo.Count == 0)
            {
                return null;
            }

            var builder = new StringBuilder();
            builder.AppendLine("Catalogo de servicos visiveis no bot:");

            foreach (var servico in catalogo.Take(40))
            {
                var keywords = servico.PalavrasChave
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Take(4)
                    .ToArray();

                builder.Append("- ");
                builder.Append(servico.Nome);
                builder.Append(" | tipo=");
                builder.Append(string.IsNullOrWhiteSpace(servico.Tipo) ? "geral" : servico.Tipo);
                builder.Append(" | varia_por_veiculo=");
                builder.Append(servico.DiferePorVeiculo ? "sim" : "nao");
                builder.Append(" | agendavel=");
                builder.Append(servico.PermiteAgendamento ? "sim" : "nao");

                if (keywords.Length > 0)
                {
                    builder.Append(" | palavras-chave=");
                    builder.Append(string.Join(", ", keywords));
                }

                builder.AppendLine();
            }

            return builder.ToString().TrimEnd();
        }

        private static ServicoCatalogItem Map(
            DTOs.Configuracoes.EstabelecimentoServicoDto item,
            IReadOnlyDictionary<Guid, EstabelecimentoCarro> carrosPorId)
        {
            var veiculos = item.VeiculoConfigs
                .Select(config =>
                {
                    Guid.TryParse(config.CarroId, out var carroId);
                    carrosPorId.TryGetValue(carroId, out var carro);

                    return new ServicoCatalogVehicleItem
                    {
                        CarroId = carroId,
                        NomeExibicao = carro == null
                            ? config.CarroId
                            : $"{carro.Marca} {carro.Modelo}".Trim(),
                        Compativel = config.Compativel,
                        ValorCentavos = config.ValorCentavos,
                        ValorMinimoCentavos = config.ValorMinimoCentavos,
                        ValorMaximoCentavos = config.ValorMaximoCentavos,
                        MarcasPeca = config.MarcasPeca
                            .Select(piece => new ServicoCatalogPieceItem
                            {
                                Id = piece.Id,
                                Nome = piece.Nome,
                                ValorCentavos = piece.ValorCentavos,
                                ValorMinimoCentavos = piece.ValorMinimoCentavos,
                                ValorMaximoCentavos = piece.ValorMaximoCentavos
                            })
                            .ToArray()
                    };
                })
                .ToArray();

            return new ServicoCatalogItem
            {
                Id = item.Id,
                Nome = item.Nome,
                Descricao = item.Descricao,
                Tipo = item.Tipo,
                DuracaoMinutos = item.DuracaoMinutos,
                ValorCentavos = item.ValorCentavos,
                ValorMinimoCentavos = item.ValorMinimoCentavos,
                ValorMaximoCentavos = item.ValorMaximoCentavos,
                PermiteAgendamento = item.PermiteAgendamento,
                DiferePorVeiculo = item.DiferePorVeiculo,
                PalavrasChave = item.PalavrasChave,
                Veiculos = veiculos
            };
        }
    }
}
