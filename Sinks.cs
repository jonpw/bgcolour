using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;

namespace BgColour;

/// <summary>Receives the smoothed colour every tick; each sink decides how often to actually send.</summary>
interface IColorSink : IDisposable
{
    string Name { get; }
    /// <summary>Null when healthy, otherwise the last error.</summary>
    string? Error { get; }
    /// <summary>Called when the engine starts driving the lights.</summary>
    void Start() { }
    void Send(byte r, byte g, byte b);
    /// <summary>Called when the engine stops (pause, lock, exit); may block briefly.</summary>
    void Stop() { }
}

/// <summary>Prints a live true-colour swatch on one console line.</summary>
sealed class ConsoleSink : IColorSink
{
    readonly Stopwatch _clock = Stopwatch.StartNew();
    long _lastMs = -1000;
    (byte, byte, byte) _last = (1, 2, 3);

    public string Name => "Console";
    public string? Error => null;

    public void Send(byte r, byte g, byte b)
    {
        if (_last == (r, g, b) || _clock.ElapsedMilliseconds - _lastMs < 100) return;
        _last = (r, g, b);
        _lastMs = _clock.ElapsedMilliseconds;
        Console.Write($"\r\x1b[48;2;{r};{g};{b}m          \x1b[0m  #{r:X2}{g:X2}{b:X2}  rgb({r,3},{g,3},{b,3})   ");
    }

    public void Dispose() => Console.WriteLine();
}

/// <summary>
/// WLED realtime UDP (DNRGB protocol, port 21324). WLED drops back to its normal
/// effect a couple of seconds after packets stop, so we resend periodically.
/// </summary>
sealed class WledSink : IColorSink
{
    const byte TimeoutSeconds = 2;
    const int MaxLedsPerPacket = 489;

    readonly UdpClient _udp = new();
    readonly string _host;
    int _ledCount;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    byte[] _packet = [];
    long _lastMs = -10_000;
    (byte, byte, byte) _last;

    public string Name => "WLED";
    public string? Error { get; private set; }

    public WledSink(string host, int port, int ledCount)
    {
        _host = host;
        _ledCount = ledCount;
        _udp.Connect(host, port);
    }

    public void Start()
    {
        if (_ledCount > 0) return;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var doc = JsonDocument.Parse(http.GetStringAsync($"http://{_host}/json/info").GetAwaiter().GetResult());
            _ledCount = doc.RootElement.GetProperty("leds").GetProperty("count").GetInt32();
            Error = null;
        }
        catch (Exception e) { Error = $"can't read LED count: {e.Message}"; }
    }

    public void Send(byte r, byte g, byte b)
    {
        if (_ledCount <= 0) return;
        long now = _clock.ElapsedMilliseconds;
        if (_last == (r, g, b) && now - _lastMs < 1000) return;
        _last = (r, g, b);
        _lastMs = now;

        if (_packet.Length == 0) _packet = new byte[4 + Math.Min(_ledCount, MaxLedsPerPacket) * 3];
        for (int start = 0; start < _ledCount; start += MaxLedsPerPacket)
        {
            int n = Math.Min(MaxLedsPerPacket, _ledCount - start);
            _packet[0] = 4; // DNRGB
            _packet[1] = TimeoutSeconds;
            _packet[2] = (byte)(start >> 8);
            _packet[3] = (byte)start;
            for (int i = 0; i < n; i++)
            {
                _packet[4 + i * 3] = r;
                _packet[5 + i * 3] = g;
                _packet[6 + i * 3] = b;
            }
            try { _udp.Send(_packet, 4 + n * 3); Error = null; }
            catch (SocketException e) { Error = e.Message; }
        }
    }

    public void Dispose() => _udp.Dispose();
}

/// <summary>
/// Home Assistant light.turn_on via REST. HA isn't built for high update rates, so this
/// sends at most every <c>minIntervalMs</c>, only on visible change, and never overlaps requests.
/// Optionally snapshots the light into a scene first and restores it on Stop.
/// </summary>
sealed class HomeAssistantSink : IColorSink
{
    readonly HttpClient _http;
    readonly string _entity;
    readonly string _sceneId;
    readonly int _minIntervalMs;
    readonly bool _restore;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    long _lastMs = -10_000;
    (int R, int G, int B) _last = (-100, -100, -100);
    Task _pending = Task.CompletedTask;
    bool _snapshotted;

    public string Name => "Home Assistant";
    public string? Error { get; private set; }

    public HomeAssistantSink(string baseUrl, string token, string entity, int minIntervalMs, bool restore)
    {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(5) };
        _http.DefaultRequestHeaders.Authorization = new("Bearer", token);
        _entity = entity;
        _sceneId = "bgcolour_restore_" + new string(entity.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        _minIntervalMs = minIntervalMs;
        _restore = restore;
    }

    public void Start()
    {
        _last = (-100, -100, -100);
        if (!_restore) return;
        _snapshotted = Call("api/services/scene/create", new { scene_id = _sceneId, snapshot_entities = new[] { _entity } })
            .GetAwaiter().GetResult();
    }

    public void Send(byte r, byte g, byte b)
    {
        long now = _clock.ElapsedMilliseconds;
        if (now - _lastMs < _minIntervalMs || !_pending.IsCompleted) return;
        if (Math.Abs(r - _last.R) + Math.Abs(g - _last.G) + Math.Abs(b - _last.B) < 6) return;
        _last = (r, g, b);
        _lastMs = now;

        // HA wants hue in rgb_color and intensity in brightness.
        int peak = Math.Max(r, Math.Max(g, b));
        object body = peak == 0
            ? new { entity_id = _entity, brightness = 0 }
            : new
            {
                entity_id = _entity,
                rgb_color = new[] { r * 255 / peak, g * 255 / peak, b * 255 / peak },
                brightness = peak,
                transition = _minIntervalMs / 1000.0,
            };
        _pending = Call("api/services/light/turn_on", body);
    }

    public void Stop()
    {
        _pending.Wait(TimeSpan.FromSeconds(6));
        if (_snapshotted)
            Call("api/services/scene/turn_on", new { entity_id = "scene." + _sceneId, transition = 1 }).Wait(TimeSpan.FromSeconds(6));
        _snapshotted = false;
    }

    async Task<bool> Call(string path, object body)
    {
        try
        {
            using var resp = await _http.PostAsJsonAsync(path, body);
            Error = resp.IsSuccessStatusCode ? null : $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}";
            return resp.IsSuccessStatusCode;
        }
        catch (Exception e)
        {
            Error = e.Message;
            return false;
        }
    }

    public void Dispose() => _http.Dispose();
}
