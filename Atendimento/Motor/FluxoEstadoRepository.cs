using System;
using System.Text.Json;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace APIBack.Atendimento.Motor
{
    public interface IFluxoEstadoRepository
    {
        /// <summary>Estado salvo da conversa; nulo quando nao ha ou quando o formato e de uma versao que este codigo nao entende.</summary>
        Task<EstadoFluxo?> ObterAsync(Guid conversaId);
        Task SalvarAsync(Guid conversaId, EstadoFluxo estado, string fluxo);
    }

    public sealed class SqlFluxoEstadoRepository : IFluxoEstadoRepository
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private readonly string _connectionString;
        private readonly ILogger<SqlFluxoEstadoRepository> _logger;

        public SqlFluxoEstadoRepository(IConfiguration configuration, ILogger<SqlFluxoEstadoRepository> logger)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection")
                ?? configuration["ConnectionStrings:DefaultConnection"]
                ?? throw new InvalidOperationException("Connection string 'DefaultConnection' nao encontrada.");
            _logger = logger;
        }

        public async Task<EstadoFluxo?> ObterAsync(Guid conversaId)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            var linha = await connection.QueryFirstOrDefaultAsync<(string? Estado, int Versao)>(
                "SELECT fluxo_estado::text AS Estado, fluxo_versao AS Versao FROM conversas WHERE id = @Id;", new { Id = conversaId });

            if (string.IsNullOrWhiteSpace(linha.Estado)) return null;

            // Estado de uma versao futura ou ilegivel: recomeca em vez de errar. A conversa nao fica presa.
            if (linha.Versao != EstadoFluxo.VersaoAtual)
            {
                _logger.LogWarning("[atend] ev=estado_descartado conversa={Conversa} versao={Versao} esperada={Esperada}", conversaId, linha.Versao, EstadoFluxo.VersaoAtual);
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize<EstadoFluxo>(linha.Estado, Json);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "[atend] ev=estado_ilegivel conversa={Conversa}", conversaId);
                return null;
            }
        }

        public async Task SalvarAsync(Guid conversaId, EstadoFluxo estado, string fluxo)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.ExecuteAsync(@"
UPDATE conversas
   SET fluxo_estado = CAST(@Estado AS jsonb), fluxo_chave = @Fluxo, fluxo_versao = @Versao
 WHERE id = @Id;", new { Id = conversaId, Estado = JsonSerializer.Serialize(estado, Json), Fluxo = fluxo, Versao = EstadoFluxo.VersaoAtual });
        }
    }
}
