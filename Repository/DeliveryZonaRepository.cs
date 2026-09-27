using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using APIBack.DTOs.Delivery;
using APIBack.Repository.Interface;
using Dapper;
using Npgsql;

namespace APIBack.Repository
{
    public sealed class DeliveryZonaRepository : IDeliveryZonaRepository
    {
        private const string Columns = "id AS Id, nome AS Nome, raio_ate_km AS RaioAteKm, taxa AS Taxa, cor AS Cor, ordem AS Ordem, ativo AS Ativo";

        private readonly NpgsqlDataSource _dataSource;

        public DeliveryZonaRepository(NpgsqlDataSource dataSource)
        {
            _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        }

        public async Task<IReadOnlyList<DeliveryZonaDto>> ListAsync(Guid estabelecimentoId)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            var rows = await connection.QueryAsync<DeliveryZonaDto>(
                $"SELECT {Columns} FROM delivery_zona WHERE estabelecimento_id = @Id ORDER BY ordem, raio_ate_km;",
                new { Id = estabelecimentoId });
            return rows.ToList();
        }

        public async Task<IReadOnlyList<DeliveryZonaDto>> ListAtivasOrdenadasAsync(Guid estabelecimentoId)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            var rows = await connection.QueryAsync<DeliveryZonaDto>(
                $"SELECT {Columns} FROM delivery_zona WHERE estabelecimento_id = @Id AND ativo = TRUE ORDER BY raio_ate_km;",
                new { Id = estabelecimentoId });
            return rows.ToList();
        }

        public async Task<DeliveryZonaDto> CreateAsync(Guid estabelecimentoId, SalvarZonaRequest request)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            var id = Guid.NewGuid();
            return await connection.QuerySingleAsync<DeliveryZonaDto>($@"
INSERT INTO delivery_zona (id, estabelecimento_id, nome, raio_ate_km, taxa, cor, ordem, ativo)
VALUES (@Id, @EstabelecimentoId, @Nome, @RaioAteKm, @Taxa, @Cor, @Ordem, @Ativo)
RETURNING {Columns};", new
            {
                Id = id,
                EstabelecimentoId = estabelecimentoId,
                request.Nome,
                request.RaioAteKm,
                request.Taxa,
                request.Cor,
                request.Ordem,
                request.Ativo
            });
        }

        public async Task<DeliveryZonaDto?> UpdateAsync(Guid estabelecimentoId, Guid zonaId, SalvarZonaRequest request)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            return await connection.QuerySingleOrDefaultAsync<DeliveryZonaDto>($@"
UPDATE delivery_zona
   SET nome = @Nome, raio_ate_km = @RaioAteKm, taxa = @Taxa, cor = @Cor, ordem = @Ordem, ativo = @Ativo,
       atualizado_em = NOW()
 WHERE id = @ZonaId AND estabelecimento_id = @EstabelecimentoId
RETURNING {Columns};", new
            {
                ZonaId = zonaId,
                EstabelecimentoId = estabelecimentoId,
                request.Nome,
                request.RaioAteKm,
                request.Taxa,
                request.Cor,
                request.Ordem,
                request.Ativo
            });
        }

        public async Task<bool> DeleteAsync(Guid estabelecimentoId, Guid zonaId)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            var affected = await connection.ExecuteAsync(
                "DELETE FROM delivery_zona WHERE id = @ZonaId AND estabelecimento_id = @EstabelecimentoId;",
                new { ZonaId = zonaId, EstabelecimentoId = estabelecimentoId });
            return affected > 0;
        }
    }
}
