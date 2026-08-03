using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using IncidentTrainingEvidenceInventory;

public sealed class IncidentTrainingEvidenceInventoryTests
{
    [Fact]
    public void Create_DeduplicatesReviewsAndNeverReadsSealedContent()
    {
        var path = CreateArchive(new Dictionary<string, string>
        {
            ["pizzawave-incident-archive-stage-20260727/corpus-source-20260717/corpus-v1/development/blind-review-package.json"] = DevelopmentJson,
            ["pizzawave-incident-archive-stage-20260727/corpus-source-20260717/corpus-v1/heldout-sealed/corpus.json"] = "not-json-by-design",
            ["pizzawave-incident-archive-stage-20260727/experiment-20260717/incident-relationship-review-v2/review-package.json"] = PackageJson,
            ["pizzawave-incident-archive-stage-20260727/experiment-20260717/incident-relationship-review-v2/reviews/review.json"] = ReviewJson,
            ["pizzawave-incident-archive-stage-20260727/experiment-20260717/incident-relationship-review-v2/audio/a.wav"] = "audio-a",
            ["pizzawave-incident-archive-stage-20260727/experiment-20260717/incident-relationship-review-v2/audio/b.wav"] = "audio-b"
        });

        try
        {
            var report = EvidenceArchiveInventory.Create(path);

            Assert.False(report.SealedMaterial.ContentsRead);
            Assert.Equal(1, report.SealedMaterial.FileCount);
            Assert.Equal(2, report.DevelopmentCorpus.UniqueCallCount);
            Assert.Equal(1, report.HumanReview.UniquePairCount);
            Assert.Equal(1, report.HumanReview.DuplicatePairCount);
            Assert.Equal(2, report.HumanReview.SubmittedCaseCount);
            Assert.Equal(2, report.HumanReview.Dispositions["same_event"]);
            Assert.Equal(0, report.HumanReview.MissingAudioFileCount);
            Assert.Equal(0, report.TrainingReadiness.ReviewedCompleteIncidentPackageCount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Create_RejectsWrongArchiveHash()
    {
        var path = CreateArchive(new Dictionary<string, string>
        {
            ["pizzawave-incident-archive-stage-20260727/corpus-source-20260717/corpus-v1/development/blind-review-package.json"] = DevelopmentJson
        });
        try
        {
            Assert.Throws<InvalidDataException>(() => EvidenceArchiveInventory.Create(path, new string('0', 64)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string CreateArchive(IReadOnlyDictionary<string, string> entries)
    {
        var path = Path.Combine(Path.GetTempPath(), $"incident-inventory-{Guid.NewGuid():N}.tar.gz");
        using var file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionLevel.Fastest);
        using var writer = new TarWriter(gzip, leaveOpen: false);
        foreach (var (name, content) in entries)
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
            {
                DataStream = new MemoryStream(bytes)
            };
            writer.WriteEntry(entry);
        }
        return path;
    }

    private const string DevelopmentJson = """
        {"corpusId":"development","corpusVersion":"v1","bundles":[{"bundleId":"b1","observations":[{"observationId":"call:1","callId":1},{"observationId":"call:2","callId":2}],"priorState":[]}]}
        """;

    private const string PackageJson = """
        {"cases":[{"case_id":"case-1","observations":[{"observation_id":"call:1","audio_file":"audio/a.wav"},{"observation_id":"call:2","audio_file":"audio/b.wav"}]}]}
        """;

    private const string ReviewJson = """
        {"cases":[{"case_id":"case-1","relationship_assessment":"same_event"},{"case_id":"case-1","relationship_assessment":"same_event"}]}
        """;
}
