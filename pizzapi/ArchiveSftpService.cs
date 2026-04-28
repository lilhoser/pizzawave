using pizzalib;
using Renci.SshNet;
using Renci.SshNet.Sftp;
using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

namespace pizzapi;

public sealed class ArchiveFolderItem
{
    public string Name { get; set; } = string.Empty;
    public string RemotePath { get; set; } = string.Empty;
    public string ParentRemotePath { get; set; } = string.Empty;
    public DateTime ModifiedLocal { get; set; }
    public bool IsDirectory { get; set; }
    public int BinFileCount { get; set; }
    public string DisplayModified => ModifiedLocal == DateTime.MinValue ? string.Empty : ModifiedLocal.ToString("g");
    public string DisplayType => IsDirectory ? "Folder" : "File";
    public string DisplayBinCount => BinFileCount > 0 ? BinFileCount.ToString() : string.Empty;
}

public sealed class ArchiveSftpService
{
    private readonly Settings _settings;
    private readonly string? _password;
    private readonly string? _privateKeyPassphrase;

    public ArchiveSftpService(
        Settings settings,
        string? password = null,
        string? privateKeyPassphrase = null)
    {
        _settings = settings;
        _password = password;
        _privateKeyPassphrase = privateKeyPassphrase;
    }

    public string GetConfiguredRootPath()
    {
        return NormalizeRemotePath(_settings.ArchiveSftpRemoteRoot);
    }

    public Task<IReadOnlyList<ArchiveFolderItem>> ListArchivesAsync(
        string? remotePath,
        DateTime? startLocal,
        DateTime? endLocal,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            using var client = CreateClient();
            client.Connect();
            cancellationToken.ThrowIfCancellationRequested();

            var root = string.IsNullOrWhiteSpace(remotePath)
                ? NormalizeRemotePath(_settings.ArchiveSftpRemoteRoot)
                : NormalizeRemotePath(remotePath);
            var entries = client.ListDirectory(root)
                .Where(e => e.Name != "." && e.Name != ".." && !e.Name.StartsWith('.'))
                .Where(e => e.IsDirectory || e.Name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
                .Select(e => ToArchiveItem(client, e, root))
                .Where(item => IsInRange(item, startLocal, endLocal))
                .OrderByDescending(item => item.IsDirectory)
                .ThenByDescending(item => item.ModifiedLocal)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            client.Disconnect();
            return (IReadOnlyList<ArchiveFolderItem>)entries;
        }, cancellationToken);
    }

    public Task TestConnectionAsync(CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            using var client = CreateClient();
            client.Connect();
            cancellationToken.ThrowIfCancellationRequested();
            var root = NormalizeRemotePath(_settings.ArchiveSftpRemoteRoot);
            if (!client.Exists(root))
                throw new DirectoryNotFoundException($"Remote archive root not found: {root}");
            client.Disconnect();
        }, cancellationToken);
    }

    public Task<string> DownloadArchiveAsync(ArchiveFolderItem item, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            using var client = CreateClient();
            client.Connect();
            cancellationToken.ThrowIfCancellationRequested();

            var cacheRoot = ResolveCacheRoot();
            var sessionRoot = Path.Combine(cacheRoot, MakeSafePathSegment(_settings.ArchiveSftpHost), MakeSafePathSegment(item.Name));
            Directory.CreateDirectory(sessionRoot);

            if (item.IsDirectory)
            {
                DownloadDirectory(client, item.RemotePath, sessionRoot, progress, cancellationToken);
            }
            else
            {
                DownloadFile(client, item.RemotePath, Path.Combine(sessionRoot, item.Name), progress, cancellationToken);
            }

            client.Disconnect();
            return sessionRoot;
        }, cancellationToken);
    }

    private SftpClient CreateClient()
    {
        var host = Require(_settings.ArchiveSftpHost, "Archive SFTP host");
        var user = Require(_settings.ArchiveSftpUsername, "Archive SFTP username");
        var port = _settings.ArchiveSftpPort <= 0 ? 22 : _settings.ArchiveSftpPort;
        var authMode = (_settings.ArchiveSftpAuthMode ?? "password").Trim().ToLowerInvariant();

        if (authMode == "privatekey")
        {
            var keyPath = Require(_settings.ArchiveSftpPrivateKeyPath, "Archive SFTP private key path");
            PrivateKeyFile keyFile = string.IsNullOrWhiteSpace(_privateKeyPassphrase)
                ? new PrivateKeyFile(keyPath)
                : new PrivateKeyFile(keyPath, _privateKeyPassphrase);
            var connection = new ConnectionInfo(host, port, user, new PrivateKeyAuthenticationMethod(user, keyFile));
            return new SftpClient(connection);
        }

        return new SftpClient(host, port, user, _password ?? string.Empty);
    }

    private static ArchiveFolderItem ToArchiveItem(SftpClient client, ISftpFile entry, string parentRemotePath)
    {
        return new ArchiveFolderItem
        {
            Name = entry.Name,
            RemotePath = entry.FullName,
            ParentRemotePath = parentRemotePath,
            ModifiedLocal = entry.LastWriteTime,
            IsDirectory = entry.IsDirectory,
            BinFileCount = entry.IsDirectory ? CountImmediateBinFiles(client, entry.FullName) : 1
        };
    }

    private static int CountImmediateBinFiles(SftpClient client, string remotePath)
    {
        try
        {
            return client.ListDirectory(remotePath)
                .Count(e => !e.IsDirectory && e.Name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return 0;
        }
    }

    private static bool IsInRange(ArchiveFolderItem item, DateTime? startLocal, DateTime? endLocal)
    {
        if (item.IsDirectory)
            return true;

        var timestamp = TryParseTimestampFromName(item.Name) ?? item.ModifiedLocal;
        if (startLocal.HasValue && timestamp < startLocal.Value)
            return false;
        if (endLocal.HasValue && timestamp > endLocal.Value)
            return false;
        return true;
    }

    private static DateTime? TryParseTimestampFromName(string name)
    {
        var match = Regex.Match(name, @"(?<date>\d{4}-\d{2}-\d{2})(?:[-_ ](?<time>\d{6}))?");
        if (!match.Success)
            return null;

        var value = match.Groups["time"].Success
            ? $"{match.Groups["date"].Value} {match.Groups["time"].Value}"
            : $"{match.Groups["date"].Value} 000000";
        return DateTime.TryParseExact(
            value,
            "yyyy-MM-dd HHmmss",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,
            out var parsed)
            ? parsed
            : null;
    }

    private static void DownloadDirectory(
        SftpClient client,
        string remotePath,
        string localPath,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(localPath);
        foreach (var entry in client.ListDirectory(remotePath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Name == "." || entry.Name == "..")
                continue;

            var childLocalPath = Path.Combine(localPath, entry.Name);
            if (entry.IsDirectory)
            {
                DownloadDirectory(client, entry.FullName, childLocalPath, progress, cancellationToken);
            }
            else if (entry.Name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
            {
                DownloadFile(client, entry.FullName, childLocalPath, progress, cancellationToken);
            }
        }
    }

    private static void DownloadFile(
        SftpClient client,
        string remotePath,
        string localPath,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(localPath) ?? ".");
        progress?.Report($"Downloading {Path.GetFileName(remotePath)}");
        using var output = File.Open(localPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        client.DownloadFile(remotePath, output);
    }

    private string ResolveCacheRoot()
    {
        var configured = _settings.ArchiveLocalCachePath;
        var cacheRoot = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Settings.DefaultOfflineCaptureDirectory, "sftp-cache")
            : Environment.ExpandEnvironmentVariables(configured);
        Directory.CreateDirectory(cacheRoot);
        return cacheRoot;
    }

    private static string NormalizeRemotePath(string? path)
    {
        var normalized = Require(path, "Archive SFTP remote root").Replace('\\', '/').TrimEnd('/');
        return string.IsNullOrWhiteSpace(normalized) ? "/" : normalized;
    }

    private static string Require(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"{name} is required.");
        return value.Trim();
    }

    private static string MakeSafePathSegment(string? value)
    {
        var segment = string.IsNullOrWhiteSpace(value) ? "archive" : value.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars())
            segment = segment.Replace(invalid, '_');
        return segment.Replace('/', '_').Replace('\\', '_');
    }
}

public sealed class ArchiveBrowserViewModel
{
    public ObservableCollection<ArchiveFolderItem> Archives { get; } = new();
}
