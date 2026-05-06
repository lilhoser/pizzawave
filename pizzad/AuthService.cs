using System.Security.Cryptography;

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

        Directory.CreateDirectory(Path.GetDirectoryName(_config.Auth.TokenFile) ?? ".");
        if (!File.Exists(_config.Auth.TokenFile))
        {
            _token = GenerateToken();
            File.WriteAllText(_config.Auth.TokenFile, _token);
            TrySetOwnerOnlyPermissions(_config.Auth.TokenFile);
            _logger.LogWarning("Generated pizzad auth token at {Path}", _config.Auth.TokenFile);
        }
        else
        {
            _token = File.ReadAllText(_config.Auth.TokenFile).Trim();
        }
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
        Directory.CreateDirectory(Path.GetDirectoryName(_config.Auth.TokenFile) ?? ".");
        File.WriteAllText(_config.Auth.TokenFile, _token);
        TrySetOwnerOnlyPermissions(_config.Auth.TokenFile);
        return _config.Auth.TokenFile;
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

    private static void TrySetOwnerOnlyPermissions(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            System.Diagnostics.Process.Start("chmod", $"600 \"{path}\"")?.WaitForExit(2000);
        }
        catch
        {
            // Best effort. Install scripts also set permissions.
        }
    }
}
