using Microsoft.Extensions.Logging.Abstractions;

namespace pizzad.Tests;

public sealed class CredentialStoreTests
{
    [Fact]
    public void ApplyAlertSecrets_StoresNewPasswordAndRedactsClientView()
    {
        using var temp = new TempCredentialStore();
        var incoming = new AlertConfig { EmailEnabled = true, EmailUser = "operator@example.com", EmailPassword = "app-password" };

        var saved = temp.Store.ApplyAlertSecrets(incoming, temp.Config.Alerts, persistSecret: true);
        temp.Config.Alerts = saved;

        Assert.Equal(CredentialStore.StoredSecretMarker, saved.EmailPassword);
        Assert.Equal("app-password", temp.Store.ResolveAlertEmailPassword());
        Assert.Equal(string.Empty, temp.Store.SanitizeAlertsForClient(saved).EmailPassword);
        Assert.Equal(string.Empty, temp.Store.SanitizeAlertsForExport(saved).EmailPassword);
    }

    [Fact]
    public void ApplyAlertSecrets_MigratesLegacyRawPasswordWhenBlankClientPayloadIsSaved()
    {
        using var temp = new TempCredentialStore();
        temp.Config.Alerts.EmailPassword = "legacy-password";
        var incoming = new AlertConfig { EmailEnabled = true, EmailUser = "operator@example.com", EmailPassword = string.Empty };

        var saved = temp.Store.ApplyAlertSecrets(incoming, temp.Config.Alerts, persistSecret: true);
        temp.Config.Alerts = saved;

        Assert.Equal(CredentialStore.StoredSecretMarker, saved.EmailPassword);
        Assert.Equal("legacy-password", temp.Store.ResolveAlertEmailPassword());
    }

    private sealed class TempCredentialStore : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "pizzawave-credentials-test-" + Guid.NewGuid().ToString("N"));

        public TempCredentialStore()
        {
            Directory.CreateDirectory(_root);
            Config = new EngineConfig
            {
                Storage = new StorageConfig
                {
                    AppDataRoot = Path.Combine(_root, "appdata"),
                    AudioRoot = Path.Combine(_root, "audio"),
                    DatabasePath = Path.Combine(_root, "pizzad.db")
                }
            };
            Config.ApplyDefaults();
            Store = new CredentialStore(Config, NullLogger<CredentialStore>.Instance);
        }

        public EngineConfig Config { get; }
        public CredentialStore Store { get; }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }
    }
}
