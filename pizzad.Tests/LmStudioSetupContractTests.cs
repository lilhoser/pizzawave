namespace pizzad.Tests;

public sealed class LmStudioSetupContractTests
{
    [Fact]
    public void SetupUiUsesBackendJobThatExecutesLmStudioSetupScript()
    {
        var root = FindRepositoryRoot();
        var app = File.ReadAllText(Path.Combine(root, "pizzad", "web", "src", "App.tsx"));
        var service = File.ReadAllText(Path.Combine(root, "pizzad", "SetupJobService.cs"));
        var project = File.ReadAllText(Path.Combine(root, "pizzad", "pizzad.csproj"));
        var deploy = File.ReadAllText(Path.Combine(root, "scripts", "deploy_pizzad_tar.ps1"));

        Assert.Contains("startSetupJob(\"lmstudio-prime\")", app, StringComparison.Ordinal);
        Assert.Contains("FindLmStudioScript()", service, StringComparison.Ordinal);
        Assert.Contains("RunCommandAsync(jobId, \"sudo\", $\"{script} --skip-model-load\", ct)", service, StringComparison.Ordinal);
        Assert.Contains("..\\scripts\\setup-lmstudio.sh", project, StringComparison.Ordinal);
        Assert.Contains("/opt/pizzawave/pizzad/scripts/setup-lmstudio.sh /usr/lib/pizzawave/scripts/setup-lmstudio.sh", deploy, StringComparison.Ordinal);
        Assert.Contains("setup-lmstudio.sh`t$lmStudioSetupSourceHash", deploy, StringComparison.Ordinal);
    }

    [Fact]
    public void LmStudioServiceRestartsAfterStartupFailureWithoutLocalChatPreload()
    {
        var root = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(root, "scripts", "setup-lmstudio.sh"))
            .ReplaceLineEndings("\n");

        var preloadBranch = script.IndexOf("if [[ \"$PRELOAD_MODEL\" == \"true\" ]]; then", StringComparison.Ordinal);
        Assert.True(preloadBranch >= 0, "Local chat preload branch was not found.");

        var preloadBranchEnd = script.IndexOf("\n  fi", preloadBranch, StringComparison.Ordinal);
        Assert.True(preloadBranchEnd > preloadBranch, "Local chat preload branch end was not found.");

        var restartPolicy = script.IndexOf("echo \"Restart=on-failure\"", StringComparison.Ordinal);
        Assert.True(restartPolicy > preloadBranchEnd, "LM Studio restart recovery must apply outside the local chat preload branch.");
        Assert.Contains("echo \"RestartSec=20s\"", script, StringComparison.Ordinal);
        Assert.Contains("ExecStartPost=+/usr/local/bin/pizzawave-load-local-embedding-model", script, StringComparison.Ordinal);
        Assert.Contains("['sudo', '-u', TARGET_USER, '-H', LMS, 'load'", script, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null &&
               !File.Exists(Path.Combine(directory.FullName, ".git")) &&
               !Directory.Exists(Path.Combine(directory.FullName, ".git")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
