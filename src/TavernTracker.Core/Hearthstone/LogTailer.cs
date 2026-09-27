using System.Globalization;
using System.Text;

namespace TavernTracker.Core.Hearthstone;

/// <summary>
/// Follows Hearthstone's Power.log as the game writes it. Since 2022 the client starts a new
/// folder per launch (Logs\Hearthstone_YYYY_MM_DD_HH_MM_SS\Power.log); we always follow the newest.
/// </summary>
public sealed class LogTailer : IDisposable
{
    private readonly string _logsDir;
    private readonly Action<string, DateTime> _onLine;
    private readonly Action<string> _onNewFile;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    private string? _file;
    private long _offset;
    private readonly StringBuilder _partial = new();
    private Decoder _decoder = Encoding.UTF8.GetDecoder();
    private DateTime _fileDate = DateTime.Today;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(250);
    public string? CurrentFile => _file;

    /// <param name="onLine">Called for every complete line, with the date the log session started.</param>
    /// <param name="onNewFile">Called when we switch to a new Power.log (Hearthstone restarted).</param>
    public LogTailer(string logsDir, Action<string, DateTime> onLine, Action<string> onNewFile)
    {
        _logsDir = logsDir;
        _onLine = onLine;
        _onNewFile = onNewFile;
    }

    public void Start() => _loop ??= Task.Run(() => RunAsync(_cts.Token));

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(2000); } catch { /* shutting down */ }
        _cts.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var lastDirCheck = DateTime.MinValue;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_file == null || DateTime.UtcNow - lastDirCheck > TimeSpan.FromSeconds(3))
                {
                    lastDirCheck = DateTime.UtcNow;
                    var newest = FindNewestPowerLog(_logsDir);
                    if (newest != null && newest != _file)
                    {
                        _file = newest;
                        _offset = 0;
                        _partial.Clear();
            _decoder = Encoding.UTF8.GetDecoder();
                        _fileDate = SessionDate(newest);
                        Log.Info($"Reading {newest}");
                        _onNewFile(newest);
                    }
                }
                if (_file != null) ReadNew();
            }
            catch (Exception ex)
            {
                Log.Error("Log reading error", ex);
            }

            try { await Task.Delay(PollInterval, ct); }
            catch (TaskCanceledException) { break; }
        }
    }

    /// <summary>Reads whatever was appended since last time. Public so tests can drive it directly.</summary>
    public void ReadNew()
    {
        if (_file == null || !File.Exists(_file)) return;
        using var fs = new FileStream(_file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length < _offset)
        {
            // File was truncated/replaced: start over.
            _offset = 0;
            _partial.Clear();
            _decoder = Encoding.UTF8.GetDecoder();
        }
        if (fs.Length == _offset) return;

        fs.Seek(_offset, SeekOrigin.Begin);
        var buffer = new byte[64 * 1024];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
        int read;
        while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
        {
            _offset += read;
            int n = _decoder.GetChars(buffer, 0, read, chars, 0, flush: false);
            for (int i = 0; i < n; i++)
            {
                char c = chars[i];
                if (c == '\n')
                {
                    var line = _partial.ToString().TrimEnd('\r');
                    _partial.Clear();
                    if (line.Length > 0) _onLine(line, _fileDate);
                }
                else
                {
                    _partial.Append(c);
                }
            }
        }
    }

    /// <summary>Manual file mode for "import a Power.log" and tests.</summary>
    public void OpenFile(string path)
    {
        _file = path;
        _offset = 0;
        _partial.Clear();
            _decoder = Encoding.UTF8.GetDecoder();
        _fileDate = SessionDate(path);
    }

    public static string? FindNewestPowerLog(string logsDir)
    {
        if (!Directory.Exists(logsDir)) return null;
        var newestDir = new DirectoryInfo(logsDir)
            .GetDirectories("Hearthstone_*")
            .OrderByDescending(d => d.CreationTimeUtc)
            .FirstOrDefault(d => File.Exists(Path.Combine(d.FullName, "Power.log")));
        if (newestDir != null) return Path.Combine(newestDir.FullName, "Power.log");

        // Older clients wrote straight into Logs\.
        var flat = Path.Combine(logsDir, "Power.log");
        return File.Exists(flat) ? flat : null;
    }

    /// <summary>Date the client session started, from the folder name; falls back to the file time.</summary>
    public static DateTime SessionDate(string powerLogPath)
    {
        var folder = Path.GetFileName(Path.GetDirectoryName(powerLogPath)) ?? "";
        if (folder.StartsWith("Hearthstone_") &&
            DateTime.TryParseExact(folder["Hearthstone_".Length..], "yyyy_MM_dd_HH_mm_ss",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt))
            return dt;
        try { return File.GetCreationTime(powerLogPath); }
        catch { return DateTime.Now; }
    }
}
