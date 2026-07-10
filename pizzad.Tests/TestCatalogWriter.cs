using System.Text;
using System.Text.Json;

namespace pizzad.Tests;

internal static class TestCatalogWriter
{
    public static async Task WriteAsync(EngineConfig config, TalkgroupCatalogDocument document)
    {
        var path = string.IsNullOrWhiteSpace(config.TrunkRecorder.TalkgroupCatalogPath)
            ? Path.Combine(config.Storage.AppDataRoot, "talkgroups.json")
            : config.TrunkRecorder.TalkgroupCatalogPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(document, EngineConfig.JsonOptions()) + Environment.NewLine,
            new UTF8Encoding(false));
    }
}
