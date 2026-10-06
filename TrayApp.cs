using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace BgColour;

/// <summary>Tray icon that shows the live colour, plus a right-click menu for common settings.</summary>
sealed class TrayApp : ApplicationContext
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "BgColour";

    readonly string _configPath;
    Settings _settings;
    readonly Engine _engine;
    readonly NotifyIcon _tray;
    readonly System.Windows.Forms.Timer _uiTimer;
    readonly FileSystemWatcher _watcher;
    readonly SynchronizationContext _ui;
    string _lastWrittenConfig = "";
    long _reloadAtTicks;

    (byte, byte, byte) _shownColour = (1, 2, 3);
    EngineState _shownState = (EngineState)(-1);
    readonly HashSet<string> _reportedErrors = [];
    readonly List<(EventWaitHandle Handle, RegisteredWaitHandle Wait)> _commands = [];

    static readonly string[] Commands = ["toggle", "pause", "resume", "exit"];
    static string CommandEventName(string command) => $@"Local\BgColour.{command}";

    /// <summary>Signals the already-running instance. Returns false if none is running.</summary>
    public static bool SignalRunningInstance(string command)
    {
        if (!EventWaitHandle.TryOpenExisting(CommandEventName(command), out var handle)) return false;
        using (handle) handle.Set();
        return true;
    }

    public TrayApp(string configPath, Settings settings, bool console, bool debug)
    {
        _ui = new WindowsFormsSynchronizationContext();
        _configPath = configPath;
        _settings = settings;
        _lastWrittenConfig = SafeRead(configPath);
        _engine = new Engine(settings, console, debug);

        _tray = new NotifyIcon { Text = "BgColour", Visible = true, ContextMenuStrip = new ContextMenuStrip() };
        _tray.ContextMenuStrip.Opening += (_, _) => BuildMenu(_tray.ContextMenuStrip);
        BuildMenu(_tray.ContextMenuStrip);
        _tray.DoubleClick += (_, _) => TogglePause();
        UpdateIcon(force: true);

        _uiTimer = new System.Windows.Forms.Timer { Interval = 250 };
        _uiTimer.Tick += (_, _) => OnUiTick();
        _uiTimer.Start();

        _watcher = new FileSystemWatcher(Path.GetDirectoryName(configPath)!, Path.GetFileName(configPath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += (_, _) => Interlocked.Exchange(ref _reloadAtTicks, DateTime.UtcNow.AddMilliseconds(300).Ticks);
        _watcher.Created += (_, _) => Interlocked.Exchange(ref _reloadAtTicks, DateTime.UtcNow.AddMilliseconds(300).Ticks);
        _watcher.Renamed += (_, _) => Interlocked.Exchange(ref _reloadAtTicks, DateTime.UtcNow.AddMilliseconds(300).Ticks);

        foreach (var command in Commands)
        {
            var handle = new EventWaitHandle(false, EventResetMode.AutoReset, CommandEventName(command));
            var wait = ThreadPool.RegisterWaitForSingleObject(handle, (_, _) => _ui.Post(_ => OnCommand(command), null),
                null, Timeout.Infinite, executeOnlyOnce: false);
            _commands.Add((handle, wait));
        }

        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.SessionEnded += OnSessionEnded;
    }

    /// <summary>Thread-safe exit (used by Ctrl+C in console mode).</summary>
    public void RequestExit() => _ui.Post(_ => ExitThread(), null);

    void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect)
            _engine.Locked = true;
        else if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect)
            _engine.Locked = false;
    }

    void OnSessionEnded(object? sender, SessionEndedEventArgs e) => RequestExit();

    void OnCommand(string command)
    {
        switch (command)
        {
            case "toggle": TogglePause(); break;
            case "pause": _engine.Paused = true; UpdateIcon(force: true); break;
            case "resume": _engine.Paused = false; UpdateIcon(force: true); break;
            case "exit": ExitThread(); break;
        }
    }

    // ---------------------------------------------------------------- periodic UI work

    void OnUiTick()
    {
        long reloadAt = Interlocked.Read(ref _reloadAtTicks);
        if (reloadAt != 0 && DateTime.UtcNow.Ticks >= reloadAt)
        {
            Interlocked.Exchange(ref _reloadAtTicks, 0);
            ReloadConfig();
        }

        UpdateIcon(force: false);

        foreach (var (name, error) in _engine.Outputs)
        {
            if (error is null) continue;
            string key = name + error;
            if (_reportedErrors.Add(key))
                _tray.ShowBalloonTip(5000, $"BgColour – {name}", error, ToolTipIcon.Warning);
        }
        if (_engine.Outputs.All(o => o.Error is null)) _reportedErrors.Clear();
    }

    void ReloadConfig()
    {
        string text = SafeRead(_configPath);
        if (text.Length == 0 || text == _lastWrittenConfig) return;
        try
        {
            _settings = Settings.Load(_configPath);
            _lastWrittenConfig = text;
            _engine.Apply(_settings);
            _tray.ShowBalloonTip(2000, "BgColour", "Config reloaded", ToolTipIcon.Info);
        }
        catch (Exception e)
        {
            _tray.ShowBalloonTip(5000, "BgColour – config error", e.Message, ToolTipIcon.Error);
        }
    }

    void UpdateIcon(bool force)
    {
        var colour = _engine.Current;
        var state = _engine.State;
        int diff = Math.Abs(colour.R - _shownColour.Item1) + Math.Abs(colour.G - _shownColour.Item2) + Math.Abs(colour.B - _shownColour.Item3);
        if (!force && state == _shownState && diff < 4) return;
        _shownColour = colour;
        _shownState = state;

        var old = _tray.Icon;
        _tray.Icon = MakeIcon(Color.FromArgb(colour.R, colour.G, colour.B), state);
        old?.Dispose();

        string text = $"BgColour – {Describe(state)}";
        if (state == EngineState.Running) text += $" #{colour.R:X2}{colour.G:X2}{colour.B:X2}";
        foreach (var (name, error) in _engine.Outputs)
            text += $"\n{name}: {(error is null ? "OK" : "error")}";
        _tray.Text = text.Length > 127 ? text[..127] : text;
    }

    static string Describe(EngineState s) => s switch
    {
        EngineState.Running => "running",
        EngineState.Paused => "paused",
        EngineState.Locked => "paused (PC locked)",
        EngineState.WaitingForFullscreen => "waiting for a fullscreen app",
        EngineState.NoCapture => "can't capture screen",
        _ => "starting",
    };

    static Icon MakeIcon(Color colour, EngineState state)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            bool active = state == EngineState.Running;
            var fill = active ? colour : Color.FromArgb(90, 90, 90);
            using (var brush = new SolidBrush(fill)) g.FillEllipse(brush, 2, 2, 27, 27);
            using (var pen = new Pen(Color.FromArgb(200, 200, 200), 2f)) g.DrawEllipse(pen, 2, 2, 27, 27);
            if (!active)
            {
                using var bars = new SolidBrush(Color.FromArgb(230, 230, 230));
                if (state == EngineState.WaitingForFullscreen)
                {
                    // Three dots: standing by.
                    for (int i = 0; i < 3; i++) g.FillEllipse(bars, 8 + i * 6, 14, 4, 4);
                }
                else
                {
                    g.FillRectangle(bars, 11, 9, 4, 14);
                    g.FillRectangle(bars, 17, 9, 4, 14);
                }
            }
        }
        nint h = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(h).Clone(); }
        finally { DestroyIcon(h); }
    }

    // ---------------------------------------------------------------- menu

    void BuildMenu(ContextMenuStrip menu)
    {
        menu.Items.Clear();
        var c = _engine.Current;
        var state = _engine.State;

        var header = new ToolStripMenuItem(state == EngineState.Running
            ? $"BgColour – #{c.R:X2}{c.G:X2}{c.B:X2}"
            : $"BgColour – {Describe(state)}") { Enabled = false };
        if (state == EngineState.Running) header.Image = Swatch(Color.FromArgb(c.R, c.G, c.B));
        menu.Items.Add(header);
        foreach (var (name, error) in _engine.Outputs.Where(o => o.Error is not null))
            menu.Items.Add(new ToolStripMenuItem($"⚠ {name}: {Trim(error!, 60)}") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());

        var pause = new ToolStripMenuItem(_engine.Paused ? "Resume" : "Pause", null, (_, _) => TogglePause())
        { Font = new Font(menu.Font, FontStyle.Bold) };
        menu.Items.Add(pause);
        menu.Items.Add(Check("Only while a fullscreen app is running", _settings.FullscreenOnly,
            () => Change(s => s.FullscreenOnly = !s.FullscreenOnly)));
        menu.Items.Add(new ToolStripSeparator());

        var outputs = new ToolStripMenuItem("Outputs");
        var ha = _settings.HomeAssistant;
        outputs.DropDownItems.Add(Check(ha.IsConfigured ? $"Home Assistant ({ha.Entity})" : "Home Assistant (not configured)",
            ha.Enabled, () => Change(s => s.HomeAssistant.Enabled = !s.HomeAssistant.Enabled), ha.IsConfigured));
        outputs.DropDownItems.Add(Check("    Restore light when stopped",
            ha.RestoreOnStop, () => Change(s => s.HomeAssistant.RestoreOnStop = !s.HomeAssistant.RestoreOnStop), ha.IsConfigured));
        var wled = _settings.Wled;
        outputs.DropDownItems.Add(Check(wled.IsConfigured ? $"WLED ({wled.Host})" : "WLED (not configured)",
            wled.Enabled, () => Change(s => s.Wled.Enabled = !s.Wled.Enabled), wled.IsConfigured));
        menu.Items.Add(outputs);

        var monitors = new ToolStripMenuItem("Monitor");
        var list = ScreenSampler.ListMonitors();
        for (int i = 0; i < list.Count; i++)
        {
            int index = i;
            monitors.DropDownItems.Add(Check($"{i}: {list[i]}", _settings.Monitor == i, () => Change(s => s.Monitor = index)));
        }
        menu.Items.Add(monitors);

        menu.Items.Add(Choice("Brightness", _settings.Brightness, (s, v) => s.Brightness = v,
            ("25 %", 0.25f), ("50 %", 0.5f), ("75 %", 0.75f), ("100 %", 1f), ("150 % (lift dark scenes)", 1.5f)));
        menu.Items.Add(Choice("Smoothing", (float)_settings.Smooth, (s, v) => s.Smooth = v,
            ("Instant", 0f), ("Snappy", 0.05f), ("Normal", 0.15f), ("Relaxed", 0.4f), ("Slow", 1f)));
        menu.Items.Add(Choice("Vividness", _settings.Boost, (s, v) =>
            {
                s.Boost = v;
                s.Saturation = v switch { <= 1f => 1f, <= 1.2f => 3f, _ => 5f };
            },
            ("Natural (plain average)", 1f), ("Normal", 1.2f), ("Vivid", 1.5f)));
        menu.Items.Add(Choice("Update rate", _settings.Fps, (s, v) => s.Fps = (int)v,
            ("10 fps", 10), ("20 fps", 20), ("30 fps", 30), ("60 fps", 60)));
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(new ToolStripMenuItem("Edit config…", null, (_, _) => OpenConfig()));
        menu.Items.Add(Check("Start with Windows", IsStartupEnabled(), ToggleStartup));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitThread()));
    }

    static ToolStripMenuItem Check(string text, bool isChecked, Action onClick, bool enabled = true) =>
        new(text, null, (_, _) => onClick()) { Checked = isChecked, Enabled = enabled };

    ToolStripMenuItem Choice(string text, float current, Action<Settings, float> set, params (string Label, float Value)[] options)
    {
        var item = new ToolStripMenuItem(text);
        foreach (var (label, value) in options)
            item.DropDownItems.Add(Check(label, Math.Abs(current - value) < 0.001f, () => Change(s => set(s, value))));
        return item;
    }

    static Bitmap Swatch(Color c)
    {
        var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        using var brush = new SolidBrush(c);
        g.FillRectangle(brush, 1, 1, 14, 14);
        g.DrawRectangle(Pens.Gray, 1, 1, 13, 13);
        return bmp;
    }

    static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    void TogglePause()
    {
        _engine.Paused = !_engine.Paused;
        UpdateIcon(force: true);
    }

    void Change(Action<Settings> change)
    {
        change(_settings);
        try
        {
            _settings.Save(_configPath);
            _lastWrittenConfig = SafeRead(_configPath);
        }
        catch (Exception e)
        {
            _tray.ShowBalloonTip(5000, "BgColour", $"Couldn't save config: {e.Message}", ToolTipIcon.Error);
        }
        _engine.Apply(_settings);
    }

    void OpenConfig()
    {
        try { System.Diagnostics.Process.Start("notepad.exe", $"\"{_configPath}\""); }
        catch (Exception e) { _tray.ShowBalloonTip(5000, "BgColour", e.Message, ToolTipIcon.Error); }
    }

    static string StartupCommand => $"\"{Environment.ProcessPath}\"";

    static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) is string v && string.Equals(v, StartupCommand, StringComparison.OrdinalIgnoreCase);
    }

    static void ToggleStartup()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (IsStartupEnabled()) key.DeleteValue(RunValue, false);
        else key.SetValue(RunValue, StartupCommand);
    }

    static string SafeRead(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : ""; }
        catch (IOException) { return ""; }
    }

    protected override void ExitThreadCore()
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.SessionEnded -= OnSessionEnded;
        _uiTimer.Stop();
        foreach (var (handle, wait) in _commands) { wait.Unregister(null); handle.Dispose(); }
        _commands.Clear();
        _watcher.Dispose();
        _tray.Visible = false;
        _engine.Dispose(); // restores the light
        _tray.Dispose();
        base.ExitThreadCore();
    }

    [DllImport("user32.dll")] static extern bool DestroyIcon(nint handle);
}
