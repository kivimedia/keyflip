using System.Text.Json;
using System.Text.Json.Serialization;

namespace KeyFlip;

public sealed class Config
{
    public string? AnthropicApiKey { get; set; }
    public double JevThreshold { get; set; } = 0.70;
    public bool FixerEnabled { get; set; } = true;
    public bool Paused { get; set; }
    public List<string> ExcludedApps { get; set; } = new();
    public List<string> FixerExcludedApps { get; set; } = new() { "WindowsTerminal", "cmd", "powershell", "pwsh", "conhost", "mintty" };
}

public sealed class Learned
{
    public Lang Lang { get; set; }
    public string Source { get; set; } = "";
    public DateTime At { get; set; }
}

public sealed class StoreData
{
    public Dictionary<string, Learned> Fields { get; set; } = new();
    public Dictionary<string, Lang> History { get; set; } = new();
    public HashSet<string> NeverFlip { get; set; } = new();
    public Dictionary<string, DayStats> Days { get; set; } = new();
}

public sealed class DayStats
{
    public int Focus { get; set; }
    public int Jev { get; set; }
    public int Haiku { get; set; }
    public int Fixes { get; set; }
    public int Undos { get; set; }
    public double Usd { get; set; }
}

/// <summary>Everything KeyFlip remembers, in %LOCALAPPDATA%\KeyFlip. Thread safe; saves debounced.</summary>
public sealed class Store
{
    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KeyFlip");
    private static readonly string DataPath = Path.Combine(Dir, "learned.json");
    private static readonly string ConfigPath = Path.Combine(Dir, "config.json");
    public static readonly string LogPath = Path.Combine(Dir, "log.jsonl");

    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _lock = new();
    private readonly StoreData _data;
    private System.Threading.Timer? _saveTimer;
    public Config Config { get; }

    public Store()
    {
        Directory.CreateDirectory(Dir);
        _data = TryRead<StoreData>(DataPath) ?? new StoreData();
        Config = TryRead<Config>(ConfigPath) ?? new Config();
        if (!File.Exists(ConfigPath)) SaveConfig();
    }

    private static T? TryRead<T>(string path) where T : class
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : null; }
        catch { return null; }
    }

    public void SaveConfig()
    {
        lock (_lock) File.WriteAllText(ConfigPath, JsonSerializer.Serialize(Config, Json));
    }

    private void ScheduleSave()
    {
        _saveTimer ??= new System.Threading.Timer(_ => Flush());
        _saveTimer.Change(2000, Timeout.Infinite);
    }

    public void Flush()
    {
        string json;
        lock (_lock) json = JsonSerializer.Serialize(_data, Json);
        try { File.WriteAllText(DataPath, json); } catch { }
    }

    public Learned? GetField(string key) { lock (_lock) return _data.Fields.TryGetValue(key, out var l) ? l : null; }

    public void SetField(string key, Lang lang, string source)
    {
        lock (_lock)
        {
            // A manual choice is never overwritten by a model or by typing.
            if (_data.Fields.TryGetValue(key, out var old) && old.Source == "manual" && source != "manual") return;
            _data.Fields[key] = new Learned { Lang = lang, Source = source, At = DateTime.Now };
        }
        ScheduleSave();
    }

    public Lang? GetHistory(string appSite) { lock (_lock) return _data.History.TryGetValue(appSite, out var l) ? l : null; }

    public void SetHistory(string appSite, Lang lang)
    {
        lock (_lock)
        {
            if (_data.History.TryGetValue(appSite, out var old) && old == lang) return;
            _data.History[appSite] = lang;
        }
        ScheduleSave();
    }

    public ISet<string> NeverFlip { get { lock (_lock) return new HashSet<string>(_data.NeverFlip); } }

    public void AddNeverFlip(string word) { lock (_lock) _data.NeverFlip.Add(word); ScheduleSave(); }

    public DayStats Today
    {
        get
        {
            lock (_lock)
            {
                var k = DateTime.Now.ToString("yyyy-MM-dd");
                if (!_data.Days.TryGetValue(k, out var d)) _data.Days[k] = d = new DayStats();
                return d;
            }
        }
    }

    public void Count(Action<DayStats> change)
    {
        var d = Today;
        lock (_lock) change(d);
        ScheduleSave();
    }

    public double MonthUsd()
    {
        var prefix = DateTime.Now.ToString("yyyy-MM");
        lock (_lock) return _data.Days.Where(kv => kv.Key.StartsWith(prefix)).Sum(kv => kv.Value.Usd);
    }
}

/// <summary>One JSON line per decision, so the real cost and which step decided are visible.</summary>
public static class Log
{
    private static readonly object Lock = new();

    public static void Write(object entry)
    {
        try
        {
            var line = JsonSerializer.Serialize(entry, new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } });
            lock (Lock) File.AppendAllText(Store.LogPath, $"{{\"ts\":\"{DateTime.Now:yyyy-MM-ddTHH:mm:ss.fff}\",{line[1..]}\n");
        }
        catch { }
    }
}
