using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace pizzad;

public static class EncryptedBackupArchive
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("PWBAK001");
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
        Span<byte> actual = stackalloc byte[Magic.Length];
        using var stream = File.OpenRead(path);
        return stream.Read(actual) == Magic.Length && actual.SequenceEqual(Magic);
    }

    public static Task EncryptFileAsync(string sourcePath, string destinationPath, string passphrase, CancellationToken ct)
    {
        ValidatePassphrase(passphrase);
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var noncePrefix = RandomNumberGenerator.GetBytes(NoncePrefixBytes);
        var plaintextLength = new FileInfo(sourcePath).Length;
        var header = BuildHeader(salt, noncePrefix, KdfIterations, ChunkBytes, plaintextLength);
        var key = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, KdfIterations, HashAlgorithmName.SHA256, KeyBytes);

        try
        {
            using var input = File.Open(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var output = File.Create(destinationPath);
            output.Write(header);
            using var aes = new AesGcm(key, TagBytes);
            var plaintext = new byte[ChunkBytes];
            var ciphertext = new byte[ChunkBytes];
            var tag = new byte[TagBytes];
            uint counter = 0;

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var read = ReadChunk(input, plaintext);
                if (read == 0)
                    break;
                var nonce = BuildNonce(noncePrefix, counter);
                var aad = BuildAssociatedData(header, counter, read);
                aes.Encrypt(nonce, plaintext.AsSpan(0, read), ciphertext.AsSpan(0, read), tag, aad);
                output.Write(ciphertext, 0, read);
                output.Write(tag);
                counter++;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
        return Task.CompletedTask;
    }

    public static Task DecryptFileAsync(string sourcePath, string destinationPath, string passphrase, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(passphrase))
            throw new InvalidOperationException("The backup passphrase is required.");

        using var input = File.Open(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var header = new byte[HeaderBytes];
        ReadExactly(input, header);
        var parsed = ParseHeader(header);
        var key = Rfc2898DeriveBytes.Pbkdf2(passphrase, parsed.Salt, parsed.Iterations, HashAlgorithmName.SHA256, KeyBytes);

        try
        {
            using var output = File.Create(destinationPath);
            using var aes = new AesGcm(key, TagBytes);
            var ciphertext = new byte[parsed.ChunkSize];
            var plaintext = new byte[parsed.ChunkSize];
            var tag = new byte[TagBytes];
            long remaining = parsed.PlaintextLength;
            uint counter = 0;

            while (remaining > 0)
            {
                ct.ThrowIfCancellationRequested();
                var count = (int)Math.Min(parsed.ChunkSize, remaining);
                ReadExactly(input, ciphertext.AsSpan(0, count));
                ReadExactly(input, tag);
                var nonce = BuildNonce(parsed.NoncePrefix, counter);
                var aad = BuildAssociatedData(header, counter, count);
                try
                {
                    aes.Decrypt(nonce, ciphertext.AsSpan(0, count), tag, plaintext.AsSpan(0, count), aad);
                }
                catch (CryptographicException ex)
                {
                    throw new InvalidOperationException($"The backup could not be unlocked at encrypted chunk {counter:N0}. Check the passphrase and archive integrity.", ex);
                }
                output.Write(plaintext, 0, count);
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
        return Task.CompletedTask;
    }

    public static void ValidatePassphrase(string? passphrase, string? confirmation = null)
    {
        if (string.IsNullOrEmpty(passphrase) || passphrase.Length < 12)
            throw new InvalidOperationException("Backup passphrase must be at least 12 characters.");
        if (confirmation != null && !string.Equals(passphrase, confirmation, StringComparison.Ordinal))
            throw new InvalidOperationException("Backup passphrase confirmation does not match.");
    }

    private static byte[] BuildHeader(byte[] salt, byte[] noncePrefix, int iterations, int chunkSize, long plaintextLength)
    {
        var header = new byte[HeaderBytes];
        Magic.CopyTo(header, 0);
        salt.CopyTo(header, Magic.Length);
        noncePrefix.CopyTo(header, Magic.Length + SaltBytes);
        var offset = Magic.Length + SaltBytes + NoncePrefixBytes;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(offset, sizeof(int)), iterations);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(offset + sizeof(int), sizeof(int)), chunkSize);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(offset + sizeof(int) * 2, sizeof(long)), plaintextLength);
        return header;
    }

    private static ParsedHeader ParseHeader(byte[] header)
    {
        if (!header.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new InvalidOperationException("Backup is not a supported encrypted PizzaWave archive.");
        var salt = header.AsSpan(Magic.Length, SaltBytes).ToArray();
        var noncePrefix = header.AsSpan(Magic.Length + SaltBytes, NoncePrefixBytes).ToArray();
        var offset = Magic.Length + SaltBytes + NoncePrefixBytes;
        var iterations = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(offset, sizeof(int)));
        var chunkSize = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(offset + sizeof(int), sizeof(int)));
        var plaintextLength = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(offset + sizeof(int) * 2, sizeof(long)));
        if (iterations < 100_000 || iterations > 10_000_000 || chunkSize < 64 * 1024 || chunkSize > 16 * 1024 * 1024 || plaintextLength < 0)
            throw new InvalidOperationException("Encrypted backup header is invalid.");
        return new ParsedHeader(salt, noncePrefix, iterations, chunkSize, plaintextLength);
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

    private static int ReadChunk(Stream stream, byte[] buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0)
                break;
            total += read;
        }
        return total;
    }

    private static void ReadExactly(Stream stream, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer[total..]);
            if (read == 0)
                throw new InvalidOperationException("Encrypted backup ended unexpectedly.");
            total += read;
        }
    }

    private sealed record ParsedHeader(byte[] Salt, byte[] NoncePrefix, int Iterations, int ChunkSize, long PlaintextLength);
}
