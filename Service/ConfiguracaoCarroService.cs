using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using APIBack.DTOs.Configuracoes;
using APIBack.Repository.Interface;
using APIBack.Service.Interface;

namespace APIBack.Service
{
    public class ConfiguracaoCarroService : IConfiguracaoCarroService
    {
        private readonly IConfiguracaoCarroRepository _repository;

        public ConfiguracaoCarroService(IConfiguracaoCarroRepository repository)
        {
            _repository = repository;
        }

        public async Task<IReadOnlyCollection<CarroEstabelecimentoDto>> ListarPorEstabelecimentoAsync(Guid idEstabelecimento)
        {
            var itens = await _repository.ListarPorEstabelecimentoAsync(idEstabelecimento);
            return itens.Select(item => new CarroEstabelecimentoDto
            {
                Id = item.Id.ToString(),
                Marca = item.Marca,
                Modelo = item.Modelo,
                Ativo = item.Ativo
            }).ToArray();
        }
    }
}
