using System;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace APIBack.Service
{
    /// <summary>
    /// Diz se uma conversa e de um cliente de TESTE (clientes.simulado). O envio real de WhatsApp
    /// consulta isto antes de sair: o simulador de cliente usa numeros que existem de verdade, e a
    /// resposta do atendente ou da IA jamais pode chegar ao aparelho de uma pessoa real.
    /// </summary>
    public interface ISimulatedCustomerGuard
    {
        Task<bool> IsSimulatedConversationAsync(Guid conversaId);
    }

    public sealed class SimulatedCustomerGuard : ISimulatedCustomerGuard
    {
        private readonly NpgsqlDataSource _dataSource;
        private readonly ILogger<SimulatedCustomerGuard> _logger;

        public SimulatedCustomerGuard(NpgsqlDataSource dataSource, ILogger<SimulatedCustomerGuard> logger)
        {
            _dataSource = dataSource;
            _logger = logger;
        }

        public async Task<bool> IsSimulatedConversationAsync(Guid conversaId)
        {
            if (conversaId == Guid.Empty) return false;
            try
            {
                await using var connection = await _dataSource.OpenConnectionAsync();
                return await connection.ExecuteScalarAsync<bool>(@"
SELECT COALESCE(BOOL_OR(cl.simulado), FALSE)
  FROM conversas c
  JOIN clientes cl ON cl.id = c.id_cliente
 WHERE c.id = @Id;", new { Id = conversaId });
            }
            catch (Exception ex)
            {
                // Sem a coluna (migration pendente) ou banco indisponivel: comporta-se como antes, envia normalmente.
                _logger.LogDebug(ex, "Nao foi possivel checar cliente de teste da conversa {Conversa}.", conversaId);
                return false;
            }
        }
    }
}
