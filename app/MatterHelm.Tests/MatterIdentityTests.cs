using Xunit;

namespace MatterHelm.Tests;

/// <summary>
/// S10-4 commissioning identity: the seed decision (<see cref="MatterIdentity"/>)
/// and hex/decimal id parsing (<see cref="MatterIds"/>). The migration
/// guarantee — an install that already has fabric storage keeps the legacy
/// seed, so upgrading never silently unpairs it from Google Home — is the
/// reason this type exists, and is pinned first.
/// </summary>
public sealed class MatterIdentityTests
{
    [Fact]
    public void ExistingFabricPinsTheLegacySeedSoAnUpgradeNeverRepairs()
    {
        string seed = MatterIdentity.Resolve(
            configuredSeed: null, hasExistingFabric: true, mintSeed: () => "must-not-be-used");

        Assert.Equal("htpc-matter-bridge", seed);
        Assert.Equal(MatterIdentity.LegacySeed, seed);
    }

    [Fact]
    public void FreshInstallMintsItsOwnSeed()
    {
        string seed = MatterIdentity.Resolve(
            configuredSeed: null, hasExistingFabric: false, mintSeed: () => "minted-seed");

        Assert.Equal("minted-seed", seed);
    }

    [Theory]
    [InlineData("already-set")]
    [InlineData("  padded  ")]
    public void AConfiguredSeedAlwaysWinsRegardlessOfFabricState(string configured)
    {
        foreach (bool fabric in (bool[])[true, false])
        {
            Assert.Equal(
                configured.Trim(),
                MatterIdentity.Resolve(configured, fabric, () => "minted"));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void BlankConfiguredSeedIsTreatedAsUnresolved(string? blank)
    {
        Assert.Equal("minted", MatterIdentity.Resolve(blank, hasExistingFabric: false, () => "minted"));
    }

    [Fact]
    public void MintedSeedsAreUniquePerCall()
    {
        Assert.NotEqual(MatterIdentity.MintSeed(), MatterIdentity.MintSeed());
    }

    [Theory]
    [InlineData("0xFFF1", 0xFFF1)]
    [InlineData("0xfff1", 0xFFF1)]
    [InlineData("0X8000", 0x8000)]
    [InlineData("65521", 65521)]
    [InlineData("  0x8003  ", 0x8003)]
    public void MatterIdsParsesHexAndDecimal(string text, int expected)
    {
        Assert.True(MatterIds.TryParse(text, out int value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("0x")]           // prefix only
    [InlineData("nope")]
    [InlineData("0")]            // below range
    [InlineData("65536")]        // above range
    [InlineData("0x10000")]      // above range in hex
    public void MatterIdsRejectsUnparsableOrOutOfRange(string? text)
    {
        Assert.False(MatterIds.TryParse(text, out int value));
        Assert.Equal(0, value);
    }

    [Fact]
    public void MatterIdsFormatsAsFourDigitHex()
    {
        Assert.Equal("0xFFF1", MatterIds.Format(0xFFF1));
        Assert.Equal("0x8000", MatterIds.Format(0x8000));
    }

    [Theory]
    [InlineData(0xFFF1, 0x8000, true)]
    [InlineData(0xFFF4, 0x801F, true)]
    [InlineData(0xFFF5, 0x8000, false)]  // VID outside the test range
    [InlineData(0xFFF1, 0x8020, false)]  // PID outside the test range
    public void TestRangeDetectionMatchesAdr002(int vendorId, int productId, bool expected)
    {
        Assert.Equal(expected, MatterIds.IsTestRange(vendorId, productId));
    }
}
