using Xunit;

namespace MatterHelm.Tests;

/// <summary>Regression guards preventing tests from using the maintainer's live MatterHelm profile.</summary>
[Collection(DiagnosticsTests.StaticLogState.Name)]
public sealed class StateIsolationTests
{
    [Fact]
    public void TestAssemblyStateIsOutsideApplicationData()
    {
        string applicationData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        Assert.False(IsUnder(AppPaths.Root, applicationData), $"AppPaths.Root escaped into the live profile: {AppPaths.Root}");
        Assert.False(
            IsUnder(Log.LogDirectory, applicationData),
            $"Log.LogDirectory escaped into the live profile: {Log.LogDirectory}");
    }

    [Fact]
    public void DefaultAppPathsFollowThePerProcessTestRoot()
    {
        Assert.Equal(TestEnvironment.IsolatedRoot, AppPaths.Root);
        Assert.Equal(Path.Combine(TestEnvironment.IsolatedRoot, "config.json"), Config.DefaultPath);
        Assert.Equal(Path.Combine(TestEnvironment.IsolatedRoot, "matter"), Ui.SettingsViewModel.StorageDirDisplay);
        Assert.Equal(Path.Combine(TestEnvironment.IsolatedRoot, "logs"), Log.LogDirectory);
    }

    private static bool IsUnder(string candidate, string parent)
    {
        string relative = Path.GetRelativePath(Path.GetFullPath(parent), Path.GetFullPath(candidate));
        return !Path.IsPathRooted(relative)
            && !relative.Equals("..", StringComparison.Ordinal)
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }
}
