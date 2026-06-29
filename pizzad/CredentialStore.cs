using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace pizzad;

public sealed class CredentialStore
{
    public const string StoredSecretMarker = "__pizzawave_secret__:alerts.smtp.password";
    private const string AlertSmtpPasswordName = "alerts.smtp.password";
    private readonly EngineConfig _config;
    private readonly ILogger<CredentialStore> _logger;

    public CredentialStore(EngineConfig config, ILogger<CredentialStore> logger)
    {
        _config = config;
        _logger = logger;
    }

    public bool IsStoredSecretMarker(string? value) =>
        string.Equals(value?.Trim(), StoredSecretMarker, StringComparison.Ordinal);

    public string SanitizeForClient(string? value) => string.Empty;

    public string ResolveAlertEmailPassword()
    {
        var configured = _config.Alerts.EmailPassword ?? string.Empty;
        if (!IsStoredSecretMarker(configured))
            return configured;
        return ReadSecret(AlertSmtpPasswordName) ?? string.Empty;
    }

    public bool HasAlertEmailPassword() => !string.IsNullOrWhiteSpace(ResolveAlertEmailPassword());

    public void StoreAlertEmailPassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password))
            return;
        WriteSecret(AlertSmtpPasswordName, password);
        _config.Alerts.EmailPassword = StoredSecretMarker;
    }

    public AlertConfig SanitizeAlertsForClient(AlertConfig alerts)
    {
        var clone = Clone(alerts);
        clone.EmailPassword = SanitizeForClient(alerts.EmailPassword);
        return clone;
    }

    public AlertConfig SanitizeAlertsForExport(AlertConfig alerts)
    {
        var clone = Clone(alerts);
        clone.EmailPassword = string.Empty;
        return clone;
    }

    public AlertConfig ApplyAlertSecrets(AlertConfig incoming, AlertConfig current, bool persistSecret)
    {
        var password = incoming.EmailPassword ?? string.Empty;
        if (IsStoredSecretMarker(password))
        {
            incoming.EmailPassword = IsStoredSecretMarker(current.EmailPassword) || HasAlertEmailPassword()
                ? StoredSecretMarker
                : string.Empty;
            return incoming;
        }

        if (!string.IsNullOrWhiteSpace(password))
        {
            if (persistSecret)
                WriteSecret(AlertSmtpPasswordName, password);
            incoming.EmailPassword = StoredSecretMarker;
            return incoming;
        }

        if (IsStoredSecretMarker(current.EmailPassword) || HasAlertEmailPassword())
        {
            if (persistSecret && !IsStoredSecretMarker(current.EmailPassword) && !string.IsNullOrWhiteSpace(current.EmailPassword))
                WriteSecret(AlertSmtpPasswordName, current.EmailPassword);
            incoming.EmailPassword = StoredSecretMarker;
        }
        return incoming;
    }

    private string? ReadSecret(string name)
    {
        var path = SecretPath(name);
        if (!File.Exists(path))
            return null;
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (OperatingSystem.IsWindows())
                bytes = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read PizzaWave credential {Name}", name);
            return null;
        }
    }

    private void WriteSecret(string name, string value)
    {
        var path = SecretPath(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        var bytes = Encoding.UTF8.GetBytes(value);
        if (OperatingSystem.IsWindows())
            bytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(path, bytes);
        TrySetOwnerOnlyPermissions(path);
    }

    private string SecretPath(string name)
    {
        var safe = string.Join('-', name.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        if (string.IsNullOrWhiteSpace(safe))
            safe = "secret";
        var extension = OperatingSystem.IsWindows() ? ".dpapi" : ".secret";
        return Path.Combine(_config.Storage.AppDataRoot, "credentials", safe + extension);
    }

    private static AlertConfig Clone(AlertConfig alerts) =>
        JsonSerializer.Deserialize<AlertConfig>(JsonSerializer.Serialize(alerts, EngineConfig.JsonOptions()), EngineConfig.JsonOptions()) ?? new AlertConfig();

    private static void TrySetOwnerOnlyPermissions(string path)
    {
        if (OperatingSystem.IsWindows())
            return;
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            // Best effort. The secret directory lives under appdata owned by pizzawave.
        }
    }
}
