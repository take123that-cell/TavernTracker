using System.Text;
using Microsoft.Win32;

namespace TavernTracker.Core.Hearthstone;

/// <summary>Finds the Hearthstone install and makes sure the game writes the log we read.</summary>
public static class HearthstoneSetup
{
    /// <summary>Returns the folder containing Hearthstone.exe, or null if it can't be found.</summary>
    public static string? FindInstall(string? configured)
    {
        if (IsInstall(configured)) return configured;

        if (OperatingSystem.IsWindows())
        {
            foreach (var key in new[]
                     {
                         @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Hearthstone",
                         @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Hearthstone",
                     })
            {
                try
                {
                    using var k = Registry.LocalMachine.OpenSubKey(key);
                    var dir = k?.GetValue("InstallLocation") as string;
                    if (IsInstall(dir)) return dir;
                }
                catch (Exception ex)
                {
                    Log.Warn($"Registry lookup failed: {ex.Message}");
                }
            }
        }

        foreach (var guess in new[]
                 {
                     @"C:\Program Files (x86)\Hearthstone",
                     @"C:\Program Files\Hearthstone",
                     @"D:\Hearthstone",
                     @"D:\Games\Hearthstone",
                 })
        {
            if (IsInstall(guess)) return guess;
        }
        return null;
    }

    private static bool IsInstall(string? dir) =>
        !string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, "Hearthstone.exe"));

    public static string LogsDirectory(string installDir) => Path.Combine(installDir, "Logs");

    public static string LogConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Blizzard", "Hearthstone", "log.config");

    private static readonly (string Key, string Value)[] PowerSettings =
    {
        ("LogLevel", "1"),
        ("FilePrinting", "True"),
        ("ConsolePrinting", "False"),
        ("ScreenPrinting", "False"),
        ("Verbose", "True"),
    };

    /// <summary>
    /// Makes sure log.config has a [Power] section with verbose file logging, keeping every other
    /// section (HDT and other trackers use the same file). Returns true if the file changed,
    /// which means Hearthstone must be restarted before the log appears.
    /// </summary>
    public static bool EnsureLogConfig(string? path = null)
    {
        path ??= LogConfigPath;
        try
        {
            var existing = File.Exists(path) ? File.ReadAllText(path) : "";
            var (updated, changed) = WithPowerSection(existing);
            if (!changed) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, updated, new UTF8Encoding(false));
            Log.Info("log.config updated");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Could not update log.config", ex);
            return false;
        }
    }

    /// <summary>Pure text transform, so it can be tested.</summary>
    public static (string Text, bool Changed) WithPowerSection(string text)
    {
        bool changed = false;
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[^1] == "") lines.RemoveAt(lines.Count - 1);

        int start = lines.FindIndex(l => l.Trim().Equals("[Power]", StringComparison.OrdinalIgnoreCase));
        if (start < 0)
        {
            if (lines.Count > 0 && lines[^1].Trim() != "") lines.Add("");
            lines.Add("[Power]");
            lines.AddRange(PowerSettings.Select(p => $"{p.Key}={p.Value}"));
            changed = true;
        }
        else
        {
            int end = lines.FindIndex(start + 1, l => l.TrimStart().StartsWith("["));
            if (end < 0) end = lines.Count;
            foreach (var (key, value) in PowerSettings)
            {
                int at = -1;
                for (int i = start + 1; i < end; i++)
                {
                    if (lines[i].Trim().StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)) { at = i; break; }
                }
                var line = $"{key}={value}";
                if (at >= 0)
                {
                    if (!lines[at].Trim().Equals(line, StringComparison.OrdinalIgnoreCase)) { lines[at] = line; changed = true; }
                }
                else
                {
                    lines.Insert(end, line);
                    end++;
                    changed = true;
                }
            }
        }
        return (string.Join("\r\n", lines) + "\r\n", changed);
    }
}
