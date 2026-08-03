using System.Security.Cryptography;

namespace pizzad.Tests;

public sealed class NativeDependencyBuildContractTests
{
    [Fact]
    public void LockedCommitsAndPatchHashesAreCompleteAndMatchFiles()
    {
        var root = FindRepositoryRoot();
        var scripts = Path.Combine(root, "scripts");
        var values = File.ReadAllLines(Path.Combine(scripts, "native-dependencies.lock"))
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Select(line => line.Split('=', 2))
            .ToDictionary(
                parts => parts[0],
                parts => parts[1].Trim().Trim('"'),
                StringComparer.Ordinal);

        Assert.Matches("^[0-9a-f]{40}$", values["TRUNK_RECORDER_COMMIT"]);
        Assert.Matches("^[0-9a-f]{40}$", values["CALLSTREAM_COMMIT"]);
        Assert.Equal("6a46546bde7728fb870ebcdc7ed64979b42247ea", values["CALLSTREAM_COMMIT"]);
        AssertPatchHash(scripts, values["TRUNK_RECORDER_PATCH"], values["TRUNK_RECORDER_PATCH_SHA256"]);
        Assert.DoesNotContain("CALLSTREAM_PATCH", values.Keys);
    }

    [Fact]
    public void SetupSourceBuildUsesThePrivilegedExplicitBuildAction()
    {
        var root = FindRepositoryRoot();
        var service = File.ReadAllText(Path.Combine(root, "pizzad", "SetupJobService.cs"));
        var admin = File.ReadAllText(Path.Combine(root, "scripts", "pizzawave_setup_admin.sh"));
        var setup = File.ReadAllText(Path.Combine(root, "scripts", "setup_trunk_recorder.sh"));

        Assert.Contains("RunAdminHelperAsync(jobId, \"build-tr-source\", ct)", service, StringComparison.Ordinal);
        Assert.Contains("build-tr-source)", admin, StringComparison.Ordinal);
        Assert.Contains("\"$candidate\" --build", admin, StringComparison.Ordinal);
        Assert.DoesNotContain("pull --ff-only", setup, StringComparison.Ordinal);
        Assert.Contains("prepare_trunk_recorder_source.sh", setup, StringComparison.Ordinal);
        Assert.Contains("ctest --output-on-failure", setup, StringComparison.Ordinal);
    }

    private static void AssertPatchHash(string scripts, string relativePath, string expected)
    {
        Assert.Matches("^[0-9a-f]{64}$", expected);
        var bytes = File.ReadAllBytes(Path.Combine(scripts, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        Assert.Equal(expected, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
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
