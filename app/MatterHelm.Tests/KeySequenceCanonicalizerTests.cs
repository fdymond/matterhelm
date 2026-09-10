using MatterHelm.Ui;
using Xunit;

namespace MatterHelm.Tests;

public sealed class KeySequenceCanonicalizerTests
{
    [Fact]
    public void ValidSequenceUsesCanonicalModifierOrderAndCasing()
    {
        Assert.Equal("Ctrl+Shift+V", KeySequenceCanonicalizer.Canonicalize("shift + ctrl + v"));
    }

    [Fact]
    public void InvalidSequenceIsReturnedUnchangedForTheValidatorToReject()
    {
        Assert.Equal("Ctrl+NoSuchKey", KeySequenceCanonicalizer.Canonicalize("Ctrl+NoSuchKey"));
    }
}
