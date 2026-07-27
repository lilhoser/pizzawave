using pizzad;

namespace pizzad.Tests;

public sealed class EmbeddingSearchResponseParserTests
{
    [Fact]
    public void ParsePointVectorMapReadsVectorsByCallId()
    {
        const string json = """
            {"result":[{"id":22,"vector":[0.2,0.3]},{"id":11,"vector":[0.0,0.1]}],"status":"ok"}
            """;

        var vectors = EmbeddingSearchResponseParser.ParsePointVectorMap(json, 2);

        Assert.Equal([0.0f, 0.1f], vectors[11]);
        Assert.Equal([0.2f, 0.3f], vectors[22]);
    }

    [Fact]
    public void ParsePointVectorMapRejectsWrongDimensions()
    {
        const string json = """
            {"result":[{"id":11,"vector":[0.0,0.1]}],"status":"ok"}
            """;

        var error = Assert.Throws<InvalidDataException>(() =>
            EmbeddingSearchResponseParser.ParsePointVectorMap(json, 3));

        Assert.Contains("size 2", error.Message);
    }

    [Fact]
    public void ParseSingleReadsValidMatchesAndSkipsInvalidIds()
    {
        const string json = """
            {"result":[{"id":42,"score":0.91},{"id":"bad","score":0.5},{"id":84,"score":0.72}]}
            """;

        var rows = EmbeddingSearchResponseParser.ParseSingle(json);

        Assert.Equal([42L, 84L], rows.Select(row => row.CallId));
        Assert.Equal([0.91, 0.72], rows.Select(row => row.Score));
    }

    [Fact]
    public void ParseBatchPreservesQueryOrderAndEmptyResultSets()
    {
        const string json = """
            {"result":[[{"id":11,"score":0.8}],[],[{"id":33,"score":0.6},{"id":34,"score":0.5}]],"status":"ok"}
            """;

        var batches = EmbeddingSearchResponseParser.ParseBatch(json);

        Assert.Equal(3, batches.Count);
        Assert.Equal([11L], batches[0].Select(row => row.CallId));
        Assert.Empty(batches[1]);
        Assert.Equal([33L, 34L], batches[2].Select(row => row.CallId));
    }

    [Fact]
    public void ParseBatchReturnsEmptyForMalformedEnvelope()
    {
        Assert.Empty(EmbeddingSearchResponseParser.ParseBatch("{\"status\":\"ok\"}"));
    }
}
