using Xunit;

namespace MatterHelm.Tests;

public sealed class BuildScriptTests
{
    [Fact]
    public void ThirdPartyNoticesRequireAllPublishedRuntimePacksAndWindowsSdkLicenseUrl()
    {
        string script = File.ReadAllText(FindRepoFile("build.ps1"));

        Assert.Contains("Microsoft.NETCore.App.Runtime.win-x64", script, StringComparison.Ordinal);
        Assert.Contains("Microsoft.WindowsDesktop.App.Runtime.win-x64", script, StringComparison.Ordinal);
        Assert.Contains("Microsoft.Windows.SDK.NET.Ref", script, StringComparison.Ordinal);
        Assert.Contains("https://aka.ms/WinSDKLicenseURL", script, StringComparison.Ordinal);
        Assert.Contains("referenced runtime pack", script, StringComparison.Ordinal);
        Assert.DoesNotContain("$installedNotices", script, StringComparison.Ordinal);
    }

    private static string FindRepoFile(params string[] relativeParts)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine([directory.FullName, .. relativeParts]);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Could not locate repository file {Path.Combine(relativeParts)}.");
    }
}
