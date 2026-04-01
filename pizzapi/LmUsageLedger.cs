using Newtonsoft.Json;
using System.Collections.Concurrent;
using System.Text;

namespace pizzapi;

public sealed class LmUsageLedgerEntry
{
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public string TriggerActivity { get; set; } = "other";
    public string RequestKind { get; set; } = "chat.completions";
    public int Attempt { get; set; } = 1;
    public bool Success { get; set; }
    public string Error { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
    public string RequestModel { get; set; } = string.Empty;
    public string ResponseModel { get; set; } = string.Empty;
    public string ResponseId { get; set; } = string.Empty;
    public string FinishReason { get; set; } = string.Empty;
    public long ResponseCreatedUnix { get; set; }
    public int InputChars { get; set; }
    public int PayloadChars { get; set; }
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int TotalTokens { get; set; }
    public string UsageJson { get; set; } = string.Empty;
    public string StatsJson { get; set; } = string.Empty;
}

public sealed class LmUsageLedgerStore
{
    private readonly string _path;
    private readonly object _lock;
    private static readonly ConcurrentDictionary<string, object> FileLocks = new(StringComparer.OrdinalIgnoreCase);

    public LmUsageLedgerStore(string? path = null)
    {
        _path = path ?? Path.Combine(pizzalib.Settings.DefaultWorkingDirectory, "logs", "lm-link-usage-ledger.jsonl");
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);
        _lock = FileLocks.GetOrAdd(_path, _ => new object());
    }

    public string LedgerPath => _path;

    public void Append(LmUsageLedgerEntry entry)
    {
        lock (_lock)
        {
            var json = JsonConvert.SerializeObject(entry, Formatting.None);
            using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var writer = new StreamWriter(stream, Encoding.UTF8);
            writer.WriteLine(json);
        }
    }

    public List<LmUsageLedgerEntry> LoadAll()
    {
        lock (_lock)
        {
            if (!File.Exists(_path))
                return new List<LmUsageLedgerEntry>();

            var result = new List<LmUsageLedgerEntry>();
            try
            {
                using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                while (!reader.EndOfStream)
                {
                    var line = reader.ReadLine();
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    try
                    {
                        var entry = JsonConvert.DeserializeObject<LmUsageLedgerEntry>(line);
                        if (entry != null)
                            result.Add(entry);
                    }
                    catch
                    {
                        // Ignore malformed lines.
                    }
                }
            }
            catch (IOException)
            {
                return new List<LmUsageLedgerEntry>();
            }
            catch (UnauthorizedAccessException)
            {
                return new List<LmUsageLedgerEntry>();
            }
            return result.OrderByDescending(x => x.TimestampUtc).ToList();
        }
    }
}
