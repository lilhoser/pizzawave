using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace pizzad;

public sealed class TrLogService
{
    private readonly EngineConfig _config;
    private readonly ILogger<TrLogService> _logger;

    public TrLogService(EngineConfig config, ILogger<TrLogService> logger)
    {
        _config = config;
        _logger = logger;
    }

    public async Task<TrLogPageDto> ReadAsync(long start, long end, int requestedPageSize, string? cursor, CancellationToken ct)
    {
        var pageSize = Math.Clamp(requestedPageSize, 50, 500);
        if (start <= 0 || end <= start)
            throw new ArgumentException("The TR log range is invalid.");
        var pageToken = DecodePageToken(cursor);
        if (OperatingSystem.IsWindows())
            return new TrLogPageDto(start, end, pageSize, [], false, string.Empty, "TR journald logs are available on Linux hosts only.");

        var psi = new ProcessStartInfo("journalctl")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        psi.ArgumentList.Add("-u");
        psi.ArgumentList.Add(_config.TrunkRecorder.LogServiceName);
        psi.ArgumentList.Add("--utc");
        psi.ArgumentList.Add("--since");
        psi.ArgumentList.Add("@" + start.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("--until");
        psi.ArgumentList.Add(pageToken == null ? "@" + end.ToString(CultureInfo.InvariantCulture) : JournalTimestamp(pageToken.BeforeMicros));
        psi.ArgumentList.Add("--reverse");
        psi.ArgumentList.Add("--lines");
        psi.ArgumentList.Add((pageSize + 2).ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("--output=json");
        psi.ArgumentList.Add("--no-pager");
        try
        {
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("journalctl failed to start.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var rows = new List<ParsedLogEntry>();
            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var row = ParseEntry(line);
                if (row == null || pageToken != null && row.RealtimeMicros == pageToken.BeforeMicros && pageToken.SeenCursors.Contains(row.Entry.Cursor, StringComparer.Ordinal)) continue;
                rows.Add(row);
                if (rows.Count > pageSize) break;
            }
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            var error = await process.StandardError.ReadToEndAsync(CancellationToken.None);
            var hasOlder = rows.Count > pageSize;
            if (hasOlder) rows.RemoveRange(pageSize, rows.Count - pageSize);
            var olderCursor = string.Empty;
            if (hasOlder && rows.Count > 0)
            {
                var boundary = rows[^1].RealtimeMicros;
                var seen = rows.Where(row => row.RealtimeMicros == boundary).Select(row => row.Entry.Cursor).ToList();
                if (pageToken != null && pageToken.BeforeMicros == boundary)
                    seen.InsertRange(0, pageToken.SeenCursors);
                olderCursor = EncodePageToken(new TrLogPageToken(boundary, seen.Distinct(StringComparer.Ordinal).ToList()));
            }
            rows.Reverse();
            return new TrLogPageDto(start, end, pageSize, rows.Select(row => row.Entry).ToList(), hasOlder, olderCursor, error.Trim());
        }
        catch (Exception ex) when (ex is not ArgumentException)
        {
            _logger.LogWarning(ex, "Failed to read paged Trunk Recorder journal");
            return new TrLogPageDto(start, end, pageSize, [], false, string.Empty, "journalctl failed: " + ex.Message);
        }
    }

    private static ParsedLogEntry? ParseEntry(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var cursor = ReadString(root, "__CURSOR");
            if (string.IsNullOrWhiteSpace(cursor)) return null;
            var micros = long.TryParse(ReadString(root, "__REALTIME_TIMESTAMP"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
            var timestamp = micros > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(micros / 1000).UtcDateTime : DateTime.UnixEpoch;
            var identifier = ReadString(root, "SYSLOG_IDENTIFIER");
            if (string.IsNullOrWhiteSpace(identifier)) identifier = ReadString(root, "_COMM");
            return new ParsedLogEntry(new TrLogEntryDto(cursor, timestamp, ReadString(root, "_HOSTNAME"), identifier, ReadString(root, "_PID"), CleanJournalMessage(ReadString(root, "MESSAGE"))), micros);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ReadString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return string.Empty;
        if (value.ValueKind == JsonValueKind.String) return value.GetString() ?? string.Empty;
        if (value.ValueKind == JsonValueKind.Array)
        {
            var bytes = value.EnumerateArray()
                .Select(item => item.TryGetByte(out var parsed) ? parsed : (byte)0)
                .ToArray();
            return Encoding.UTF8.GetString(bytes);
        }
        return value.ToString();
    }

    private static string CleanJournalMessage(string value)
    {
        if (!value.Contains('\u001b')) return value;
        var output = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '\u001b' || index + 1 >= value.Length || value[index + 1] != '[')
            {
                output.Append(value[index]);
                continue;
            }
            index += 2;
            while (index < value.Length && value[index] is < '@' or > '~') index++;
        }
        return output.ToString();
    }

    private static TrLogPageToken? DecodePageToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        if (token.Length > 16_384 || token.Contains('\n') || token.Contains('\r')) throw new ArgumentException("The TR log cursor is invalid.");
        try
        {
            var padded = token.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight((padded.Length + 3) / 4 * 4, '=');
            var decoded = JsonSerializer.Deserialize<TrLogPageToken>(Convert.FromBase64String(padded));
            if (decoded == null || decoded.BeforeMicros <= 0 || decoded.SeenCursors == null || decoded.SeenCursors.Count > 1000)
                throw new ArgumentException("The TR log cursor is invalid.");
            return decoded;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw new ArgumentException("The TR log cursor is invalid.");
        }
    }

    private static string EncodePageToken(TrLogPageToken token) =>
        Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(token)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string JournalTimestamp(long micros) =>
        $"@{micros / 1_000_000}.{micros % 1_000_000:D6}";

    private sealed record ParsedLogEntry(TrLogEntryDto Entry, long RealtimeMicros);
    private sealed record TrLogPageToken(long BeforeMicros, List<string> SeenCursors);
}
