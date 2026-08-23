using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace MatterHelm.Actions;

/// <summary>Payload for the executor's <c>launch</c> action: the executable and its (unsplit) argument string from a custom command's config.</summary>
public sealed record LaunchRequest(string Path, string Args);

/// <summary>
/// Executes ADR-004 <c>launch</c> custom actions: starts the target program
/// detached (the child outlives us; its handle is disposed immediately) and
/// non-elevated, with <c>UseShellExecute = false</c> and per-argument
/// <c>ArgumentList</c> entries — the command line is never handed to a shell,
/// so no metacharacter in the config can change what runs. A missing
/// executable is a failure (nacked upstream), never a throw.
/// </summary>
public static class AppLaunch
{
    /// <summary>Starts the requested program. Never throws; <c>false</c> = failure (logged).</summary>
    public static bool Start(LaunchRequest request)
    {
        if (!File.Exists(request.Path))
        {
            Log.Error($"launch: executable not found: {request.Path}");
            return false;
        }

        // S9-5: Microsoft Store (MSIX) apps cannot be started by their package
        // path — Windows denies CreateProcess inside \Program Files\WindowsApps\
        // by design (owner report: Spotify "Access is denied"). Redirect to the
        // per-user app execution alias, which CreateProcess does support.
        string launchPath = request.Path;
        if (IsPackagedAppPath(launchPath))
        {
            string alias = ExecutionAliasFor(launchPath);
            if (!File.Exists(alias))
            {
                Log.Error(
                    $"launch: {launchPath} is a Microsoft Store app, which Windows refuses to start by its package "
                    + $"path, and no app execution alias was found at {alias}. Use the alias path (enable it under "
                    + "Windows Settings > Apps > Advanced app settings > App execution aliases if needed).");
                return false;
            }

            Log.Info($"launch: Store-app package path redirected to its execution alias: {alias}");
            launchPath = alias;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = launchPath,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(launchPath) ?? "",
        };
        foreach (string argument in SplitArgs(request.Args))
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                Log.Error($"launch: {launchPath} did not start.");
                return false;
            }

            Log.Info($"launch: started {Path.GetFileName(launchPath)} (pid {process.Id}), detached.");
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            Log.Error($"launch: failed to start {launchPath}: {ex.Message}");
            return false;
        }
    }

    /// <summary>True when <paramref name="path"/> points inside the protected MSIX package store (<c>\Program Files\WindowsApps\</c>), which CreateProcess refuses (S9-5).</summary>
    public static bool IsPackagedAppPath(string path) =>
        path.Contains(@"\Program Files\WindowsApps\", StringComparison.OrdinalIgnoreCase);

    /// <summary>The per-user app execution alias for a packaged exe: same file name under <c>%LOCALAPPDATA%\Microsoft\WindowsApps</c> (S9-5).</summary>
    public static string ExecutionAliasFor(string packagedPath) =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft",
            "WindowsApps",
            Path.GetFileName(packagedPath));

    /// <summary>
    /// Splits a config <c>args</c> string into discrete arguments for
    /// <see cref="ProcessStartInfo.ArgumentList"/>. Rules (deliberately
    /// simple and Windows-path-friendly, NOT the MSVCRT rules): arguments
    /// are separated by runs of spaces/tabs; a double-quoted segment keeps
    /// its whitespace; a doubled quote (<c>""</c>) inside a quoted segment
    /// emits one literal quote; backslashes are always literal; an
    /// unterminated quote extends to the end of the string; a bare <c>""</c>
    /// yields one empty argument.
    /// </summary>
    public static IReadOnlyList<string> SplitArgs(string args)
    {
        List<string> result = [];
        var current = new StringBuilder();
        bool inQuotes = false;
        bool tokenStarted = false;
        for (int i = 0; i < args.Length; i++)
        {
            char c = args[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < args.Length && args[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                    tokenStarted = true;
                }
            }
            else if (!inQuotes && c is ' ' or '\t')
            {
                if (tokenStarted)
                {
                    result.Add(current.ToString());
                    current.Clear();
                    tokenStarted = false;
                }
            }
            else
            {
                current.Append(c);
                tokenStarted = true;
            }
        }

        if (tokenStarted)
        {
            result.Add(current.ToString());
        }

        return result;
    }
}
