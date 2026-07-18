using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace pizzad;

public static class EncryptedBackupArchive
{
    private static readonly byte[] MagicV1 = Encoding.ASCII.GetBytes("PWBAK001");
    private static readonly byte[] MagicV2 = Encoding.ASCII.GetBytes("PWBAK002");
    private const int SaltBytes = 16;
    private const int NoncePrefixBytes = 8;
    private const int TagBytes = 16;
    private const int KeyBytes = 32;
    private const int ChunkBytes = 1024 * 1024;
    private const int KdfIterations = 600_000;
    private const int HeaderBytes = 8 + SaltBytes + NoncePrefixBytes + sizeof(int) + sizeof(int) + sizeof(long);

    public static bool HasEncryptedExtension(string path) =>
        Path.GetExtension(path).Equals(".pwbak", StringComparison.OrdinalIgnoreCase);

    public static bool HasEncryptedHeader(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length < HeaderBytes)
            return false;
        Span<byte> actual = stackalloc byte[MagicV2.Length];
        using var stream = File.OpenRead(path);
        return stream.Read(actual) == MagicV2.Length && (actual.SequenceEqual(MagicV1) || actual.SequenceEqual(MagicV2));
    }

    public static async Task EncryptFileAsync(string sourcePath, string destinationPath, string passphrase, CancellationToken ct)
    {
        ValidatePassphrase(passphrase);
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var noncePrefix = RandomNumberGenerator.GetBytes(NoncePrefixBytes);
        var plaintextLength = new FileInfo(sourcePath).Length;
        var header = BuildHeader(MagicV2, salt, noncePrefix, KdfIterations, ChunkBytes, plaintextLength);
        var key = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, KdfIterations, HashAlgorithmName.SHA256, KeyBytes);

        try
        {
            await using var input = File.Open(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            await using var output = File.Create(destinationPath);
            await output.WriteAsync(header, ct);
            using var cipher = new ChaCha20Poly1305(key);
            var plaintext = new byte[ChunkBytes];
            var ciphertext = new byte[ChunkBytes];
            var tag = new byte[TagBytes];
            uint counter = 0;

            while (true)
            {
                var read = await ReadChunkAsync(input, plaintext, ct);
                if (read == 0)
                    break;
                var nonce = BuildNonce(noncePrefix, counter);
                var aad = BuildAssociatedData(header, counter, read);
                cipher.Encrypt(nonce, plaintext.AsSpan(0, read), ciphertext.AsSpan(0, read), tag, aad);
                await output.WriteAsync(ciphertext.AsMemory(0, read), ct);
                await output.WriteAsync(tag, ct);
                counter++;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public static async Task DecryptFileAsync(string sourcePath, string destinationPath, string passphrase, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(passphrase))
            throw new InvalidOperationException("The backup passphrase is required.");

        await using var input = File.Open(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var header = new byte[HeaderBytes];
        await ReadExactlyAsync(input, header, ct);
        var parsed = ParseHeader(header);
        var key = Rfc2898DeriveBytes.Pbkdf2(passphrase, parsed.Salt, parsed.Iterations, HashAlgorithmName.SHA256, KeyBytes);

        try
        {
            await using var output = File.Create(destinationPath);
            using var aes = parsed.Version == 1 ? new AesGcm(key, TagBytes) : null;
            using var chacha = parsed.Version == 2 ? new ChaCha20Poly1305(key) : null;
            var ciphertext = new byte[parsed.ChunkSize];
            var plaintext = new byte[parsed.ChunkSize];
            var tag = new byte[TagBytes];
            long remaining = parsed.PlaintextLength;
            uint counter = 0;

            while (remaining > 0)
            {
                var count = (int)Math.Min(parsed.ChunkSize, remaining);
                await ReadExactlyAsync(input, ciphertext.AsMemory(0, count), ct);
                await ReadExactlyAsync(input, tag, ct);
                var nonce = BuildNonce(parsed.NoncePrefix, counter);
                var aad = BuildAssociatedData(header, counter, count);
                try
                {
                    if (chacha != null)
                        chacha.Decrypt(nonce, ciphertext.AsSpan(0, count), tag, plaintext.AsSpan(0, count), aad);
                    else
                        aes!.Decrypt(nonce, ciphertext.AsSpan(0, count), tag, plaintext.AsSpan(0, count), aad);
                }
                catch (CryptographicException ex)
                {
                    throw new InvalidOperationException($"The backup could not be unlocked at encrypted chunk {counter:N0}. Check the passphrase and archive integrity.", ex);
                }
                await output.WriteAsync(plaintext.AsMemory(0, count), ct);
                remaining -= count;
                counter++;
            }

            if (input.Position != input.Length)
                throw new InvalidOperationException("The encrypted backup contains unexpected trailing data.");
        }
        catch
        {
            try { File.Delete(destinationPath); } catch { }
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public static void ValidatePassphrase(string? passphrase, string? confirmation = null)
    {
        if (string.IsNullOrEmpty(passphrase) || passphrase.Length < 12)
            throw new InvalidOperationException("Backup passphrase must be at least 12 characters.");
        if (confirmation != null && !string.Equals(passphrase, confirmation, StringComparison.Ordinal))
            throw new InvalidOperationException("Backup passphrase confirmation does not match.");
    }

    private static byte[] BuildHeader(byte[] magic, byte[] salt, byte[] noncePrefix, int iterations, int chunkSize, long plaintextLength)
    {
        var header = new byte[HeaderBytes];
        magic.CopyTo(header, 0);
        salt.CopyTo(header, magic.Length);
        noncePrefix.CopyTo(header, magic.Length + SaltBytes);
        var offset = magic.Length + SaltBytes + NoncePrefixBytes;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(offset, sizeof(int)), iterations);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(offset + sizeof(int), sizeof(int)), chunkSize);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(offset + sizeof(int) * 2, sizeof(long)), plaintextLength);
        return header;
    }

    private static ParsedHeader ParseHeader(byte[] header)
    {
        var version = header.AsSpan(0, MagicV2.Length).SequenceEqual(MagicV2)
            ? 2
            : header.AsSpan(0, MagicV1.Length).SequenceEqual(MagicV1) ? 1 : 0;
        if (version == 0)
            throw new InvalidOperationException("Backup is not a supported encrypted PizzaWave archive.");
        var salt = header.AsSpan(MagicV2.Length, SaltBytes).ToArray();
        var noncePrefix = header.AsSpan(MagicV2.Length + SaltBytes, NoncePrefixBytes).ToArray();
        var offset = MagicV2.Length + SaltBytes + NoncePrefixBytes;
        var iterations = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(offset, sizeof(int)));
        var chunkSize = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(offset + sizeof(int), sizeof(int)));
        var plaintextLength = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(offset + sizeof(int) * 2, sizeof(long)));
        if (iterations < 100_000 || iterations > 10_000_000 || chunkSize < 64 * 1024 || chunkSize > 16 * 1024 * 1024 || plaintextLength < 0)
            throw new InvalidOperationException("Encrypted backup header is invalid.");
        return new ParsedHeader(version, salt, noncePrefix, iterations, chunkSize, plaintextLength);
    }

    private static byte[] BuildNonce(byte[] prefix, uint counter)
    {
        var nonce = new byte[12];
        prefix.CopyTo(nonce, 0);
        BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(8), counter);
        return nonce;
    }

    private static byte[] BuildAssociatedData(byte[] header, uint counter, int count)
    {
        var aad = new byte[header.Length + sizeof(uint) + sizeof(int)];
        header.CopyTo(aad, 0);
        BinaryPrimitives.WriteUInt32BigEndian(aad.AsSpan(header.Length, sizeof(uint)), counter);
        BinaryPrimitives.WriteInt32BigEndian(aad.AsSpan(header.Length + sizeof(uint), sizeof(int)), count);
        return aad;
    }

    private static async Task<int> ReadChunkAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), ct);
            if (read == 0)
                break;
            total += read;
        }
        return total;
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], ct);
            if (read == 0)
                throw new InvalidOperationException("Encrypted backup ended unexpectedly.");
            total += read;
        }
    }

    private sealed record ParsedHeader(int Version, byte[] Salt, byte[] NoncePrefix, int Iterations, int ChunkSize, long PlaintextLength);
}
