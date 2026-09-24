using System;
using System.Threading.Tasks;
using APIBack.DTOs.Delivery;
using APIBack.Repository.Interface;
using Dapper;
using Npgsql;

namespace APIBack.Repository
{
    /// <summary>Le e grava so os dados do restaurante que a configuracao do delivery controla.</summary>
    public sealed class RestaurantSettingsRepository : IRestaurantSettingsRepository
    {
        private readonly NpgsqlDataSource _dataSource;

        public RestaurantSettingsRepository(NpgsqlDataSource dataSource)
        {
            _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        }

        private const string SelectSql = @"
SELECT e.id                  AS EstabelecimentoId,
       COALESCE(e.nome_fantasia, '') AS NomeFantasia,
       e.telefone            AS Telefone,
       e.logradouro          AS Logradouro,
       e.numero              AS Numero,
       e.complemento         AS Complemento,
       e.bairro              AS Bairro,
       e.cidade              AS Cidade,
       e.uf                  AS Uf,
       e.cep                 AS Cep,
       e.latitude            AS Latitude,
       e.longitude           AS Longitude,
       COALESCE(e.aceita_pedidos, TRUE) AS AceitaPedidos,
       e.raio_entrega_km     AS RaioEntregaKm,
       e.pedido_minimo       AS PedidoMinimo,
       e.taxa_entrega_fixa   AS TaxaEntregaFixa,
       e.taxa_entrega_por_km AS TaxaEntregaPorKm,
       e.tempo_preparo_min   AS TempoPreparoMin
  FROM estabelecimentos e
 WHERE e.id = @EstabelecimentoId;";

        public async Task<RestaurantSettingsDto?> GetAsync(Guid estabelecimentoId)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            return await connection.QuerySingleOrDefaultAsync<RestaurantSettingsDto>(
                SelectSql, new { EstabelecimentoId = estabelecimentoId });
        }

        public async Task<RestaurantSettingsDto?> UpdateAsync(Guid estabelecimentoId, UpdateRestaurantSettingsRequest request)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            var affected = await connection.ExecuteAsync(@"
UPDATE estabelecimentos
   SET telefone = @Telefone,
       logradouro = @Logradouro,
       numero = @Numero,
       complemento = @Complemento,
       bairro = @Bairro,
       cidade = @Cidade,
       uf = @Uf,
       cep = @Cep,
       latitude = @Latitude,
       longitude = @Longitude,
       aceita_pedidos = @AceitaPedidos,
       raio_entrega_km = @RaioEntregaKm,
       pedido_minimo = @PedidoMinimo,
       taxa_entrega_fixa = @TaxaEntregaFixa,
       taxa_entrega_por_km = @TaxaEntregaPorKm,
       tempo_preparo_min = @TempoPreparoMin,
       data_atualizacao = NOW()
 WHERE id = @EstabelecimentoId;", new
            {
                EstabelecimentoId = estabelecimentoId,
                request.Telefone,
                request.Logradouro,
                request.Numero,
                request.Complemento,
                request.Bairro,
                request.Cidade,
                request.Uf,
                request.Cep,
                request.Latitude,
                request.Longitude,
                request.AceitaPedidos,
                request.RaioEntregaKm,
                request.PedidoMinimo,
                request.TaxaEntregaFixa,
                request.TaxaEntregaPorKm,
                request.TempoPreparoMin
            });
            return affected == 0 ? null : await GetAsync(estabelecimentoId);
        }
    }
}
