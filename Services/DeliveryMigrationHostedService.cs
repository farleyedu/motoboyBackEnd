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

        public async Task StartAsync(CancellationToken cancellationToken)
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

