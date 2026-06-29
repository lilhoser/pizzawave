using System.Security.Cryptography;
using System.Diagnostics;

namespace pizzad;

public sealed class AuthService
{
    private readonly EngineConfig _config;
    private readonly ILogger<AuthService> _logger;
    private string? _token;

    public AuthService(EngineConfig config, ILogger<AuthService> logger)
    {
        _config = config;
        _logger = logger;
    }

    public void Initialize()
    {
        if (!string.Equals(_config.Auth.Mode, "token", StringComparison.OrdinalIgnoreCase))
            return;

        EnsureToken();
    }

    public AuthInitDto GetAuthInit() => new(
        _config.Auth.Mode,
        _config.Auth.ReadRequiresAuth,
        _config.Auth.WriteRequiresAuth);

    public bool IsReadAllowed(HttpContext context) =>
        !_config.Auth.ReadRequiresAuth || IsAuthorized(context);

    public bool IsWriteAllowed(HttpContext context) =>
        !_config.Auth.WriteRequiresAuth || IsAuthorized(context);

    public string RegenerateToken()
    {
        _token = GenerateToken();
        WriteToken(_token);
        return _config.Auth.TokenFile;
    }

    public string EnsureToken()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_config.Auth.TokenFile) ?? ".");
        if (!File.Exists(_config.Auth.TokenFile))
        {
            _token = GenerateToken();
            WriteToken(_token);
            _logger.LogWarning("Generated pizzad auth token at {Path}", _config.Auth.TokenFile);
        }
        else
        {
            _token = File.ReadAllText(_config.Auth.TokenFile).Trim();
        }

        return _token ?? string.Empty;
    }

    public string? CurrentToken()
    {
        if (string.IsNullOrWhiteSpace(_token) && string.Equals(_config.Auth.Mode, "token", StringComparison.OrdinalIgnoreCase))
            EnsureToken();
        return _token;
    }

    private bool IsAuthorized(HttpContext context)
    {
        if (!string.Equals(_config.Auth.Mode, "token", StringComparison.OrdinalIgnoreCase))
            return true;

        var header = context.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var candidate = header[prefix.Length..].Trim();
        return !string.IsNullOrWhiteSpace(_token) &&
               CryptographicOperations.FixedTimeEquals(
                   System.Text.Encoding.UTF8.GetBytes(candidate),
                   System.Text.Encoding.UTF8.GetBytes(_token));
    }

    private static string GenerateToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private void WriteToken(string token)
    {
        if (OperatingSystem.IsWindows() || !_config.Auth.TokenFile.StartsWith("/etc/", StringComparison.Ordinal))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_config.Auth.TokenFile) ?? ".");
            File.WriteAllText(_config.Auth.TokenFile, token);
            TrySetOwnerOnlyPermissions(_config.Auth.TokenFile);
            return;
        }

        var stagingRoot = Path.Combine(_config.Storage.AppDataRoot, "protected-config");
        Directory.CreateDirectory(stagingRoot);
        var candidatePath = Path.Combine(stagingRoot, $"pizzad-token-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.token");
        File.WriteAllText(candidatePath, token + Environment.NewLine);
        try
        {
            var helper = FindAdminHelper();
            var psi = new ProcessStartInfo("sudo")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            psi.ArgumentList.Add(helper);
            psi.ArgumentList.Add("install-auth-token");
            psi.ArgumentList.Add(candidatePath);
            psi.ArgumentList.Add(_config.Auth.TokenFile);
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Unable to start protected token helper.");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"Protected token helper failed with exit code {process.ExitCode}: {(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr).Trim()}");
        }
        finally
        {
            try { File.Delete(candidatePath); } catch { }
        }
    }

    private static string FindAdminHelper()
    {
        var candidates = new[]
        {
            "/usr/lib/pizzawave/scripts/pizzawave_setup_admin.sh",
            "/opt/pizzawave/pizzad/scripts/pizzawave_setup_admin.sh",
            "/opt/pizzawave/scripts/pizzawave_setup_admin.sh",
            Path.Combine(AppContext.BaseDirectory, "scripts", "pizzawave_setup_admin.sh")
        };
        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("pizzawave_setup_admin.sh was not found; protected token writes are unavailable.");
    }

    private static void TrySetOwnerOnlyPermissions(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            System.Diagnostics.Process.Start("chmod", $"640 \"{path}\"")?.WaitForExit(2000);
        }
        catch
        {
            // Best effort. Install scripts also set permissions.
        }
    }
}
