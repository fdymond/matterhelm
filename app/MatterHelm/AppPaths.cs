namespace MatterHelm;

/// <summary>
/// The app's per-user data root (S7-2 product rename). Defaults to
/// <c>%APPDATA%\MatterHelm</c>; <c>Program</c> repoints it at
/// <see cref="LegacyRoot"/> for the session when the startup
/// <see cref="Migration"/> cannot move a locked legacy directory. Every
/// default path (config.json, <c>logs\</c>, <c>matter\</c> fabric storage,
/// diagnostics exports) derives from <see cref="Root"/> at access time — never
/// cached at static-init — so the fallback applies app-wide regardless of
/// type-initialization order.
/// </summary>
public static class AppPaths
{
    private static string _root = DefaultRoot;

    /// <summary>The current product root: <c>%APPDATA%\MatterHelm</c>.</summary>
    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MatterHelm");

    /// <summary>The pre-rename root, <c>%APPDATA%\HtpcMatterBridge</c> — only referenced by the startup migration.</summary>
    public static string LegacyRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "HtpcMatterBridge");

    /// <summary>
    /// The effective data root. <c>Program</c> assigns it exactly once, from
    /// the migration outcome, before any other component touches disk;
    /// thereafter it is read-only in practice.
    /// </summary>
    public static string Root
    {
        get => Volatile.Read(ref _root);
        set => Volatile.Write(ref _root, value);
    }
}
