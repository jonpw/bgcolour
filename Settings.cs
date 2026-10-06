using System.Text.Json;
using System.Text.Json.Serialization;

namespace BgColour;

sealed class Settings
{
    public int Monitor { get; set; } = 0;
    public int Fps { get; set; } = 30;
    /// <summary>Smoothing time constant in seconds (0 = none).</summary>
    public double Smooth { get; set; } = 0.15;
    public int SampleWidth { get; set; } = 64;
    public float Saturation { get; set; } = 3f;
    public float Edge { get; set; } = 1.5f;
    public float Boost { get; set; } = 1.2f;
    public float Brightness { get; set; } = 1f;
    public float Crop { get; set; } = 0f;
    /// <summary>Only drive the lights while a fullscreen window is on the captured monitor.</summary>
    public bool FullscreenOnly { get; set; } = false;
    public bool PauseWhenLocked { get; set; } = true;

    public HomeAssistantSettings HomeAssistant { get; set; } = new();
    public WledSettings Wled { get; set; } = new();

    public AnalyzerOptions ToAnalyzerOptions() => new()
    {
        SaturationWeight = Saturation, EdgeWeight = Edge, Boost = Boost, Brightness = Brightness, Crop = Crop,
    };

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BgColour", "config.json");

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static Settings Load(string path)
    {
        if (!File.Exists(path))
        {
            var s = new Settings();
            s.Save(path);
            return s;
        }
        return JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Json) ?? new Settings();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }

    public Settings Clone() => JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(this, Json), Json)!;
}

sealed class HomeAssistantSettings
{
    public bool Enabled { get; set; }
    public string Url { get; set; } = "";
    public string Entity { get; set; } = "";
    /// <summary>Optional; falls back to the HA_TOKEN environment variable.</summary>
    public string? Token { get; set; }
    public int IntervalMs { get; set; } = 1000;
    /// <summary>Snapshot the light before taking over and restore it on pause/exit.</summary>
    public bool RestoreOnStop { get; set; } = true;

    public bool IsConfigured => Url.Length > 0 && Entity.Length > 0;
}

sealed class WledSettings
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; } = 21324;
    /// <summary>0 = ask the device.</summary>
    public int Leds { get; set; } = 0;

    public bool IsConfigured => Host.Length > 0;
}
