using System.Diagnostics;

namespace BgColour;

enum EngineState { Starting, Running, Paused, Locked, WaitingForFullscreen, NoCapture }

/// <summary>
/// Runs capture → analyse → smooth → sinks on a low-priority background thread.
/// The UI only flips flags / hands over new settings and polls the public state.
/// </summary>
sealed class Engine : IDisposable
{
    readonly Thread _thread;
    readonly bool _console, _debug;
    volatile bool _stop;
    volatile bool _paused, _locked;
    Settings? _pending;

    public Engine(Settings settings, bool console, bool debug)
    {
        _pending = settings.Clone();
        _console = console;
        _debug = debug;
        _thread = new Thread(Run) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "BgColour engine" };
        _thread.Start();
    }

    public bool Paused { get => _paused; set => _paused = value; }
    public bool Locked { get => _locked; set => _locked = value; }
    public EngineState State { get; private set; } = EngineState.Starting;
    public (byte R, byte G, byte B) Current { get; private set; }
    /// <summary>Output names with their last error (null = OK).</summary>
    public IReadOnlyList<(string Name, string? Error)> Outputs { get; private set; } = [];

    public void Apply(Settings settings) => Volatile.Write(ref _pending, settings.Clone());

    void Run()
    {
        Settings s = null!;
        AnalyzerOptions opts = null!;
        List<IColorSink> sinks = [];
        List<string> setupErrors = [];
        ScreenSampler? sampler = null;
        bool ready = false, driving = false, fullscreen = false;
        string? monitorName = null;
        var clock = Stopwatch.StartNew();
        double lastTick = 0, lastFullscreenCheck = -10_000;
        Rgb target = default, current = default;

        void StopDriving()
        {
            if (!driving) return;
            foreach (var sink in sinks) sink.Stop();
            driving = false;
        }

        void ReleaseSampler()
        {
            sampler?.Dispose();
            sampler = null;
            ready = false;
        }

        try
        {
            while (!_stop)
            {
                var pending = Interlocked.Exchange(ref _pending, null);
                if (pending is not null)
                {
                    StopDriving();
                    foreach (var sink in sinks) sink.Dispose();
                    if (s is null || pending.Monitor != s.Monitor || pending.SampleWidth != s.SampleWidth)
                    {
                        ReleaseSampler();
                        monitorName = ScreenSampler.DeviceName(pending.Monitor);
                        lastFullscreenCheck = -10_000;
                    }
                    s = pending;
                    opts = s.ToAnalyzerOptions();
                    (sinks, setupErrors) = BuildSinks(s);
                }

                Outputs = [.. setupErrors.Select(e => ("Setup", (string?)e)), .. sinks.Where(k => k is not ConsoleSink).Select(k => (k.Name, k.Error))];

                // Decide whether we should be driving the lights right now.
                EngineState state = EngineState.Running;
                if (_paused) state = EngineState.Paused;
                else if (_locked && s.PauseWhenLocked) state = EngineState.Locked;
                else if (s.FullscreenOnly)
                {
                    if (clock.ElapsedMilliseconds - lastFullscreenCheck > 1000)
                    {
                        fullscreen = monitorName is not null && DisplayInfo.IsFullscreenWindowOn(monitorName);
                        lastFullscreenCheck = clock.ElapsedMilliseconds;
                    }
                    if (!fullscreen) state = EngineState.WaitingForFullscreen;
                }

                if (state != EngineState.Running)
                {
                    State = state;
                    StopDriving();
                    ReleaseSampler(); // frees the GPU resources while idle
                    current = target = default;
                    Current = default;
                    Thread.Sleep(250);
                    continue;
                }

                if (!ready)
                {
                    sampler ??= new ScreenSampler(s.Monitor, s.SampleWidth);
                    try { ready = sampler.Initialize(); }
                    catch (ArgumentException) { ready = false; }
                    if (!ready)
                    {
                        State = EngineState.NoCapture;
                        Thread.Sleep(1000);
                        continue;
                    }
                }

                if (!driving)
                {
                    foreach (var sink in sinks) sink.Start();
                    driving = true;
                }
                State = EngineState.Running;

                int intervalMs = 1000 / Math.Clamp(s.Fps, 1, 240);
                double tickStart = clock.Elapsed.TotalMilliseconds;

                // Blocks until the desktop changes or the interval passes; static screens cost ~nothing.
                switch (sampler!.Grab((uint)intervalMs))
                {
                    case ScreenSampler.GrabResult.NewFrame:
                        target = ColorAnalyzer.Analyze(sampler.Pixels, sampler.SampleWidth, sampler.SampleHeight, opts);
                        if (_debug) DebugPrint(sampler, target);
                        break;
                    case ScreenSampler.GrabResult.Lost:
                        ready = false;   // mode switch, exclusive fullscreen, UAC prompt...
                        Thread.Sleep(250);
                        continue;
                }

                double now = clock.Elapsed.TotalMilliseconds;
                double dt = Math.Min(now - lastTick, 1000) / 1000.0;
                lastTick = now;
                float alpha = s.Smooth <= 0 ? 1f : (float)(1 - Math.Exp(-dt / s.Smooth));
                current = Rgb.Lerp(current, target, alpha);

                var (r, g, b) = current.ToBytes();
                Current = (r, g, b);
                foreach (var sink in sinks) sink.Send(r, g, b);

                // Cap the capture rate.
                int remaining = intervalMs - (int)(clock.Elapsed.TotalMilliseconds - tickStart);
                if (remaining > 0) Thread.Sleep(remaining);
            }
        }
        finally
        {
            StopDriving();
            foreach (var sink in sinks) sink.Dispose();
            sampler?.Dispose();
        }
    }

    (List<IColorSink>, List<string>) BuildSinks(Settings s)
    {
        var sinks = new List<IColorSink>();
        var errors = new List<string>();
        if (_console) sinks.Add(new ConsoleSink());

        var ha = s.HomeAssistant;
        if (ha.Enabled && ha.IsConfigured)
        {
            var token = !string.IsNullOrEmpty(ha.Token) ? ha.Token
                : Environment.GetEnvironmentVariable("HA_TOKEN")
                  ?? Environment.GetEnvironmentVariable("HA_TOKEN", EnvironmentVariableTarget.User);
            if (string.IsNullOrEmpty(token)) errors.Add("Home Assistant: no token (set HA_TOKEN or homeAssistant.token)");
            else sinks.Add(new HomeAssistantSink(ha.Url, token, ha.Entity, Math.Max(50, ha.IntervalMs), ha.RestoreOnStop));
        }

        var wled = s.Wled;
        if (wled.Enabled && wled.IsConfigured)
        {
            try { sinks.Add(new WledSink(wled.Host, wled.Port, wled.Leds)); }
            catch (Exception e) { errors.Add($"WLED: {e.Message}"); }
        }
        return (sinks, errors);
    }

    static void DebugPrint(ScreenSampler sampler, Rgb target)
    {
        var px = sampler.Pixels;
        long db = 0, dg = 0, dr = 0;
        for (int k = 0; k < px.Length; k += 4) { db += px[k]; dg += px[k + 1]; dr += px[k + 2]; }
        int n = px.Length / 4;
        Console.Error.WriteLine($"[{sampler.SampleWidth}x{sampler.SampleHeight} hdr={sampler.IsHdr} white={sampler.SdrWhiteNits:0}nits] raw mean rgb({dr / n},{dg / n},{db / n}) -> {target}");
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(TimeSpan.FromSeconds(15)); // lets Home Assistant restore the light
    }
}
