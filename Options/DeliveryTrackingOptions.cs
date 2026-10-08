namespace APIBack.Options
{
    public sealed class DeliveryTrackingOptions
    {
        public const string SectionName = "DeliveryTracking";

        public bool Enabled { get; set; }
        public bool SimulatorEnabled { get; set; }
        public bool ApplyMigrationsOnStartup { get; set; }
        public int PresenceTtlSeconds { get; set; } = 90;
        /// <summary>
        /// Validade da presenca de uma sessao do simulador. O motoboy simulado so fica offline
        /// quando o operador aperta o botao: aba em segundo plano ou congelada perde o
        /// heartbeat e nao pode derrubar a sessao. O padrao (30 dias) e "nunca" na pratica.
        /// </summary>
        public int SimulatorPresenceTtlSeconds { get; set; } = 30 * 24 * 60 * 60;
        public int HeartbeatIntervalSeconds { get; set; } = 25;
        public int LocationFreshnessSeconds { get; set; } = 120;
        public int OperationalTokenExpirationMinutes { get; set; } = 60;
        public int MaxCapturedAtFutureSeconds { get; set; } = 120;
        public int MaxCapturedAtPastSeconds { get; set; } = 86400;
        public double MaxAccuracyMeters { get; set; } = 2000;
        public double MaxSpeedMetersPerSecond { get; set; } = 100;
        public int LocationRetentionDays { get; set; } = 30;
        public int ExpirationWorkerIntervalSeconds { get; set; } = 10;
        public int OutboxPollIntervalMilliseconds { get; set; } = 1000;
        public int OutboxBatchSize { get; set; } = 100;
        public int OutboxLeaseSeconds { get; set; } = 60;
        public int OutboxSendTimeoutSeconds { get; set; } = 5;
        public string MigrationsPath { get; set; } = "Migrations/Delivery";
        /// <summary>
        /// Pastas extras varridas junto com <see cref="MigrationsPath"/> no boot (mesmo lock,
        /// mesmo ledger). Financeiro fica de fora de proposito: ainda nao tem seed/versionamento
        /// e so deve rodar quando o modulo for ativado de verdade.
        /// </summary>
        public string[] AdditionalMigrationsPaths { get; set; } = new[] { "Migrations/Negocio" };
    }
}
