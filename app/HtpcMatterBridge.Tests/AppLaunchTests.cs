using HtpcMatterBridge.Actions;
using Xunit;

namespace HtpcMatterBridge.Tests;

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
                Path.GetTempPath(), "HtpcMatterBridgeTests", $"{Guid.NewGuid():N}-no-such.exe");

            Assert.False(AppLaunch.Start(new LaunchRequest(missing, "")));
        }
    }
}
