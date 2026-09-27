using System.IO.Compression;
using System.Text;

namespace TavernTracker.Core.Combat;

/// <summary>Win / tie / loss odds for one combat, in percent.</summary>
public sealed class CombatOdds
{
    public double Won { get; init; }
    public double Tied { get; init; }
    public double Lost { get; init; }
    public double WonLethal { get; init; }
    public double LostLethal { get; init; }
    public double AvgDamageWon { get; init; }
    public double AvgDamageLost { get; init; }
    public int Simulations { get; init; }
}

/// <summary>Runs Firestone's simulator. Implemented in the app (it needs a JavaScript engine).</summary>
public interface ICombatSimulator : IDisposable
{
    /// <summary>Loads the simulator and its card data. Safe to call more than once.</summary>
    Task<bool> PrepareAsync();
    /// <summary>Runs one simulation. Throws on failure.</summary>
    CombatOdds Run(string inputJson);
    string Status { get; }
}

/// <summary>
/// The card database Firestone's simulator was written against (from Firestone's own mirrors), downloaded
/// once a day. It's a large file, so it's kept on disk and only read when the simulator starts.
/// </summary>
public static class SimCardData
{
    private static readonly string[] Mirrors =
    {
        "https://static.zerotoheroes.com/data/cards/cards_enUS.gz.json",
        "https://static.firestoneapp.com/data/cards/cards_enUS.gz.json",
    };

    public static string CachePath => AppPaths.File("simcards.json");

    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Returns the card JSON text, downloading it when the cached copy is missing or a day old.</summary>
    public static async Task<string?> GetAsync(HttpClient http)
    {
        var path = await EnsureFileAsync(http).ConfigureAwait(false);
        return path != null ? await File.ReadAllTextAsync(path, Encoding.UTF8).ConfigureAwait(false) : null;
    }

    /// <summary>
    /// Makes sure a copy of Firestone's card file is on disk (refreshed daily) and returns its path, or null.
    /// Shared by the simulator and the minion list, so it's only downloaded once.
    /// </summary>
    public static async Task<string?> EnsureFileAsync(HttpClient http)
    {
        var path = CachePath;
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            bool fresh = File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromDays(1);
            if (fresh) return path;
            foreach (var url in Mirrors)
            {
                try
                {
                    var bytes = await http.GetByteArrayAsync(url).ConfigureAwait(false);
                    var text = Decode(bytes);
                    if (text.Length > 1000 && text.TrimStart().StartsWith('['))
                    {
                        await File.WriteAllTextAsync(path + ".tmp", text, Encoding.UTF8).ConfigureAwait(false);
                        File.Move(path + ".tmp", path, overwrite: true);
                        Log.Info($"Firestone card data downloaded from {new Uri(url).Host} ({text.Length / 1024 / 1024} MB)");
                        return path;
                    }
                    Log.Warn($"Card data from {url} looked wrong; trying the next mirror");
                }
                catch (Exception ex)
                {
                    Log.Warn($"Card data download failed from {url}: {ex.Message}");
                }
            }
            // Use the old copy if the download failed.
            return File.Exists(path) ? path : null;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>The file may arrive gzip-compressed (its name says .gz) or already decompressed.</summary>
    public static string Decode(byte[] bytes)
    {
        if (bytes.Length > 2 && bytes[0] == 0x1F && bytes[1] == 0x8B)
        {
            using var gz = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
            using var reader = new StreamReader(gz, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        return Encoding.UTF8.GetString(bytes);
    }
}
