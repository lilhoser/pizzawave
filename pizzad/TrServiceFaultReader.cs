using System.Text.Json;

namespace pizzad;

public static class TrServiceFaultReader
{
    public const string DefaultPath = "/run/pizzawave/tr-fault.json";

    public static TrServiceFaultSnapshotDto? ReadLatest(string path = DefaultPath)
    {
        try
        {
            if (OperatingSystem.IsWindows() || !File.Exists(path))
                return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var systemd = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("systemd", out var systemdElement) && systemdElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in systemdElement.EnumerateObject())
                    systemd[property.Name] = property.Value.ToString();
            }

            return new TrServiceFaultSnapshotDto(
                JsonDate(root, "createdAtUtc") ?? File.GetLastWriteTimeUtc(path),
                JsonString(root, "unit"),
                JsonString(root, "serviceResult"),
                JsonString(root, "exitCode"),
                JsonString(root, "exitStatus"),
                systemd,
                JsonStringArray(root, "journalTail"),
                JsonStringArray(root, "signatures"));
        }
        catch
        {
            return null;
        }
    }

    private static string JsonString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.ToString()
            : string.Empty;

    private static DateTime? JsonDate(JsonElement root, string name)
    {
        var raw = JsonString(root, name);
        return DateTime.TryParse(raw, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed.ToUniversalTime()
            : null;
    }

    private static IReadOnlyList<string> JsonStringArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            return [];
        return value.EnumerateArray().Select(item => item.ToString()).Where(item => !string.IsNullOrWhiteSpace(item)).ToList();
    }
}
