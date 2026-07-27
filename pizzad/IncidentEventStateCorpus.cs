using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace pizzad;

public sealed record IncidentEventStateCorpusManifest(
    string CorpusId,
    string CorpusVersion,
    DateTimeOffset CreatedAtUtc,
    long WindowStartUnixSeconds,
    long WindowEndUnixSeconds,
    string SelectionProtocolIdentity,
    string SoftwareVersion);

public sealed record IncidentEventStateCorpusDocument(
    IncidentEventStateCorpusManifest Manifest,
    IReadOnlyList<IncidentEventStateObservationBundle> Bundles);

public sealed record IncidentEventStateStoredCorpus(
    string ContentHash,
    string Json,
    IncidentEventStateCorpusDocument Document);

public static class IncidentEventStateCorpusExporter
{
    public static IncidentEventStateObservationBundle BuildObservationBundle(
        string bundleId,
        DateTimeOffset createdAtUtc,
        IEnumerable<EngineCall> calls,
        IReadOnlyList<IncidentEventStateProjectedEvent>? priorState = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundleId);
        ArgumentNullException.ThrowIfNull(calls);

        var observations = calls
            .OrderBy(call => call.StartTime)
            .ThenBy(call => call.Id)
            .Select(ToObservation)
            .ToList();
        var bundle = new IncidentEventStateObservationBundle(
            bundleId,
            createdAtUtc,
            observations,
            priorState ?? []);
        var validation = IncidentEventStateContractValidator.ValidateBundle(bundle);
        if (!validation.IsValid)
            throw new ArgumentException(string.Join("; ", validation.Errors), nameof(calls));
        return bundle;
    }

    public static IncidentEventStateStoredCorpus Freeze(
        IncidentEventStateCorpusManifest manifest,
        IReadOnlyList<IncidentEventStateObservationBundle> bundles)
    {
        ValidateManifest(manifest);
        ArgumentNullException.ThrowIfNull(bundles);
        if (bundles.Count == 0)
            throw new ArgumentException("A corpus must contain at least one observation bundle.", nameof(bundles));
        if (bundles.Select(bundle => bundle.BundleId).Distinct(StringComparer.Ordinal).Count() != bundles.Count)
            throw new ArgumentException("Corpus bundle ids must be unique.", nameof(bundles));

        foreach (var bundle in bundles)
        {
            var validation = IncidentEventStateContractValidator.ValidateBundle(bundle);
            if (!validation.IsValid)
                throw new ArgumentException(string.Join("; ", validation.Errors), nameof(bundles));
            if (bundle.Observations.Any(observation =>
                    observation.ObservedAtUnixSeconds < manifest.WindowStartUnixSeconds ||
                    observation.ObservedAtUnixSeconds > manifest.WindowEndUnixSeconds))
            {
                throw new ArgumentException(
                    $"Bundle '{bundle.BundleId}' contains an observation outside the frozen corpus window.",
                    nameof(bundles));
            }
        }

        var document = new IncidentEventStateCorpusDocument(manifest, bundles);
        var json = JsonSerializer.Serialize(document, EngineConfig.JsonOptions());
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        return new IncidentEventStateStoredCorpus(hash, json, document);
    }

    private static IncidentEventStateSourceObservation ToObservation(EngineCall call)
    {
        var observationId = $"call:{call.Id.ToString(CultureInfo.InvariantCulture)}";
        IReadOnlyList<IncidentEventStateTranscriptObservation> transcripts =
            string.IsNullOrWhiteSpace(call.Transcription)
                ? []
                :
                [
                    new IncidentEventStateTranscriptObservation(
                        $"{observationId}:stored-transcript",
                        call.Transcription,
                        "pizzad.calls.transcription",
                        null)
                ];
        var durationMilliseconds = call.StopTime >= call.StartTime
            ? checked((call.StopTime - call.StartTime) * 1000)
            : (long?)null;

        return new IncidentEventStateSourceObservation(
            observationId,
            call.Id,
            call.StartTime,
            call.AudioPath,
            durationMilliseconds,
            transcripts,
            new Dictionary<string, IncidentEventStateMetadataObservation>(StringComparer.Ordinal)
            {
                ["uniqueKey"] = Source(call.UniqueKey),
                ["stopTimeUnixSeconds"] = Source(call.StopTime.ToString(CultureInfo.InvariantCulture)),
                ["source"] = Source(call.Source.ToString(CultureInfo.InvariantCulture)),
                ["systemShortName"] = Source(call.SystemShortName),
                ["callstreamCallId"] = Source(call.CallstreamCallId.ToString(CultureInfo.InvariantCulture)),
                ["talkgroup"] = Source(call.Talkgroup.ToString(CultureInfo.InvariantCulture)),
                ["talkgroupName"] = Source(call.TalkgroupName),
                ["frequency"] = Source(call.Frequency.ToString("R", CultureInfo.InvariantCulture)),
                ["category"] = Derived(call.Category),
                ["transcriptionStatus"] = Derived(call.TranscriptionStatus),
                ["qualityReason"] = Derived(call.QualityReason),
                ["isImported"] = Derived(call.IsImported.ToString(CultureInfo.InvariantCulture)),
                ["isAlertMatch"] = Derived(call.IsAlertMatch.ToString(CultureInfo.InvariantCulture)),
                ["rawMetadataJson"] = new(call.RawMetadataJson, IncidentEventStateMetadataOrigin.RawPayload)
            });
    }

    private static IncidentEventStateMetadataObservation Source(string value) =>
        new(value, IncidentEventStateMetadataOrigin.SourceRecord);

    private static IncidentEventStateMetadataObservation Derived(string value) =>
        new(value, IncidentEventStateMetadataOrigin.ApplicationDerived);

    private static void ValidateManifest(IncidentEventStateCorpusManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (string.IsNullOrWhiteSpace(manifest.CorpusId) ||
            string.IsNullOrWhiteSpace(manifest.CorpusVersion) ||
            string.IsNullOrWhiteSpace(manifest.SelectionProtocolIdentity) ||
            string.IsNullOrWhiteSpace(manifest.SoftwareVersion))
        {
            throw new ArgumentException("Corpus identity, version, selection protocol, and software version are required.", nameof(manifest));
        }
        if (manifest.CreatedAtUtc == default)
            throw new ArgumentException("Corpus creation timestamp is required.", nameof(manifest));
        if (manifest.WindowStartUnixSeconds < 0 || manifest.WindowEndUnixSeconds < manifest.WindowStartUnixSeconds)
            throw new ArgumentException("Corpus window is invalid.", nameof(manifest));
    }
}
