namespace pizzad;

public sealed record IncidentIdentityResolution(string? IncidentKey, bool Accepted, string Reason);

public static class IncidentIdentity
{
    public static IncidentIdentityResolution ResolveManagedIncidentKey(
        string systemShortName,
        string? suppliedIncidentId,
        string? title,
        IReadOnlyCollection<long> callIds,
        IReadOnlyDictionary<string, IncidentDto> existingByKey)
    {
        if (callIds.Count == 0)
            throw new ArgumentException("At least one call id is required.", nameof(callIds));

        var supplied = (suppliedIncidentId ?? string.Empty).Trim();
        if (IsCreateMarker(supplied))
            return new(BuildServerOwnedKey(systemShortName, callIds), true, "created server-owned incident key");

        if (existingByKey.ContainsKey(supplied))
            return new(supplied, true, "accepted existing incident key");

        return new(BuildServerOwnedKey(systemShortName, callIds), true, $"ignored unknown model incident_id '{supplied}'; created server-owned incident key");
    }

    public static bool IsCreateMarker(string? value)
    {
        value = (value ?? string.Empty).Trim();
        return
        string.IsNullOrWhiteSpace(value)
        || value.Equals("new", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("new-", StringComparison.OrdinalIgnoreCase)
        || value.Equals("create", StringComparison.OrdinalIgnoreCase)
        || value.Equals("__new__", StringComparison.OrdinalIgnoreCase);
    }

    public static string BuildServerOwnedKey(string systemShortName, IReadOnlyCollection<long> callIds)
    {
        if (callIds.Count == 0)
            throw new ArgumentException("At least one call id is required.", nameof(callIds));

        return $"llm:{systemShortName}:{callIds.Min()}:event";
    }
}
