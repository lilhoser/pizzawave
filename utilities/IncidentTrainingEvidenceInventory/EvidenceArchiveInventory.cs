using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IncidentTrainingEvidenceInventory;

public static class EvidenceArchiveInventory
{
    private const string Root = "pizzawave-incident-archive-stage-20260727/";
    private const string DevelopmentCorpus = Root + "corpus-source-20260717/corpus-v1/development/blind-review-package.json";
    private const string RelationshipPrefix = Root + "experiment-20260717/incident-relationship-review-";
    private const string JavaScriptPrefix = "window.INCIDENT_RELATIONSHIP_REVIEW_PACKAGE=";

    public static InventoryReport Create(string archivePath, string? expectedSha256 = null)
    {
        archivePath = Path.GetFullPath(archivePath);
        using var hashStream = File.OpenRead(archivePath);
        var archiveSha256 = Convert.ToHexString(SHA256.HashData(hashStream));
        if (expectedSha256 is not null && !archiveSha256.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Archive SHA-256 mismatch. Expected {expectedSha256}, got {archiveSha256}.");

        var entryNames = new HashSet<string>(StringComparer.Ordinal);
        var sectionFiles = new Dictionary<string, int>(StringComparer.Ordinal);
        var sealedFiles = 0;
        long sealedBytes = 0;
        var fileCount = 0;
        var directoryCount = 0;
        JsonDocument? development = null;
        var packages = new Dictionary<string, JsonDocument>(StringComparer.Ordinal);
        var reviews = new List<(string Version, JsonDocument Document)>();

        using var archive = File.OpenRead(archivePath);
        using var gzip = new GZipStream(archive, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        while (tar.GetNextEntry() is { } entry)
        {
            var name = entry.Name.Replace('\\', '/');
            entryNames.Add(name);
            if (entry.EntryType is TarEntryType.Directory)
            {
                directoryCount++;
                continue;
            }

            fileCount++;
            var section = GetSection(name);
            sectionFiles[section] = sectionFiles.GetValueOrDefault(section) + 1;

            if (IsSealed(name))
            {
                sealedFiles++;
                sealedBytes += entry.Length;
                continue;
            }

            if (name == DevelopmentCorpus)
                development = ParseEntry(entry);
            else if (IsRelationshipPackage(name))
                packages[RelationshipVersion(name)] = ParseRelationshipPackage(entry, name.EndsWith(".js", StringComparison.Ordinal));
            else if (IsRelationshipReview(name))
                reviews.Add((RelationshipVersion(name), ParseEntry(entry)));
        }

        if (development is null)
            throw new InvalidDataException("The development corpus package is missing.");

        try
        {
            return BuildReport(
                archivePath,
                archiveSha256,
                fileCount,
                directoryCount,
                sealedFiles,
                sealedBytes,
                sectionFiles,
                entryNames,
                development,
                packages,
                reviews);
        }
        finally
        {
            development.Dispose();
            foreach (var package in packages.Values)
                package.Dispose();
            foreach (var review in reviews)
                review.Document.Dispose();
        }
    }

    private static InventoryReport BuildReport(
        string archivePath,
        string archiveSha256,
        int fileCount,
        int directoryCount,
        int sealedFiles,
        long sealedBytes,
        IReadOnlyDictionary<string, int> sectionFiles,
        IReadOnlySet<string> entryNames,
        JsonDocument development,
        IReadOnlyDictionary<string, JsonDocument> packages,
        IReadOnlyList<(string Version, JsonDocument Document)> reviews)
    {
        var developmentBundles = new List<DevelopmentBundleInventory>();
        var developmentObservationIds = new HashSet<string>(StringComparer.Ordinal);
        var developmentCallIds = new HashSet<long>();
        var duplicateObservationIds = 0;
        var duplicateCallIds = 0;
        foreach (var bundle in development.RootElement.GetProperty("bundles").EnumerateArray())
        {
            var observations = bundle.GetProperty("observations");
            developmentBundles.Add(new DevelopmentBundleInventory(
                bundle.GetProperty("bundleId").GetString()!,
                observations.GetArrayLength()));
            foreach (var observation in observations.EnumerateArray())
            {
                if (!developmentObservationIds.Add(observation.GetProperty("observationId").GetString()!))
                    duplicateObservationIds++;
                if (!developmentCallIds.Add(observation.GetProperty("callId").GetInt64()))
                    duplicateCallIds++;
            }
        }

        var packageCases = new Dictionary<string, RelationshipCase>(StringComparer.Ordinal);
        foreach (var (version, package) in packages)
        {
            foreach (var item in package.RootElement.GetProperty("cases").EnumerateArray())
            {
                var observations = item.GetProperty("observations").EnumerateArray().ToArray();
                if (observations.Length != 2)
                    throw new InvalidDataException($"Relationship case in {version} does not contain exactly two observations.");
                var sourceIds = observations.Select(SourceId).Order(StringComparer.Ordinal).ToArray();
                var audioFiles = observations.Select(o => ResolveAudioEntry(version, o.GetProperty("audio_file").GetString()!)).ToArray();
                var caseId = item.GetProperty("case_id").GetString()!;
                packageCases.Add($"{version}:{caseId}", new RelationshipCase(version, sourceIds, audioFiles));
            }
        }

        var dispositions = new Dictionary<string, int>(StringComparer.Ordinal);
        var reviewedPairKeys = new HashSet<string>(StringComparer.Ordinal);
        var reviewedSourceIds = new HashSet<string>(StringComparer.Ordinal);
        var duplicateReviewedPairs = 0;
        var missingPackageCases = 0;
        var missingAudioFiles = 0;
        foreach (var (version, review) in reviews)
        {
            foreach (var item in review.RootElement.GetProperty("cases").EnumerateArray())
            {
                var caseId = item.GetProperty("case_id").GetString()!;
                if (!packageCases.TryGetValue($"{version}:{caseId}", out var packageCase))
                {
                    missingPackageCases++;
                    continue;
                }

                var disposition = item.GetProperty("relationship_assessment").GetString()!;
                dispositions[disposition] = dispositions.GetValueOrDefault(disposition) + 1;
                var key = string.Join('|', packageCase.SourceIds);
                if (!reviewedPairKeys.Add(key))
                    duplicateReviewedPairs++;
                foreach (var sourceId in packageCase.SourceIds)
                    reviewedSourceIds.Add(sourceId);
                missingAudioFiles += packageCase.AudioEntries.Count(path => !entryNames.Contains(path));
            }
        }

        var reviewedCallIds = reviewedSourceIds
            .Select(ParseCallId)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToHashSet();

        return new InventoryReport(
            1,
            archivePath,
            archiveSha256,
            fileCount,
            directoryCount,
            sectionFiles.OrderBy(pair => pair.Key).ToDictionary(),
            new SealedMaterialInventory(sealedFiles, sealedBytes, false),
            new DevelopmentCorpusInventory(
                development.RootElement.GetProperty("corpusId").GetString()!,
                development.RootElement.GetProperty("corpusVersion").GetString()!,
                developmentBundles,
                developmentObservationIds.Count,
                developmentCallIds.Count,
                duplicateObservationIds,
                duplicateCallIds),
            new HumanReviewInventory(
                packages.Count,
                reviews.Count,
                reviews.Sum(review => review.Document.RootElement.GetProperty("cases").GetArrayLength()),
                reviewedPairKeys.Count,
                duplicateReviewedPairs,
                reviewedSourceIds.Count,
                reviewedCallIds.Count(id => developmentCallIds.Contains(id)),
                dispositions.OrderBy(pair => pair.Key).ToDictionary(),
                missingPackageCases,
                missingAudioFiles),
            new TrainingReadinessInventory(
                reviewedPairKeys.Count,
                0,
                Math.Max(0, developmentCallIds.Count - reviewedCallIds.Count(id => developmentCallIds.Contains(id))),
                "The archive contains direct human labels for call pairs, but no reviewed complete-incident membership packages in the supported evidence sets."));
    }

    private static JsonDocument ParseEntry(TarEntry entry)
    {
        if (entry.DataStream is null)
            throw new InvalidDataException($"Archive entry {entry.Name} has no data.");
        return JsonDocument.Parse(entry.DataStream);
    }

    private static JsonDocument ParseRelationshipPackage(TarEntry entry, bool javaScript)
    {
        if (!javaScript)
            return ParseEntry(entry);
        if (entry.DataStream is null)
            throw new InvalidDataException($"Archive entry {entry.Name} has no data.");
        using var reader = new StreamReader(entry.DataStream, Encoding.UTF8, true, leaveOpen: true);
        var text = reader.ReadToEnd().Trim();
        if (!text.StartsWith(JavaScriptPrefix, StringComparison.Ordinal) || !text.EndsWith(';'))
            throw new InvalidDataException($"Relationship package {entry.Name} has an unexpected wrapper.");
        return JsonDocument.Parse(text.Substring(JavaScriptPrefix.Length, text.Length - JavaScriptPrefix.Length - 1));
    }

    private static bool IsSealed(string name) =>
        name.Contains("/heldout-sealed/", StringComparison.OrdinalIgnoreCase);

    private static bool IsRelationshipPackage(string name) =>
        name == RelationshipPrefix + "v1/review-package.js" ||
        name == RelationshipPrefix + "v2/review-package.json";

    private static bool IsRelationshipReview(string name) =>
        name.StartsWith(RelationshipPrefix, StringComparison.Ordinal) &&
        name.Contains("/reviews/", StringComparison.Ordinal) &&
        name.EndsWith(".json", StringComparison.Ordinal);

    private static string RelationshipVersion(string name)
    {
        var remainder = name[RelationshipPrefix.Length..];
        return remainder[..remainder.IndexOf('/')];
    }

    private static string GetSection(string name)
    {
        if (!name.StartsWith(Root, StringComparison.Ordinal))
            return "outside-root";
        var remainder = name[Root.Length..];
        var slash = remainder.IndexOf('/');
        return slash < 0 ? "root" : remainder[..slash];
    }

    private static string SourceId(JsonElement observation) =>
        observation.GetProperty("observation_id").GetString()
        ?? throw new InvalidDataException("A relationship observation is missing its source identity.");

    private static string ResolveAudioEntry(string version, string relativePath) =>
        $"{RelationshipPrefix}{version}/{relativePath.Replace('\\', '/')}";

    private static long? ParseCallId(string sourceId) =>
        sourceId.StartsWith("call:", StringComparison.Ordinal) && long.TryParse(sourceId.AsSpan(5), out var id) ? id : null;

    private sealed record RelationshipCase(string Version, IReadOnlyList<string> SourceIds, IReadOnlyList<string> AudioEntries);
}

public sealed record InventoryReport(
    int SchemaVersion,
    string ArchivePath,
    string ArchiveSha256,
    int FileCount,
    int DirectoryCount,
    IReadOnlyDictionary<string, int> FilesBySection,
    SealedMaterialInventory SealedMaterial,
    DevelopmentCorpusInventory DevelopmentCorpus,
    HumanReviewInventory HumanReview,
    TrainingReadinessInventory TrainingReadiness);

public sealed record SealedMaterialInventory(int FileCount, long Bytes, bool ContentsRead);
public sealed record DevelopmentCorpusInventory(
    string CorpusId,
    string CorpusVersion,
    IReadOnlyList<DevelopmentBundleInventory> Bundles,
    int UniqueObservationCount,
    int UniqueCallCount,
    int DuplicateObservationCount,
    int DuplicateCallCount);
public sealed record DevelopmentBundleInventory(string BundleId, int ObservationCount);
public sealed record HumanReviewInventory(
    int PackageCount,
    int ReviewFileCount,
    int SubmittedCaseCount,
    int UniquePairCount,
    int DuplicatePairCount,
    int UniqueReviewedSourceCount,
    int ReviewedCallsFoundInDevelopmentCorpus,
    IReadOnlyDictionary<string, int> Dispositions,
    int MissingPackageCaseCount,
    int MissingAudioFileCount);
public sealed record TrainingReadinessInventory(
    int DirectHumanReviewedPairCount,
    int ReviewedCompleteIncidentPackageCount,
    int DevelopmentCallsWithoutDirectHumanRelationshipReview,
    string EvidenceLimitation);
