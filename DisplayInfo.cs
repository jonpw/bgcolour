using System.Runtime.InteropServices;

namespace BgColour;

/// <summary>Win32 display queries needed for HDR capture.</summary>
static class DisplayInfo
{
    /// <summary>
    /// True when the foreground window covers the whole of the given monitor (a fullscreen or
    /// borderless game, a fullscreen video...). The desktop itself doesn't count.
    /// </summary>
    public static bool IsFullscreenWindowOn(string gdiDeviceName)
    {
        nint hwnd = GetForegroundWindow();
        if (hwnd == 0 || hwnd == GetShellWindow() || hwnd == GetDesktopWindow()) return false;

        var cls = new char[64];
        int len = GetClassName(hwnd, cls, cls.Length);
        var name = new string(cls, 0, len);
        if (name is "WorkerW" or "Progman" or "Shell_TrayWnd") return false;

        if (!GetWindowRect(hwnd, out var r)) return false;
        var mi = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref mi)) return false;
        if (!string.Equals(mi.Device, gdiDeviceName, StringComparison.OrdinalIgnoreCase)) return false;

        var m = mi.Monitor;
        return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
    }

    /// <summary>Gives a WinExe a console for --console mode, with ANSI colours enabled.</summary>
    public static void AttachDebugConsole()
    {
        if (!AllocConsole()) return;
        nint handle = GetStdHandle(-11);
        if (GetConsoleMode(handle, out uint mode)) SetConsoleMode(handle, mode | 0x0004); // VIRTUAL_TERMINAL_PROCESSING
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MonitorInfoEx
    {
        public int Size;
        public Rect Monitor, Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }

    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern nint GetShellWindow();
    [DllImport("user32.dll")] static extern nint GetDesktopWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(nint hwnd, [Out] char[] name, int max);
    [DllImport("user32.dll")] static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(nint monitor, ref MonitorInfoEx info);
    [DllImport("kernel32.dll")] static extern bool AllocConsole();
    [DllImport("kernel32.dll")] static extern nint GetStdHandle(int which);
    [DllImport("kernel32.dll")] static extern bool GetConsoleMode(nint handle, out uint mode);
    [DllImport("kernel32.dll")] static extern bool SetConsoleMode(nint handle, uint mode);

    /// <summary>
    /// The Windows "SDR content brightness" for a GDI device name (e.g. \\.\DISPLAY1) as an
    /// scRGB multiplier (1.0 = 80 nits). Desktop/game white in an HDR capture sits at this value.
    /// </summary>
    public static float GetSdrWhiteLevel(string gdiDeviceName)
    {
        const uint QdcOnlyActivePaths = 2;
        if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out uint numPaths, out uint numModes) != 0) return 2.5f;
        var paths = new PathInfo[numPaths];
        var modes = new byte[numModes * 64];
        if (QueryDisplayConfig(QdcOnlyActivePaths, ref numPaths, paths, ref numModes, modes, 0) != 0) return 2.5f;

        foreach (var p in paths.AsSpan(0, (int)numPaths))
        {
            var src = new SourceDeviceName
            {
                Header = new() { Type = 1, Size = (uint)Marshal.SizeOf<SourceDeviceName>(), AdapterId = p.SourceAdapter, Id = p.SourceId },
            };
            if (DisplayConfigGetDeviceInfo(ref src) != 0 || src.GdiDeviceName != gdiDeviceName) continue;

            var white = new SdrWhiteLevel
            {
                Header = new() { Type = 11, Size = (uint)Marshal.SizeOf<SdrWhiteLevel>(), AdapterId = p.TargetAdapter, Id = p.TargetId },
            };
            if (DisplayConfigGetDeviceInfo(ref white) == 0 && white.Level > 0) return white.Level / 1000f;
        }
        return 2.5f; // 200 nits, a common default
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Luid { public uint Low; public int High; }

    [StructLayout(LayoutKind.Sequential)]
    struct PathInfo
    {
        public Luid SourceAdapter; public uint SourceId, SourceModeIdx, SourceFlags;
        public Luid TargetAdapter; public uint TargetId, TargetModeIdx, OutputTech, Rotation, Scaling,
            RefreshNum, RefreshDen, ScanLineOrdering, TargetAvailable, TargetFlags;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Header { public uint Type, Size; public Luid AdapterId; public uint Id; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SourceDeviceName
    {
        public Header Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string GdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SdrWhiteLevel { public Header Header; public uint Level; }

    [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);
    [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] PathInfo[] paths, ref uint numModes, [Out] byte[] modes, nint topologyId);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref SourceDeviceName info);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref SdrWhiteLevel info);}
