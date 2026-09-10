using MatterHelm.Sidecar;

namespace MatterHelm;

/// <summary>
/// Resolves the packaged sidecar layout or the development fallback used by
/// the tray application.
/// </summary>
internal static class SidecarLaunchSpec
{
    private const string LayoutManifestName = "sidecar-layout.json";

    /// <summary>
    /// The packaged spec under <paramref name="baseDirectory"/>, or null when
    /// no packaged layout is declared or discoverable there. A layout manifest takes
    /// precedence over file-presence heuristics so stale files left by an old
    /// portable release cannot select the wrong sidecar.
    /// </summary>
    internal static SidecarSpec? TryPackaged(string baseDirectory)
    {
        string packagedDir = Path.Combine(baseDirectory, "sidecar");
        string manifestPath = Path.Combine(baseDirectory, LayoutManifestName);
        if (File.Exists(manifestPath))
        {
            return TryManifestLayout(manifestPath, packagedDir);
        }

        string packagedSea = Path.Combine(packagedDir, "bridge.exe");
        if (File.Exists(packagedSea))
        {
            return new SidecarSpec(packagedSea, [], packagedDir);
        }

        return TryNodeLayout(packagedDir);
    }

    /// <summary>
    /// The production spec, resolved relative to the exe; falls back to the
    /// repo build tree when the packaged layout is absent so the app remains
    /// runnable during development.
    /// </summary>
    internal static SidecarSpec Default()
    {
        if (TryPackaged(AppContext.BaseDirectory) is SidecarSpec packaged)
        {
            return packaged;
        }

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string bridgeDir = Path.Combine(dir.FullName, "bridge");
            string srcDir = Path.Combine(bridgeDir, "src");
            if (!File.Exists(Path.Combine(srcDir, "index.ts")))
            {
                continue;
            }

            string bundle = Path.Combine(bridgeDir, "dist", "bridge.cjs");
            if (IsBundleFresh(bundle, srcDir))
            {
                return new SidecarSpec("node.exe", [bundle], bridgeDir);
            }

            string tsxCli = Path.Combine(bridgeDir, "node_modules", "tsx", "dist", "cli.mjs");
            if (File.Exists(tsxCli))
            {
                return new SidecarSpec(
                    "node.exe", [tsxCli, Path.Combine(srcDir, "index.ts")], bridgeDir);
            }
        }

        string packagedDir = Path.Combine(AppContext.BaseDirectory, "sidecar");
        return new SidecarSpec(Path.Combine(packagedDir, "bridge.exe"), [], packagedDir);
    }

    /// <summary>
    /// True iff <paramref name="bundlePath"/> exists and is at least as new
    /// as every file under <paramref name="srcDir"/>.
    /// </summary>
    internal static bool IsBundleFresh(string bundlePath, string srcDir)
    {
        if (!File.Exists(bundlePath))
        {
            return false;
        }

        DateTime bundleTime = File.GetLastWriteTimeUtc(bundlePath);
        if (!Directory.Exists(srcDir))
        {
            return true;
        }

        foreach (string file in Directory.EnumerateFiles(srcDir, "*", SearchOption.AllDirectories))
        {
            if (File.GetLastWriteTimeUtc(file) > bundleTime)
            {
                return false;
            }
        }

        return true;
    }

    private static SidecarSpec? TryManifestLayout(string manifestPath, string packagedDir)
    {
        try
        {
            using FileStream stream = File.OpenRead(manifestPath);
            using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(stream);
            if (!document.RootElement.TryGetProperty("layout", out System.Text.Json.JsonElement layout)
                || layout.ValueKind != System.Text.Json.JsonValueKind.String)
            {
                return null;
            }

            return layout.GetString() switch
            {
                "sea" => new SidecarSpec(Path.Combine(packagedDir, "bridge.exe"), [], packagedDir),
                "node" => new SidecarSpec(
                    Path.Combine(packagedDir, "node.exe"),
                    [Path.Combine(packagedDir, "bridge.cjs")],
                    packagedDir),
                _ => null,
            };
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static SidecarSpec? TryNodeLayout(string packagedDir)
    {
        string executable = Path.Combine(packagedDir, "node.exe");
        string entry = Path.Combine(packagedDir, "bridge.cjs");
        return File.Exists(executable) && File.Exists(entry)
            ? new SidecarSpec(executable, [entry], packagedDir)
            : null;
    }
}
