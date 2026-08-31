using MatterHelm.Ui;
using Xunit;

namespace MatterHelm.Tests;

public sealed class CustomCommandDialogTests
{
    [Theory]
    [InlineData(MediaKeyName.PlayPause)]
    [InlineData(MediaKeyName.Play)]
    [InlineData(MediaKeyName.Pause)]
    public void MediaEditorMappingRoundTripsFocusedFirstVerbs(MediaKeyName key)
    {
        int index = CustomCommandDialog.MediaKeyIndex(key);

        Assert.Equal(key, CustomCommandDialog.MediaKeyFromIndex(index));
    }

    [Theory]
    [InlineData(MouseTarget.BottomRight, 0, 0)]
    [InlineData(MouseTarget.Center, 0, 0)]
    [InlineData(MouseTarget.Custom, -1234, 5678)]
    public void SharedMouseTargetSelectionRoundTripsEveryTarget(MouseTarget target, int x, int y)
    {
        var original = new MouseMoveActionConfig
        {
            Target = target,
            X = target == MouseTarget.Custom ? x : null,
            Y = target == MouseTarget.Custom ? y : null,
        };

        MouseMoveActionConfig result = MouseTargetSelection
            .FromAction(original)
            .ToAction();

        Assert.Equal(original.Target, result.Target);
        Assert.Equal(original.X, result.X);
        Assert.Equal(original.Y, result.Y);
    }

    [Theory]
    [InlineData(MouseTarget.TopLeft, 0, 0)]
    [InlineData(MouseTarget.Custom, -2500, 1400)]
    public void UnifiedEditorRoundTripsAnExistingMouseStep(MouseTarget target, int x, int y)
    {
        var original = new MouseMoveActionConfig
        {
            Target = target,
            X = target == MouseTarget.Custom ? x : null,
            Y = target == MouseTarget.Custom ? y : null,
        };

        SequenceStepDialog.EditorState state = SequenceStepDialog.EditorState.FromAction(original);
        var result = Assert.IsType<MouseMoveActionConfig>(state.ToAction());

        Assert.Equal(SequenceStepDialog.MouseMoveIndex, state.TypeIndex);
        Assert.Equal(original.Target, result.Target);
        Assert.Equal(original.X, result.X);
        Assert.Equal(original.Y, result.Y);
    }

    [Fact]
    public void UnifiedStepTypeDropdownPinsEveryIndex()
    {
        Assert.Equal(0, SequenceStepDialog.MediaKeyIndex);
        Assert.Equal(1, SequenceStepDialog.LaunchIndex);
        Assert.Equal(2, SequenceStepDialog.KeySequenceIndex);
        Assert.Equal(3, SequenceStepDialog.SystemIndex);
        Assert.Equal(4, SequenceStepDialog.MouseMoveIndex);
        Assert.Equal(5, SequenceStepDialog.DelayIndex);
        Assert.Collection(
            SequenceStepDialog.StepTypeLabels,
            label => Assert.Equal("Press a media key", label),
            label => Assert.Equal("Launch a program", label),
            label => Assert.Equal("Key sequence", label),
            label => Assert.Equal("System command", label),
            label => Assert.Equal("Mouse move", label),
            label => Assert.Equal("Wait", label));
        Assert.DoesNotContain("Command sequence (macro)", SequenceStepDialog.StepTypeLabels);
    }

    [Fact]
    public void EveryUnifiedStepTypeCreatesAndRoundTripsItsAction()
    {
        CustomActionConfig[] originals =
        [
            new MediaKeyActionConfig { KeyName = MediaKeyName.Pause },
            new LaunchActionConfig { Path = @"C:\Apps\player.exe", Args = "--fullscreen" },
            new KeySequenceActionConfig { Sequence = "Ctrl+Shift+V" },
            new SystemActionConfig { Command = SystemCommandName.StopScreenSaver },
            new MouseMoveActionConfig { Target = MouseTarget.Custom, X = -2500, Y = 1400 },
            new DelayActionConfig { Ms = 1250 },
        ];
        int[] expectedIndices =
        [
            SequenceStepDialog.MediaKeyIndex,
            SequenceStepDialog.LaunchIndex,
            SequenceStepDialog.KeySequenceIndex,
            SequenceStepDialog.SystemIndex,
            SequenceStepDialog.MouseMoveIndex,
            SequenceStepDialog.DelayIndex,
        ];

        for (int i = 0; i < originals.Length; i++)
        {
            SequenceStepDialog.EditorState state = SequenceStepDialog.EditorState.FromAction(originals[i]);
            CustomActionConfig result = state.ToAction();

            Assert.Equal(expectedIndices[i], state.TypeIndex);
            AssertEquivalent(originals[i], result);
        }
    }

    private static void AssertEquivalent(CustomActionConfig expected, CustomActionConfig actual)
    {
        Assert.Equal(expected.GetType(), actual.GetType());
        switch (expected)
        {
            case MediaKeyActionConfig mediaKey:
                Assert.Equal(mediaKey.KeyName, Assert.IsType<MediaKeyActionConfig>(actual).KeyName);
                break;
            case LaunchActionConfig launch:
                var actualLaunch = Assert.IsType<LaunchActionConfig>(actual);
                Assert.Equal(launch.Path, actualLaunch.Path);
                Assert.Equal(launch.Args, actualLaunch.Args);
                break;
            case KeySequenceActionConfig keySequence:
                Assert.Equal(keySequence.Sequence, Assert.IsType<KeySequenceActionConfig>(actual).Sequence);
                break;
            case SystemActionConfig system:
                Assert.Equal(system.Command, Assert.IsType<SystemActionConfig>(actual).Command);
                break;
            case MouseMoveActionConfig mouseMove:
                var actualMouseMove = Assert.IsType<MouseMoveActionConfig>(actual);
                Assert.Equal(mouseMove.Target, actualMouseMove.Target);
                Assert.Equal(mouseMove.X, actualMouseMove.X);
                Assert.Equal(mouseMove.Y, actualMouseMove.Y);
                break;
            case DelayActionConfig delay:
                Assert.Equal(delay.Ms, Assert.IsType<DelayActionConfig>(actual).Ms);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(expected));
        }
    }
}
