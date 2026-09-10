namespace MatterHelm;

/// <summary>Shared numeric bounds for settings validated by both app and bridge configuration.</summary>
internal static class SettingLimits
{
    internal const int MomentaryResetMinimumMs = 0;
    internal const int MomentaryResetMaximumMs = 2000;
    internal const int OverlayOpacityMinimumPercent = 30;
    internal const int OverlayOpacityMaximumPercent = 100;
}
