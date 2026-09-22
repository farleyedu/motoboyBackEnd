using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Options;
using Dapper;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace APIBack.Services
{
    public sealed class DeliveryMigrationHostedService : IHostedService
    {
        private const long MigrationAdvisoryLock = 75442200;
        private readonly NpgsqlDataSource _dataSource;
        private readonly IHostEnvironment _environment;
        private readonly DeliveryTrackingOptions _options;
        private readonly ILogger<DeliveryMigrationHostedService> _logger;

        public DeliveryMigrationHostedService(
            NpgsqlDataSource dataSource,
            IHostEnvironment environment,
            IOptions<DeliveryTrackingOptions> options,
            ILogger<DeliveryMigrationHostedService> logger)
        {
            _dataSource = dataSource;
            _environment = environment;
            _options = options.Value;
            _logger = logger;
        }

        /// <summary>
        /// Falha de migration do delivery NAO derruba a API: antes, uma migration com erro
        /// (ex.: min(uuid) no PG &lt; 17) impedia o boot e tirava do ar WhatsApp, reservas e
        /// todos os outros modulos. Agora o erro e registrado como critico, a transacao do
        /// arquivo e desfeita pelo proprio Postgres e so o delivery fica comprometido ate a
        /// correcao. A proxima subida retoma do arquivo que falhou (ledger).
        /// </summary>
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                await ApplyMigrationsAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex,
                    "Migrations do delivery falharam. A API continua no ar, mas o modulo de delivery pode " +
                    "falhar ate o SQL ser corrigido e o servico reiniciado.");
            }
        }

        private async Task ApplyMigrationsAsync(CancellationToken cancellationToken)
        {
            if (!_options.ApplyMigrationsOnStartup)
            {
                _logger.LogInformation(
                    "Delivery migrations automaticas desabilitadas. Execute-as no deploy ou habilite DeliveryTracking:ApplyMigrationsOnStartup conscientemente.");
                return;
            }

            var migrationsPath = Path.GetFullPath(Path.Combine(
                _environment.ContentRootPath,
                _options.MigrationsPath.Replace('/', Path.DirectorySeparatorChar)));
            if (!Directory.Exists(migrationsPath))
            {
                throw new InvalidOperationException($"Diretorio de migrations do delivery nao encontrado: {migrationsPath}");
            }

            var files = Directory.GetFiles(migrationsPath, "*.sql")
                .Where(path =>
                {
                    var name = Path.GetFileName(path);
                    return !name.EndsWith("_preflight.sql", StringComparison.OrdinalIgnoreCase) &&
                           !name.EndsWith("_verify.sql", StringComparison.OrdinalIgnoreCase);
                })
                .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
                .ToArray();

            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
            await connection.ExecuteAsync(
                "SELECT pg_advisory_lock(@LockId);",
                new { LockId = MigrationAdvisoryLock });
            try
            {
                foreach (var path in files)
                {
                    var version = Path.GetFileNameWithoutExtension(path);
                    if (await IsAppliedAsync(connection, version))
                    {
                        continue;
                    }

                    _logger.LogInformation("Aplicando migration do delivery {Version}.", version);
                    var sql = await File.ReadAllTextAsync(path, cancellationToken);
                    await connection.ExecuteAsync(new CommandDefinition(sql, cancellationToken: cancellationToken));
                }
            }
            finally
            {
                await connection.ExecuteAsync(
                    "SELECT pg_advisory_unlock(@LockId);",
                    new { LockId = MigrationAdvisoryLock });
            }
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        private static async Task<bool> IsAppliedAsync(NpgsqlConnection connection, string version)
        {
            var tableExists = await connection.ExecuteScalarAsync<bool>(
                "SELECT to_regclass('public.delivery_tracking_schema_versions') IS NOT NULL;");
            if (!tableExists)
            {
                return false;
            }

            return await connection.ExecuteScalarAsync<bool>(@"
SELECT EXISTS (
    SELECT 1
      FROM delivery_tracking_schema_versions
     WHERE version = @Version
);", new { Version = version });
        }
    }
}

