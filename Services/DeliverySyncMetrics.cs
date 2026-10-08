using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace APIBack.Services;

public sealed class DeliverySyncMetrics
{
    public const string MeterName = "ZippyGo.Delivery.Sync";
    private readonly Histogram<double> _requestDuration;
    private readonly Histogram<double> _databaseDuration;
    private readonly Histogram<double> _poolWait;
    private readonly Histogram<double> _outboxAge;
    private readonly Counter<long> _locations;
    private readonly Counter<long> _outboxFailures;

    public DeliverySyncMetrics(IMeterFactory factory)
    {
        var meter = factory.Create(MeterName);
        _requestDuration = meter.CreateHistogram<double>("delivery.sync.request.duration", "ms");
        _databaseDuration = meter.CreateHistogram<double>("delivery.sync.database.duration", "ms");
        _poolWait = meter.CreateHistogram<double>("delivery.sync.pool.wait", "ms");
        _outboxAge = meter.CreateHistogram<double>("delivery.sync.outbox.age", "s");
        _locations = meter.CreateCounter<long>("delivery.sync.locations", "samples");
        _outboxFailures = meter.CreateCounter<long>("delivery.sync.outbox.failures");
    }

    public void RecordRequest(string operation, int status, double duration) =>
        _requestDuration.Record(duration, new("operation", operation), new("status", status));
    public void RecordPoolWait(string operation, double duration) => _poolWait.Record(duration, new KeyValuePair<string, object?>("operation", operation));
    public void RecordLocations(string outcome, int count) => _locations.Add(count, new KeyValuePair<string, object?>("outcome", outcome));
    public void RecordPublished(DateTimeOffset occurredAt) => _outboxAge.Record(Math.Max(0, (DateTimeOffset.UtcNow - occurredAt).TotalSeconds));
    public void RecordPublishFailure() => _outboxFailures.Add(1);
    public DatabaseMeasurement MeasureDatabase(string operation) => new(_databaseDuration, operation);

    public sealed class DatabaseMeasurement(Histogram<double> histogram, string operation) : IDisposable
    {
        private readonly long _started = Stopwatch.GetTimestamp();
        private bool _completed;
        public void Complete() => _completed = true;
        public void Dispose() => histogram.Record(Stopwatch.GetElapsedTime(_started).TotalMilliseconds,
            new("operation", operation), new("outcome", _completed ? "ok" : "error"));
    }
}
