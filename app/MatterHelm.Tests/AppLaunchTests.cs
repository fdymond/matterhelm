using MatterHelm.Actions;
using Xunit;

namespace MatterHelm.Tests;

/// <summary>
/// Tests for <see cref="AppLaunch"/>: the documented argument-splitting rules
/// (the pure logic behind every <c>launch</c> custom action) and the
/// missing-executable failure path. Successful real launches are covered by
/// the <c>--demo-wired</c> acceptance demo, not unit tests.
/// </summary>
public static class AppLaunchTests
{
    public sealed class SplitArgs
    {
        [Fact]
        public void EmptyStringYieldsNoArguments()
        {
            Assert.Empty(AppLaunch.SplitArgs(""));
        }

        [Fact]
        public void WhitespaceOnlyYieldsNoArguments()
        {
            Assert.Empty(AppLaunch.SplitArgs("   \t  "));
        }

        [Fact]
        public void SplitsOnRunsOfSpacesAndTabs()
        {
            Assert.Equal(["-a", "-b", "c"], AppLaunch.SplitArgs("-a  -b\t c"));
        }

        [Fact]
        public void DoubleQuotedSegmentsKeepTheirWhitespace()
        {
            Assert.Equal(["-fs", @"C:\My Movies", "next"], AppLaunch.SplitArgs("-fs \"C:\\My Movies\" next"));
        }

        [Fact]
        public void QuotedSegmentGluesToAdjacentUnquotedText()
        {
            Assert.Equal(["--path=C:\\My Movies"], AppLaunch.SplitArgs("--path=\"C:\\My Movies\""));
        }

        [Fact]
        public void DoubledQuoteInsideAQuotedSegmentEmitsOneLiteralQuote()
        {
            Assert.Equal(["say \"hi\" now"], AppLaunch.SplitArgs("\"say \"\"hi\"\" now\""));
        }

        [Fact]
        public void BackslashesAreAlwaysLiteralWindowsPathFriendly()
        {
            // Deliberately NOT the MSVCRT rules: a trailing backslash before a
            // closing quote is a path character, never an escape.
            Assert.Equal([@"C:\dir\", "-x"], AppLaunch.SplitArgs("\"C:\\dir\\\" -x"));
        }

        [Fact]
        public void UnterminatedQuoteExtendsToTheEndOfTheString()
        {
            Assert.Equal(["-a", "rest of line"], AppLaunch.SplitArgs("-a \"rest of line"));
        }

        [Fact]
        public void BareDoubleQuotePairYieldsOneEmptyArgument()
        {
            Assert.Equal([""], AppLaunch.SplitArgs("\"\""));
        }
    }

    public sealed class Start
    {
        [Fact]
        public void MissingExecutableFailsWithoutThrowing()
        {
            string missing = Path.Combine(
                Path.GetTempPath(), "MatterHelmTests", $"{Guid.NewGuid():N}-no-such.exe");

            Assert.False(AppLaunch.Start(new LaunchRequest(missing, "")));
        }
    }

    /// <summary>S9-5: Store (MSIX) package paths are detected and redirected to the per-user execution alias — CreateProcess denies the package path by design.</summary>
    public sealed class PackagedAppPaths
    {
        [Theory]
        [InlineData(@"C:\Program Files\WindowsApps\SpotifyAB.SpotifyMusic_1.296.518.0_x64__zpdnekdrzrea0\Spotify.exe")]
        [InlineData(@"c:\program files\windowsapps\Some.App_1.0_x64__abc\App.exe")] // case-insensitive
        public void PackageStorePathsAreDetected(string path)
        {
            Assert.True(AppLaunch.IsPackagedAppPath(path));
        }

        [Theory]
        [InlineData(@"C:\Program Files\VideoLAN\VLC\vlc.exe")] // ordinary Program Files
        [InlineData(@"C:\Users\x\AppData\Local\Microsoft\WindowsApps\Spotify.exe")] // the alias itself
        [InlineData(@"D:\Tools\WindowsApps\thing.exe")] // not under Program Files
        public void OrdinaryPathsAreNot(string path)
        {
            Assert.False(AppLaunch.IsPackagedAppPath(path));
        }

        [Fact]
        public void ExecutionAliasKeepsTheExeNameUnderTheUserWindowsAppsDir()
        {
            string alias = AppLaunch.ExecutionAliasFor(
                @"C:\Program Files\WindowsApps\SpotifyAB.SpotifyMusic_1.296.518.0_x64__zpdnekdrzrea0\Spotify.exe");

            Assert.Equal(
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Microsoft",
                    "WindowsApps",
                    "Spotify.exe"),
                alias);
        }

        [Fact]
        public void PackagedPathWithoutAnAliasFailsWithoutThrowing()
        {
            // The package dir must "exist" for Start to reach the redirect;
            // fabricate one under temp that matches the detection substring.
            string dir = Path.Combine(
                Path.GetTempPath(), "MatterHelmTests", @$"{Guid.NewGuid():N}\Program Files\WindowsApps\Fake.App_1.0_x64__abc");
            Directory.CreateDirectory(dir);
            string exe = Path.Combine(dir, $"{Guid.NewGuid():N}-no-alias.exe");
            File.WriteAllText(exe, "");
            try
            {
                // No execution alias exists for the random exe name → clean failure.
                Assert.False(AppLaunch.Start(new LaunchRequest(exe, "")));
            }
            finally
            {
                File.Delete(exe);
            }
        }
    }
}
