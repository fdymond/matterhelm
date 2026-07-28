using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace HtpcMatterBridge.Actions;

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

        var startInfo = new ProcessStartInfo
        {
            FileName = request.Path,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(request.Path) ?? "",
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
                Log.Error($"launch: {request.Path} did not start.");
                return false;
            }

            Log.Info($"launch: started {Path.GetFileName(request.Path)} (pid {process.Id}), detached.");
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            Log.Error($"launch: failed to start {request.Path}: {ex.Message}");
            return false;
        }
    }

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
