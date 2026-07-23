namespace APIBack.Options
{
    public sealed class DeliveryTrackingOptions
    {
        public const string SectionName = "DeliveryTracking";

        public bool Enabled { get; set; }
        public bool SimulatorEnabled { get; set; }
        public bool ApplyMigrationsOnStartup { get; set; }
        public int PresenceTtlSeconds { get; set; } = 90;
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
        public string MigrationsPath { get; set; } = "Migrations/Delivery";
    }
}
