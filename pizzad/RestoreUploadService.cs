using System.Security.Cryptography;
using System.Text.Json;

namespace pizzad;

public sealed class RestoreUploadService
{
    private const int ChunkSize = 4 * 1024 * 1024;
    private static readonly TimeSpan UploadLifetime = TimeSpan.FromHours(24);
    private readonly EngineConfig _config;
    private readonly BackupRestoreService _backups;
    private readonly RecoveryOperationCoordinator _recovery;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public RestoreUploadService(EngineConfig config, BackupRestoreService backups, RecoveryOperationCoordinator? recovery = null)
    {
        _config = config;
        _backups = backups;
        _recovery = recovery ?? new RecoveryOperationCoordinator();
    }

    public async Task<RestoreUploadDto> CreateAsync(RestoreUploadCreateRequestDto request, CancellationToken ct)
    {
        if (request.Bytes <= 0)
            throw new InvalidOperationException("Backup size must be greater than zero.");
        if (!IsSha256(request.Sha256))
            throw new InvalidOperationException("A valid whole-file SHA-256 is required.");
        CleanupExpired();
        var id = Guid.NewGuid().ToString("N");
        var root = SessionRoot(id);
        Directory.CreateDirectory(Path.Combine(root, "chunks"));
        var metadata = new RestoreUploadMetadata(
            id,
            Path.GetFileName(string.IsNullOrWhiteSpace(request.FileName) ? "restore-upload" : request.FileName),
            request.Bytes,
            request.Sha256.ToLowerInvariant(),
            ChunkSize,
            checked((int)Math.Ceiling(request.Bytes / (double)ChunkSize)),
            DateTime.UtcNow,
            DateTime.UtcNow.Add(UploadLifetime));
        await SaveAsync(root, metadata, ct);
        return ToDto(metadata, root);
    }

    public RestoreUploadDto? Get(string id)
    {
        CleanupExpired();
        var root = ValidSessionRoot(id);
        if (root == null) return null;
        var metadata = Load(root);
        return metadata == null ? null : ToDto(metadata, root);
    }

    public async Task<RestoreUploadDto> PutChunkAsync(string id, int index, Stream source, string? expectedSha256, CancellationToken ct)
    {
        if (!IsSha256(expectedSha256))
            throw new InvalidOperationException("A valid chunk SHA-256 is required.");
        await _gate.WaitAsync(ct);
        try
        {
            var root = ValidSessionRoot(id) ?? throw new FileNotFoundException("Restore upload session was not found or has expired.");
            var metadata = Load(root) ?? throw new InvalidOperationException("Restore upload metadata is missing.");
            if (index < 0 || index >= metadata.ChunkCount)
                throw new InvalidOperationException("Restore upload chunk index is outside the expected range.");
            var expectedBytes = index == metadata.ChunkCount - 1
                ? metadata.Bytes - (long)index * metadata.ChunkSize
                : metadata.ChunkSize;
            var temp = Path.Combine(root, "chunks", $"{index:D8}.partial");
            var final = Path.Combine(root, "chunks", $"{index:D8}.chunk");
            try
            {
                await using (var output = File.Create(temp))
                    await source.CopyToAsync(output, ct);
                if (new FileInfo(temp).Length != expectedBytes)
                    throw new InvalidOperationException($"Chunk {index} has an unexpected size.");
                var actual = await Sha256Async(temp, ct);
                if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Chunk {index} checksum did not match.");
                File.Move(temp, final, overwrite: true);
            }
            finally
            {
                try { File.Delete(temp); } catch { }
            }
            return ToDto(metadata, root);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<BackupRestorePreviewDto> CompleteAsync(string id, string? passphrase, CancellationToken ct)
    {
        using var recoveryLease = _recovery.Acquire("restore validation and staging");
        await _gate.WaitAsync(ct);
        try
        {
            var root = ValidSessionRoot(id) ?? throw new FileNotFoundException("Restore upload session was not found or has expired.");
            var metadata = Load(root) ?? throw new InvalidOperationException("Restore upload metadata is missing.");
            var dto = ToDto(metadata, root);
            if (dto.ReceivedChunks.Count != metadata.ChunkCount)
                throw new InvalidOperationException($"Restore upload is incomplete: {dto.ReceivedChunks.Count} of {metadata.ChunkCount} chunks received.");
            var assembled = Path.Combine(root, metadata.FileName);
            await using (var output = File.Create(assembled))
            {
                for (var index = 0; index < metadata.ChunkCount; index++)
                {
                    await using var input = File.OpenRead(Path.Combine(root, "chunks", $"{index:D8}.chunk"));
                    await input.CopyToAsync(output, ct);
                }
            }
            if (!string.Equals(await Sha256Async(assembled, ct), metadata.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Assembled restore upload checksum did not match the selected file.");
            BackupRestorePreviewDto preview;
            await using (var stream = File.OpenRead(assembled))
                preview = await _backups.StageRestoreAsync(stream, metadata.FileName, passphrase, ct);
            Directory.Delete(root, recursive: true);
            return preview;
        }
        finally
        {
            _gate.Release();
        }
    }

    public bool Cancel(string id)
    {
        var root = ValidSessionRoot(id);
        if (root == null) return false;
        Directory.Delete(root, recursive: true);
        return true;
    }

    private RestoreUploadDto ToDto(RestoreUploadMetadata metadata, string root)
    {
        var received = Directory.Exists(Path.Combine(root, "chunks"))
            ? Directory.EnumerateFiles(Path.Combine(root, "chunks"), "*.chunk").Select(path => int.Parse(Path.GetFileNameWithoutExtension(path))).Order().ToList()
            : [];
        return new RestoreUploadDto(metadata.Id, metadata.FileName, metadata.Bytes, metadata.Sha256, metadata.ChunkSize, metadata.ChunkCount, received, metadata.CreatedUtc, metadata.ExpiresUtc);
    }

    private void CleanupExpired()
    {
        var root = UploadRoot();
        if (!Directory.Exists(root)) return;
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            try
            {
                var metadata = Load(directory);
                if (metadata == null || metadata.ExpiresUtc <= DateTime.UtcNow)
                    Directory.Delete(directory, recursive: true);
            }
            catch { }
        }
    }

    private string? ValidSessionRoot(string id)
    {
        if (id.Length != 32 || id.Any(c => !Uri.IsHexDigit(c))) return null;
        var root = SessionRoot(id);
        return Directory.Exists(root) ? root : null;
    }

    private string UploadRoot() => Path.Combine(_config.Storage.AppDataRoot, "restore-uploads");
    private string SessionRoot(string id) => Path.Combine(UploadRoot(), id);
    private static RestoreUploadMetadata? Load(string root)
    {
        var path = Path.Combine(root, "upload.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<RestoreUploadMetadata>(File.ReadAllText(path), EngineConfig.JsonOptions()) : null;
    }
    private static Task SaveAsync(string root, RestoreUploadMetadata value, CancellationToken ct) =>
        File.WriteAllTextAsync(Path.Combine(root, "upload.json"), JsonSerializer.Serialize(value, EngineConfig.JsonOptions()) + Environment.NewLine, ct);
    private static bool IsSha256(string? value) => value?.Length == 64 && value.All(Uri.IsHexDigit);
    private static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
    }

    private sealed record RestoreUploadMetadata(string Id, string FileName, long Bytes, string Sha256, int ChunkSize, int ChunkCount, DateTime CreatedUtc, DateTime ExpiresUtc);
}

public sealed record RestoreUploadCreateRequestDto(string FileName, long Bytes, string Sha256);
public sealed record RestoreUploadCompleteRequestDto(string? Passphrase);
public sealed record RestoreUploadDto(string Id, string FileName, long Bytes, string Sha256, int ChunkSize, int ChunkCount, IReadOnlyList<int> ReceivedChunks, DateTime CreatedUtc, DateTime ExpiresUtc);
