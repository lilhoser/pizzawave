namespace pizzad.Tests;

public sealed class IncidentIdentityTests
{
    [Theory]
    [InlineData("")]
    [InlineData("new")]
    [InlineData("new-incident")]
    [InlineData("create")]
    [InlineData("__new__")]
    public void ResolveManagedIncidentKey_MintsServerOwnedKeyForCreateMarkers(string suppliedIncidentId)
    {
        var resolution = IncidentIdentity.ResolveManagedIncidentKey(
            "ot",
            suppliedIncidentId,
            "Chest pain at 1400 God St",
            [649365],
            new Dictionary<string, IncidentDto>(StringComparer.OrdinalIgnoreCase));

        Assert.True(resolution.Accepted);
        Assert.Equal("llm:ot:649365:event", resolution.IncidentKey);
    }

    [Fact]
    public void ResolveManagedIncidentKey_AcceptsExactExistingKey()
    {
        var existing = new Dictionary<string, IncidentDto>(StringComparer.OrdinalIgnoreCase)
        {
            ["llm:ot:100:existing-incident"] = new IncidentDto
            {
                IncidentKey = "llm:ot:100:existing-incident",
                Calls = [new IncidentCallDto(100, 1000, "Older related call", "")]
            }
        };

        var resolution = IncidentIdentity.ResolveManagedIncidentKey(
            "ot",
            "llm:ot:100:existing-incident",
            "Updated related incident",
            [101],
            existing);

        Assert.True(resolution.Accepted);
        Assert.Equal("llm:ot:100:existing-incident", resolution.IncidentKey);
    }

    [Theory]
    [InlineData("INC-20260611-001")]
    [InlineData("INC-1154STORMYRIDGE-001")]
    [InlineData("llm:ot:999:model-invented-key")]
    public void ResolveManagedIncidentKey_IgnoresUnknownModelKeysAndMintsServerOwnedKey(string suppliedIncidentId)
    {
        var resolution = IncidentIdentity.ResolveManagedIncidentKey(
            "ot",
            suppliedIncidentId,
            "Assault at Furniture Rd",
            [666461],
            new Dictionary<string, IncidentDto>(StringComparer.OrdinalIgnoreCase));

        Assert.True(resolution.Accepted);
        Assert.Equal("llm:ot:666461:event", resolution.IncidentKey);
        Assert.Contains("ignored unknown model incident_id", resolution.Reason);
    }
}
