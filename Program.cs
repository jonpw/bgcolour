using System.Diagnostics;

namespace BgColour;

static class Program
{
    const string Usage = """
    BgColour - ambient screen colour for LED strips (runs in the system tray)

      --config PATH   Config file (default %APPDATA%\BgColour\config.json)
      --console       Open a console showing the live colour
      --debug         With --console: print raw capture stats (HDR state, SDR white level)
      --list          List monitors and exit

    Control a running instance (e.g. from a hotkey tool or Stream Deck):
      --toggle | --pause | --resume | --exit
    """;

    [STAThread]
    static int Main(string[] args)
    {
        // Must come first: DuplicateOutput1 (HDR capture) needs per-monitor-v2 DPI awareness.
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        bool console = false, debug = false, list = false;
        string configPath = Settings.DefaultPath;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--console": console = true; break;
                case "--debug": debug = console = true; break;
                case "--list": list = true; break;
                case "--toggle" or "--pause" or "--resume" or "--exit":
                    return TrayApp.SignalRunningInstance(args[i][2..].ToLowerInvariant()) ? 0 : 2;
                case "--config" when i + 1 < args.Length: configPath = Path.GetFullPath(args[++i]); break;
                default:
                    MessageBox.Show(Usage, "BgColour", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 1;
            }
        }

        if (console || list) DisplayInfo.AttachDebugConsole();
        if (list)
        {
            var monitors = ScreenSampler.ListMonitors();
            for (int m = 0; m < monitors.Count; m++) Console.WriteLine($"{m}: {monitors[m]}");
            Console.WriteLine("Press Enter to close.");
            Console.ReadLine();
            return 0;
        }

        using var mutex = new Mutex(true, @"Local\BgColour.SingleInstance", out bool first);
        if (!first)
        {
            MessageBox.Show("BgColour is already running – look for it in the system tray.", "BgColour",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        Settings settings;
        try { settings = Settings.Load(configPath); }
        catch (Exception e)
        {
            MessageBox.Show($"Couldn't read {configPath}:\n\n{e.Message}", "BgColour", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        // Stay out of the game's way.
        using (var self = Process.GetCurrentProcess()) self.PriorityClass = ProcessPriorityClass.BelowNormal;

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using var app = new TrayApp(configPath, settings, console, debug);
        if (console)
        {
            Console.WriteLine($"Config: {configPath}\nCtrl+C or tray menu → Exit to stop.");
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; app.RequestExit(); };
        }
        Application.Run(app);
        return 0;
    }
}
