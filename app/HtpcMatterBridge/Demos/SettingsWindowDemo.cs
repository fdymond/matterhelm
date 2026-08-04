using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using HtpcMatterBridge.Ui;

namespace HtpcMatterBridge.Demos;

/// <summary>
/// S4-3 acceptance evidence: opens <see cref="Ui.SettingsWindow"/> over a
/// temp config dir, stages three edits through the shared
/// <see cref="Ui.SettingsViewModel"/> (port 40000, speaker rename, one
/// custom mediaKey command), saves through the window's Save path, and
/// objectively asserts all three landed in config.json on disk. Also
/// captures screenshot evidence (same DrawToBitmap technique and rationale
/// as <see cref="PairingWindowDemo"/>): the saved state
/// (<c>settings-window-light.png</c>), the active search filter
/// (<c>settings-window-search.png</c>), and — because WinForms color mode
/// cannot change once a window exists — a second process invocation with
/// <c>--dark</c> for <c>settings-window-dark.png</c>. Color modes are
/// forced (Classic/Dark, never System) so the evidence is deterministic
/// regardless of the OS theme. Returns 0 iff every check passes. Invoked
/// via <c>HtpcMatterBridge.exe --demo-settings-window</c>; not part of the
/// production tray flow.
/// </summary>
internal static partial class SettingsWindowDemo
{
    // S4-3 acceptance demo tuning.
    private const int SettingsDemoPort = 40_000;
    private const string SettingsDemoSpeakerName = "Demo Speaker";
    private const string SettingsDemoCustomKey = "demo-cmd";
    private const string SettingsDemoCustomName = "Demo Command";
    private const int AttachParentProcess = -1;

    /// <summary>Runs the demo. Returns 0 iff every check passed.</summary>
    internal static int Run(bool dark)
    {
        _ = AttachConsole(AttachParentProcess); // WinExe has no console; borrow the parent's if present.
        ApplicationConfiguration.Initialize();
        Application.SetColorMode(dark ? SystemColorMode.Dark : SystemColorMode.Classic);

        bool allPassed = true;
        void Check(bool pass, string what)
        {
            allPassed &= pass;
            Console.WriteLine($"{(pass ? "PASS" : "FAIL")}  {what}");
        }

        string tempRoot = Path.Combine(Path.GetTempPath(), "htpc-settings-demo", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        string configPath = Path.Combine(tempRoot, "config.json");
        var config = new Config(configPath, (level, message) => Console.WriteLine($"    [{level}] {message}"));

        var vm = new Ui.SettingsViewModel(config);
        using var window = new Ui.SettingsWindow(vm);
        window.StartPosition = FormStartPosition.Manual;
        window.Location = new Point(60, 60);
        window.Show();
        window.Activate();
        DemoSupport.Pump(500);

        if (dark)
        {
            // Child run: only the dark screenshot; the parent asserts it exists.
            SaveWindowScreenshot(window, "settings-window-dark.png");
            window.Close();
            return 0;
        }

        Console.WriteLine($"S4-3 settings-window demo — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine($"temp config: {configPath}");

        Ui.SettingsViewModel.Describe("ipc-port").Set!(vm.Working, SettingsDemoPort);
        Ui.SettingsViewModel.Describe("speaker-name").Set!(vm.Working, SettingsDemoSpeakerName);
        vm.AddCustomCommand(new CustomCommandConfig
        {
            Key = SettingsDemoCustomKey,
            Name = SettingsDemoCustomName,
            Action = new MediaKeyActionConfig { KeyName = MediaKeyName.Stop },
        });
        window.RefreshFromViewModel();
        DemoSupport.Pump(200);
        Check(vm.IsDirty && vm.IsValid, "staged edits leave the view-model dirty and valid (Save enabled)");

        window.SaveNow();
        DemoSupport.Pump(200);
        Check(!vm.IsDirty, "save re-stages the working copy (dirty cleared, window stays open)");

        using (JsonDocument document = JsonDocument.Parse(File.ReadAllText(configPath)))
        {
            JsonElement root = document.RootElement;
            Check(
                root.GetProperty("ipcPort").GetInt32() == SettingsDemoPort,
                $"config.json ipcPort == {SettingsDemoPort}");
            Check(
                root.GetProperty("commands").GetProperty("speaker").GetProperty("name").GetString() == SettingsDemoSpeakerName,
                $"config.json commands.speaker.name == \"{SettingsDemoSpeakerName}\"");
            JsonElement custom = root.GetProperty("commands").GetProperty("custom");
            bool customOk = custom.GetArrayLength() == 1
                && custom[0].GetProperty("key").GetString() == SettingsDemoCustomKey
                && custom[0].GetProperty("action").GetProperty("type").GetString() == "mediaKey"
                && custom[0].GetProperty("action").GetProperty("keyName").GetString() == "stop";
            Check(customOk, $"config.json commands.custom[0] == {{key: {SettingsDemoCustomKey}, mediaKey stop}}");
        }

        SaveWindowScreenshot(window, "settings-window-light.png");

        SettingsSearchResult filtered = Ui.SettingsSearch.Filter(Ui.SettingsViewModel.Categories, "port");
        Check(
            filtered.MatchingSettingIds.Contains("ipc-port") && !filtered.MatchingSettingIds.Contains("speaker-name"),
            "SettingsSearch \"port\" matches ipc-port and filters out speaker-name");
        window.SetSearchQuery("port");
        DemoSupport.Pump(200);
        Check(
            window.IsSettingRowVisible("ipc-port") && !window.IsSettingRowVisible("log-level"),
            "window hides non-matching rows while searching \"port\"");
        SaveWindowScreenshot(window, "settings-window-search.png");
        window.SetSearchQuery("");
        DemoSupport.Pump(100);

        // S4-5 DPI audit evidence: every category page as its own screenshot
        // (the "light" shot above only covers the first page), captured at
        // whatever DPI the demo actually runs at (200 % on the dev display).
        for (int i = 0; i < Ui.SettingsViewModel.Categories.Count; i++)
        {
            string categoryId = Ui.SettingsViewModel.Categories[i].Id;
            window.SelectCategory(i);
            DemoSupport.Pump(150);
            SaveWindowScreenshot(window, $"settings-window-page-{categoryId}.png");
            if (window.ScrollCurrentCategoryToEnd())
            {
                // Long pages (Devices & Commands) hide their tail — capture it too.
                DemoSupport.Pump(150);
                SaveWindowScreenshot(window, $"settings-window-page-{categoryId}-bottom.png");
            }
        }

        window.SelectCategory(0);
        DemoSupport.Pump(100);
        window.Close();
        DemoSupport.Pump(100);

        // S4-5 DPI audit evidence: the custom-command dialog in both action
        // modes (media key / launch). Shown modeless purely for capture — the
        // production path is ShowDialog from the settings window.
        using (var mediaDialog = new Ui.CustomCommandDialog(vm, existing: null))
        {
            mediaDialog.StartPosition = FormStartPosition.Manual;
            mediaDialog.Location = new Point(60, 60);
            mediaDialog.Show();
            DemoSupport.Pump(300);
            SaveWindowScreenshot(mediaDialog, "custom-command-mediakey.png");
            mediaDialog.Close();
        }

        using (var launchDialog = new Ui.CustomCommandDialog(vm, existing: new CustomCommandConfig
        {
            Key = "demo-launch",
            Name = "Demo Launch",
            Action = new LaunchActionConfig { Path = Environment.ProcessPath ?? "", Args = "--demo" },
        }))
        {
            launchDialog.StartPosition = FormStartPosition.Manual;
            launchDialog.Location = new Point(60, 60);
            launchDialog.Show();
            DemoSupport.Pump(300);
            SaveWindowScreenshot(launchDialog, "custom-command-launch.png");
            launchDialog.Close();
        }

        // Dark rendering can't be flipped mid-process, so a second invocation
        // of this exe produces the dark screenshot (see the method doc).
        string darkPng = Path.Combine(AppContext.BaseDirectory, "settings-window-dark.png");
        File.Delete(darkPng);
        bool darkOk = false;
        if (Environment.ProcessPath is { } exe)
        {
            var startInfo = new ProcessStartInfo(exe) { UseShellExecute = false };
            startInfo.ArgumentList.Add("--demo-settings-window");
            startInfo.ArgumentList.Add("--dark");
            using Process? child = Process.Start(startInfo);
            darkOk = child is not null && child.WaitForExit(60_000) && child.ExitCode == 0 && File.Exists(darkPng);
        }

        Check(darkOk, "second process (--dark) wrote settings-window-dark.png and exited 0");

        Console.WriteLine(
            $"screenshots: {AppContext.BaseDirectory}settings-window-{{light,dark,search,page-*}}.png + custom-command-{{mediakey,launch}}.png");
        Console.WriteLine($"OVERALL: {(allPassed ? "PASS" : "FAIL")}");
        return allPassed ? 0 : 1;
    }

    /// <summary>Renders the full window (client + frame) to a PNG next to the exe via <see cref="Control.DrawToBitmap"/> (see <see cref="PairingWindowDemo"/> remarks for why not a screen capture).</summary>
    private static void SaveWindowScreenshot(Form window, string fileName)
    {
        using var bitmap = new Bitmap(window.Width, window.Height);
        window.DrawToBitmap(bitmap, new Rectangle(Point.Empty, window.Size));
        bitmap.Save(Path.Combine(AppContext.BaseDirectory, fileName), ImageFormat.Png);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(int processId);
}
