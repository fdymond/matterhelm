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
    public void MouseMoveEditorStateRoundTripsEveryTarget(MouseTarget target, int x, int y)
    {
        var original = new MouseMoveActionConfig
        {
            Target = target,
            X = target == MouseTarget.Custom ? x : null,
            Y = target == MouseTarget.Custom ? y : null,
        };

        MouseMoveActionConfig result = CustomCommandDialog.MouseMoveEditorState
            .FromAction(original)
            .ToAction();

        Assert.Equal(original.Target, result.Target);
        Assert.Equal(original.X, result.X);
        Assert.Equal(original.Y, result.Y);
    }

    [Theory]
    [InlineData(MouseTarget.TopLeft, 0, 0)]
    [InlineData(MouseTarget.Custom, -2500, 1400)]
    public void SequenceMouseStepUsesTheStandaloneTargetPickerRoundTrip(MouseTarget target, int x, int y)
    {
        var original = new MouseMoveActionConfig
        {
            Target = target,
            X = target == MouseTarget.Custom ? x : null,
            Y = target == MouseTarget.Custom ? y : null,
        };

        MouseMoveActionConfig result = CustomCommandDialog.CreateMouseMoveSequenceStep(
            CustomCommandDialog.MouseMoveEditorState.FromAction(original));

        Assert.Equal(original.Target, result.Target);
        Assert.Equal(original.X, result.X);
        Assert.Equal(original.Y, result.Y);
    }
}
