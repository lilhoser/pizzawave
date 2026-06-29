using System.Text.Json;

namespace pizzad;

public static class TrServiceControlStateReader
{
    public const string DefaultPath = "/run/pizzawave/tr-control.json";

    public static TrServiceControlStateDto? ReadLatest(string path = DefaultPath)
    {
        try
        {
            if (OperatingSystem.IsWindows() || !File.Exists(path))
                return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            return new TrServiceControlStateDto(
                JsonDate(root, "createdAtUtc") ?? File.GetLastWriteTimeUtc(path),
                JsonString(root, "unit"),
                JsonString(root, "state"),
                JsonString(root, "reason"));
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
}
