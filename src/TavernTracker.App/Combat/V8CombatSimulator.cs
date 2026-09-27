using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using Microsoft.ClearScript.V8;
using TavernTracker.Core;
using TavernTracker.Core.Combat;

namespace TavernTracker.App.Combat;

/// <summary>
/// Runs Firestone's Battlegrounds simulator (bundled as tt-sim.js, MIT) inside Google's V8 engine via
/// Microsoft ClearScript. One engine, used from one thread at a time.
/// </summary>
public sealed class V8CombatSimulator : ICombatSimulator
{
    private readonly object _gate = new();
    private readonly HttpClient _http;
    private V8ScriptEngine? _engine;
    private Task<bool>? _preparing;

    public string Status { get; private set; } = "Not loaded";

    public V8CombatSimulator()
    {
        _http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromMinutes(3),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("TavernTracker/1.0");
    }

    public Task<bool> PrepareAsync()
    {
        lock (_gate)
        {
            if (_engine != null) return Task.FromResult(true);
            if (_preparing is { IsCompleted: false }) return _preparing;
            return _preparing = Task.Run(PrepareCoreAsync);
        }
    }

    private async Task<bool> PrepareCoreAsync()
    {
        try
        {
            Status = "Downloading card data for the simulator…";
            var cards = await SimCardData.GetAsync(_http).ConfigureAwait(false);
            if (cards == null)
            {
                Status = "Couldn't download the simulator's card data";
                return false;
            }

            Status = "Starting the simulator…";
            // In the single-file exe the V8 engine DLL is unpacked to a temporary folder; tell ClearScript
            // where to look (it otherwise searches next to the app only).
            var searchDirs = new[]
                {
                    AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") as string,
                    AppContext.BaseDirectory,
                    Path.GetDirectoryName(Environment.ProcessPath),
                }
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .SelectMany(d => d!.Split(';', StringSplitOptions.RemoveEmptyEntries))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            Microsoft.ClearScript.HostSettings.AuxiliarySearchPath = string.Join(";", searchDirs);
            var engine = new V8ScriptEngine();
            engine.Execute("tt-sim.js", LoadBundle());
            object count = engine.Script.TavernSim.loadCards(cards);
            Log.Info($"Simulator {engine.Script.TavernSim.version} ready with {Convert.ToInt32(count)} cards");
            lock (_gate) _engine = engine;
            Status = "Ready";
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Simulator failed to start", ex);
            Status = "The simulator couldn't start: " + ex.Message;
            return false;
        }
    }

    public CombatOdds Run(string inputJson)
    {
        lock (_gate)
        {
            if (_engine == null) throw new InvalidOperationException("Simulator not loaded");
            string json = _engine.Script.TavernSim.simulate(inputJson);
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            double D(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
            return new CombatOdds
            {
                Won = D("won"),
                Tied = D("tied"),
                Lost = D("lost"),
                WonLethal = D("wonLethal"),
                LostLethal = D("lostLethal"),
                AvgDamageWon = D("avgDamageWon"),
                AvgDamageLost = D("avgDamageLost"),
                Simulations = (int)D("simulations"),
            };
        }
    }

    private static string LoadBundle()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("tt-sim.js")
                           ?? throw new InvalidOperationException("tt-sim.js is missing from the app");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _engine?.Dispose();
            _engine = null;
        }
        _http.Dispose();
    }
}
