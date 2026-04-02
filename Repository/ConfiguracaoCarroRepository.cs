using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using APIBack.Model;
using APIBack.Repository.Interface;
using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace APIBack.Repository
{
    public class ConfiguracaoCarroRepository : IConfiguracaoCarroRepository
    {
        private readonly string _connectionString;

        public ConfiguracaoCarroRepository(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection")
                ?? throw new InvalidOperationException("Connection string 'DefaultConnection' nao encontrada.");
        }

        public async Task<IReadOnlyCollection<EstabelecimentoCarro>> ListarPorEstabelecimentoAsync(Guid idEstabelecimento)
        {
            const string sql = @"
WITH marcas_ativas AS (
    SELECT em.marca_id
      FROM estabelecimento_veiculo_marcas em
     WHERE em.id_estabelecimento = @IdEstabelecimento
       AND em.ativo = TRUE
),
modelos_configurados AS (
    SELECT em.modelo_id,
           vm.marca_id,
           em.ativo
      FROM estabelecimento_veiculo_modelos em
      JOIN veiculo_modelos vm ON vm.id = em.modelo_id
     WHERE em.id_estabelecimento = @IdEstabelecimento
),
marcas_com_restricao AS (
    SELECT DISTINCT marca_id
      FROM modelos_configurados
)
SELECT vm.id AS Id,
       vm.marca_id AS MarcaId,
       m.nome AS Marca,
       vm.nome AS Modelo,
       CASE
           WHEN mcr.marca_id IS NULL
               THEN (m.ativo = TRUE AND vm.ativo = TRUE)
           ELSE (m.ativo = TRUE AND vm.ativo = TRUE AND COALESCE(mc.ativo, FALSE))
       END AS Ativo
  FROM veiculo_modelos vm
  JOIN veiculo_marcas m
    ON m.id = vm.marca_id
  JOIN marcas_ativas ma
    ON ma.marca_id = vm.marca_id
  LEFT JOIN marcas_com_restricao mcr
    ON mcr.marca_id = vm.marca_id
  LEFT JOIN modelos_configurados mc
    ON mc.modelo_id = vm.id
 WHERE mcr.marca_id IS NULL
    OR mc.modelo_id IS NOT NULL
 ORDER BY m.nome ASC, vm.nome ASC;";

            await using var connection = new NpgsqlConnection(_connectionString);
            var rows = await connection.QueryAsync<Row>(sql, new
            {
                IdEstabelecimento = idEstabelecimento
            });

            return rows.Select(Map).ToArray();
        }

        private static EstabelecimentoCarro Map(Row row)
        {
            return new EstabelecimentoCarro
            {
                Id = row.Id,
                MarcaId = row.MarcaId,
                Marca = row.Marca ?? string.Empty,
                Modelo = row.Modelo ?? string.Empty,
                Ativo = row.Ativo
            };
        }

        private sealed class Row
        {
            public Guid Id { get; set; }
            public Guid MarcaId { get; set; }
            public string? Marca { get; set; }
            public string? Modelo { get; set; }
            public bool Ativo { get; set; }
        }
    }
}
