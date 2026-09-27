using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TavernTracker.Core;

/// <summary>Where the app keeps its files: %AppData%\TavernTracker (overridable for tests).</summary>
public static class AppPaths
{
    private static string? _root;

    public static string Root
    {
        get
        {
            _root ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TavernTracker");
            Directory.CreateDirectory(_root);
            return _root;
        }
        set => _root = value;
    }

    public static string File(string name) => Path.Combine(Root, name);
}

/// <summary>Small file logger with a size cap.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private const long MaxBytes = 2 * 1024 * 1024;

    public static void Info(string msg) => Write("INFO ", msg);
    public static void Warn(string msg) => Write("WARN ", msg);
    public static void Error(string msg, Exception? ex = null) =>
        Write("ERROR", ex == null ? msg : $"{msg} :: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string msg)
    {
        try
        {
            lock (Gate)
            {
                var path = AppPaths.File("log.txt");
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxBytes)
                {
                    var old = AppPaths.File("log.old.txt");
                    System.IO.File.Delete(old);
                    System.IO.File.Move(path, old);
                }
                System.IO.File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {msg}{Environment.NewLine}");
            }
        }
        catch
        {
            // never let logging crash the app
        }
    }
}

public static class JsonFile
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keep non-Latin names readable
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNameCaseInsensitive = true,
    };

    public static T? Load<T>(string path) where T : class
    {
        try
        {
            if (!System.IO.File.Exists(path)) return null;
            return JsonSerializer.Deserialize<T>(System.IO.File.ReadAllText(path, Encoding.UTF8), Options);
        }
        catch (Exception ex)
        {
            Log.Error($"Could not read {Path.GetFileName(path)}", ex);
            return null;
        }
    }

    /// <summary>Write to a temp file then swap it in, so a crash can't leave a half-written file.</summary>
    public static void Save<T>(string path, T value)
    {
        try
        {
            var tmp = path + ".tmp";
            System.IO.File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options), Encoding.UTF8);
            System.IO.File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error($"Could not save {Path.GetFileName(path)}", ex);
        }
    }
}

/// <summary>Turns BattleTags into the names the public leaderboard uses.</summary>
public static class Names
{
    /// <summary>"Name#1234" -> "Name".</summary>
    public static string Display(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var s = raw.Trim();
        var hash = s.IndexOf('#');
        return hash > 0 ? s[..hash] : s;
    }

    public static string Key(string? raw) => Display(raw).ToLowerInvariant();
}
