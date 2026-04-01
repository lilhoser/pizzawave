using Newtonsoft.Json;

namespace pizzapi;

public sealed class AlertHistoryStore
{
    private readonly string _path;
    private readonly object _lock = new();

    public AlertHistoryStore(string? path = null)
    {
        _path = path ?? Path.Combine(pizzalib.Settings.DefaultWorkingDirectory, "alerts", "matches.jsonl");
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    public List<AlertMatchRecord> LoadAll()
    {
        lock (_lock)
        {
            if (!File.Exists(_path))
            {
                return new List<AlertMatchRecord>();
            }

            var records = new List<AlertMatchRecord>();
            foreach (var line in File.ReadLines(_path))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    var record = JsonConvert.DeserializeObject<AlertMatchRecord>(line);
                    if (record != null)
                    {
                        records.Add(record);
                    }
                }
                catch
                {
                    // Ignore malformed historical lines.
                }
            }

            return records
                .OrderByDescending(r => r.TimestampUnix)
                .ThenByDescending(r => r.MatchedAtUtc)
                .ToList();
        }
    }

    public void Append(AlertMatchRecord record)
    {
        lock (_lock)
        {
            var json = JsonConvert.SerializeObject(record, Formatting.None);
            File.AppendAllText(_path, json + Environment.NewLine);
        }
    }

    public int MarkAllRead()
    {
        lock (_lock)
        {
            var records = LoadAll();
            var unread = records.Where(r => !r.IsRead).ToList();
            if (unread.Count == 0)
            {
                return 0;
            }

            var now = DateTime.UtcNow;
            foreach (var item in unread)
            {
                item.IsRead = true;
                item.ReadAtUtc = now;
            }

            Rewrite(records);
            return unread.Count;
        }
    }

    public int CountUnread()
    {
        return LoadAll().Count(r => !r.IsRead);
    }

    public int PruneOlderThan(DateTime cutoffLocal)
    {
        lock (_lock)
        {
            var records = LoadAll();
            if (records.Count == 0)
                return 0;

            bool ShouldDelete(AlertMatchRecord record)
            {
                if (cutoffLocal == DateTime.MaxValue)
                    return true;

                var matchedAtLocal = record.MatchedAtUtc.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(record.MatchedAtUtc, DateTimeKind.Utc).ToLocalTime()
                    : record.MatchedAtUtc.ToLocalTime();

                if (record.MatchedAtUtc > DateTime.MinValue && matchedAtLocal < cutoffLocal)
                    return true;

                if (record.TimestampUnix > 0)
                {
                    var tsLocal = DateTimeOffset.FromUnixTimeSeconds(record.TimestampUnix).LocalDateTime;
                    if (tsLocal < cutoffLocal)
                        return true;
                }

                return false;
            }

            var kept = records.Where(r => !ShouldDelete(r)).ToList();
            var removed = records.Count - kept.Count;
            if (removed <= 0)
                return 0;

            Rewrite(kept);
            return removed;
        }
    }

    private void Rewrite(List<AlertMatchRecord> records)
    {
        var tmp = _path + ".tmp";
        using (var writer = new StreamWriter(tmp, false))
        {
            foreach (var record in records)
            {
                var json = JsonConvert.SerializeObject(record, Formatting.None);
                writer.WriteLine(json);
            }
        }

        File.Copy(tmp, _path, overwrite: true);
        File.Delete(tmp);
    }
}
