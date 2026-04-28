using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace pizzapi;

public sealed class OsSecretStore
{
    private const string ServiceName = "pizzawave.pizzapi.archive-sftp";
    private readonly string _dpapiRoot;

    public OsSecretStore(string appDataRoot)
    {
        _dpapiRoot = Path.Combine(appDataRoot, "secrets");
    }

    public void StoreArchivePassword(string keyId, string secret)
    {
        StoreSecret($"archive-password:{keyId}", secret);
    }

    public string? LookupArchivePassword(string keyId)
    {
        return LookupSecret($"archive-password:{keyId}");
    }

    public void StoreArchivePrivateKeyPassphrase(string keyId, string secret)
    {
        StoreSecret($"archive-privatekey-passphrase:{keyId}", secret);
    }

    public string? LookupArchivePrivateKeyPassphrase(string keyId)
    {
        return LookupSecret($"archive-privatekey-passphrase:{keyId}");
    }

    private void StoreSecret(string account, string secret)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            StoreWithDpapi(account, secret);
            return;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            StoreWithMacKeychain(account, secret);
            return;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            StoreWithSecretTool(account, secret);
            return;
        }

        throw new PlatformNotSupportedException("Unsupported OS for secure secret storage.");
    }

    private string? LookupSecret(string account)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return LookupWithDpapi(account);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return LookupWithMacKeychain(account);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return LookupWithSecretTool(account);

        throw new PlatformNotSupportedException("Unsupported OS for secure secret storage.");
    }

    [SupportedOSPlatform("windows")]
    private void StoreWithDpapi(string account, string secret)
    {
        Directory.CreateDirectory(_dpapiRoot);
        var plain = Encoding.UTF8.GetBytes(secret);
        var protectedBytes = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(GetDpapiPath(account), protectedBytes);
    }

    [SupportedOSPlatform("windows")]
    private string? LookupWithDpapi(string account)
    {
        var path = GetDpapiPath(account);
        if (!File.Exists(path))
            return null;
        var encrypted = File.ReadAllBytes(path);
        var plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plain);
    }

    private string GetDpapiPath(string account)
    {
        var safeName = account.Replace(':', '_');
        return Path.Combine(_dpapiRoot, $"{safeName}.bin");
    }

    private static void StoreWithMacKeychain(string account, string secret)
    {
        // -U updates existing item.
        RunProcessOrThrow(
            "security",
            $"add-generic-password -a \"{EscapeArg(account)}\" -s \"{EscapeArg(ServiceName)}\" -w \"{EscapeArg(secret)}\" -U");
    }

    private static string? LookupWithMacKeychain(string account)
    {
        var result = RunProcess(
            "security",
            $"find-generic-password -a \"{EscapeArg(account)}\" -s \"{EscapeArg(ServiceName)}\" -w");
        if (result.ExitCode != 0)
            return null;
        return result.StdOut.Trim();
    }

    private static void StoreWithSecretTool(string account, string secret)
    {
        var result = RunProcess(
            "secret-tool",
            $"store --label=\"PizzaWave SFTP Secret\" service \"{EscapeArg(ServiceName)}\" account \"{EscapeArg(account)}\"",
            stdIn: secret + Environment.NewLine);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"secret-tool store failed: {result.StdErr.Trim()}");
    }

    private static string? LookupWithSecretTool(string account)
    {
        var result = RunProcess(
            "secret-tool",
            $"lookup service \"{EscapeArg(ServiceName)}\" account \"{EscapeArg(account)}\"");
        if (result.ExitCode != 0)
            return null;
        var value = result.StdOut.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static void RunProcessOrThrow(string fileName, string args)
    {
        var result = RunProcess(fileName, args);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"{fileName} failed: {result.StdErr.Trim()}");
    }

    private static (int ExitCode, string StdOut, string StdErr) RunProcess(
        string fileName,
        string args,
        string? stdIn = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = args,
            RedirectStandardInput = stdIn != null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            using var process = new Process { StartInfo = startInfo };
            process.Start();
            if (stdIn != null)
            {
                process.StandardInput.Write(stdIn);
                process.StandardInput.Close();
            }
            var stdOut = process.StandardOutput.ReadToEnd();
            var stdErr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, stdOut, stdErr);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to execute {fileName}: {ex.Message}", ex);
        }
    }

    private static string EscapeArg(string value)
    {
        return value.Replace("\"", "\\\"");
    }
}
