using APIBack.DTOs.Tracking;
using APIBack.Model.Tracking;

namespace APIBack.Service;

public static class OperationalLocationBatchRules
{
    public const int MaxSamples = 20;

    public static IReadOnlyList<OperationalLocationRequest> ValidateShape(OperationalLocationBatchRequest? request)
    {
        var samples = request?.Samples;
        if (samples == null || samples.Count is < 1 or > MaxSamples)
            throw new DeliveryDomainException(422, "LOCATION_BATCH_INVALID", "Envie de 1 a 20 localizacoes por lote.");
        long sequence = 0;
        var ids = new HashSet<Guid>();
        foreach (var sample in samples)
        {
            if (sample == null || !sample.SampleId.HasValue || sample.SampleId == Guid.Empty ||
                !ids.Add(sample.SampleId.Value) || sample.Sequence <= sequence)
                throw new DeliveryDomainException(422, "LOCATION_BATCH_INVALID", "sampleId deve ser unico e sequence deve estar em ordem crescente.");
            sequence = sample.Sequence;
        }
        return samples;
    }

    // A sessao fica bloqueada no banco antes deste planejamento. Repeticao exata e segura;
    // um ponto antigo desconhecido nao pode fazer a posicao atual regredir.
    public static IReadOnlyList<OperationalLocationWriteResult> Plan(
        IReadOnlyList<OperationalLocationWrite> locations, IReadOnlyCollection<StoredLocationSample> stored,
        long currentSequence, long version, DateTimeOffset serverNow, bool rejectStale = false)
    {
        var byId = stored.ToDictionary(s => s.SampleId);
        var bySequence = stored.ToDictionary(s => s.Sequence);
        var results = new List<OperationalLocationWriteResult>();
        foreach (var location in locations)
        {
            byId.TryGetValue(location.SampleId, out var bySample);
            bySequence.TryGetValue(location.Sequence, out var byNumber);
            var duplicate = bySample ?? byNumber;
            if (duplicate != null)
            {
                if (duplicate.SampleId != location.SampleId || duplicate.Sequence != location.Sequence ||
                    !string.Equals(duplicate.PayloadHash, location.PayloadHash, StringComparison.Ordinal) ||
                    (bySample != null && byNumber != null && bySample.SampleId != byNumber.SampleId))
                    throw new DeliveryDomainException(409, "SEQUENCE_CONFLICT", "sampleId ou sequence ja foi usada com outro conteudo.");
                results.Add(new OperationalLocationWriteResult { Outcome = "duplicate", UpdatedCurrent = duplicate.UpdatedCurrent,
                    SessionVersion = version, ReceivedAtUtc = duplicate.ReceivedAtUtc });
            }
            else if (location.Sequence <= currentSequence)
            {
                if (rejectStale)
                    throw new DeliveryDomainException(409, location.Sequence == currentSequence ? "SEQUENCE_CONFLICT" : "STALE_SEQUENCE", "A sequence nao e posterior a ultima posicao aceita.");
                results.Add(new OperationalLocationWriteResult { Outcome = "stale", SessionVersion = version, ReceivedAtUtc = serverNow });
            }
            else
            {
                results.Add(new OperationalLocationWriteResult { Outcome = "accepted", SessionVersion = version, ReceivedAtUtc = serverNow });
            }
        }
        var latest = results.LastOrDefault(r => r.Outcome == "accepted");
        if (latest != null) latest.UpdatedCurrent = true;
        return results;
    }
}
