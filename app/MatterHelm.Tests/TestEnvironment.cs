using System.Runtime.CompilerServices;

namespace MatterHelm.Tests;

/// <summary>Process-wide test state isolation established before xUnit discovers or runs tests.</summary>
internal static class TestEnvironment
{
    private static readonly string _isolatedRoot = Path.Combine(
        Path.GetTempPath(),
        "MatterHelmTests",
        $"{Environment.ProcessId}-{Guid.NewGuid():N}");

    /// <summary>The assembly's per-process state root.</summary>
    internal static string IsolatedRoot => _isolatedRoot;

    [ModuleInitializer]
    internal static void Initialize()
    {
        // Assign both defaults before creating anything so every app component
        // sees test-owned state even if its type initializer runs immediately.
        AppPaths.Root = _isolatedRoot;
        Log.LogDirectory = Path.Combine(_isolatedRoot, "logs");
        Directory.CreateDirectory(_isolatedRoot);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => DeleteIsolatedRoot();
    }

    private static void DeleteIsolatedRoot()
    {
        try
        {
            if (Directory.Exists(_isolatedRoot))
            {
                Directory.Delete(_isolatedRoot, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Process-exit cleanup is best effort; the OS can still have a
            // transient test-owned log or child-process handle open.
        }
    }
}
