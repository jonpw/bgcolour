using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace BgColour;

/// <summary>
/// Captures a monitor with DXGI Desktop Duplication and shrinks it on the GPU
/// (via mipmap generation) so only a tiny thumbnail is ever copied back to the CPU.
/// </summary>
sealed class ScreenSampler : IDisposable
{
    readonly int _monitorIndex;
    readonly int _targetWidth;

    ID3D11Device? _device;
    ID3D11DeviceContext? _context;
    IDXGIOutputDuplication? _duplication;
    ID3D11Texture2D? _mipTexture;
    ID3D11ShaderResourceView? _mipView;
    ID3D11Texture2D? _staging;
    int _mipLevel;
    float _sdrWhite = 1f;
    byte[] _row = [];
    byte[] _pixels = [];

    public int SampleWidth { get; private set; }
    public int SampleHeight { get; private set; }
    public string MonitorName { get; private set; } = "";

    public ScreenSampler(int monitorIndex, int targetWidth)
    {
        _monitorIndex = monitorIndex;
        _targetWidth = targetWidth;
    }

    record struct MonitorInfo(uint Adapter, uint Output, string DeviceName, string Name);

    static List<MonitorInfo> EnumerateOutputs()
    {
        var list = new List<MonitorInfo>();
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            using (adapter)
            {
                for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
                {
                    using (output)
                    {
                        var d = output.Description;
                        var r = d.DesktopCoordinates;
                        list.Add(new(a, o, d.DeviceName, $"{d.DeviceName}  {r.Right - r.Left}x{r.Bottom - r.Top}  on {adapter.Description1.Description}"));
                    }
                }
            }
        }
        return list;
    }

    public static List<string> ListMonitors() => EnumerateOutputs().Select(m => m.Name).ToList();

    public static string? DeviceName(int index)
    {
        var all = EnumerateOutputs();
        return index >= 0 && index < all.Count ? all[index].DeviceName : null;
    }

    /// <summary>(Re)creates the device and duplication. Returns false if it cannot right now
    /// (e.g. secure desktop / UAC prompt, or mode change in progress).</summary>
    public bool Initialize()
    {
        ReleaseAll();

        var monitors = EnumerateOutputs();
        if (_monitorIndex < 0 || _monitorIndex >= monitors.Count)
            throw new ArgumentException($"Monitor {_monitorIndex} not found. Use --list.");
        var m = monitors[_monitorIndex];

        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        factory.EnumAdapters1(m.Adapter, out var adapter).CheckError();
        adapter.EnumOutputs(m.Output, out var output).CheckError();
        try
        {
            MonitorName = output.Description.DeviceName;
            D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.None,
                [FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0],
                out _device, out _context).CheckError();

            // DuplicateOutput1 gives us the real FP16 scRGB frame when HDR is on; the legacy
            // call would hand back a clipped 8-bit image with blown-out colours.
            using var output5 = output.QueryInterfaceOrNull<IDXGIOutput5>();
            if (output5 is not null)
                _duplication = output5.DuplicateOutput1(_device, [Format.R16G16B16A16_Float, Format.B8G8R8A8_UNorm]);
            else
                using (var output1 = output.QueryInterface<IDXGIOutput1>()) _duplication = output1.DuplicateOutput(_device);

            _sdrWhite = DisplayInfo.GetSdrWhiteLevel(MonitorName);
            return true;
        }
        catch (SharpGen.Runtime.SharpGenException)
        {
            ReleaseAll();
            return false;
        }
        finally
        {
            output.Dispose();
            adapter.Dispose();
        }
    }

    public enum GrabResult { NewFrame, NoChange, Lost }

    /// <summary>
    /// Waits up to <paramref name="timeoutMs"/> for a new desktop image. On NewFrame the
    /// thumbnail (BGRA, tightly packed, SampleWidth x SampleHeight) is in <see cref="Pixels"/>.
    /// </summary>
    public GrabResult Grab(uint timeoutMs)
    {
        if (_duplication is null || _context is null) return GrabResult.Lost;

        var hr = _duplication.AcquireNextFrame(timeoutMs, out var info, out var resource);
        if (hr == Vortice.DXGI.ResultCode.WaitTimeout) return GrabResult.NoChange;
        if (hr.Failure) return GrabResult.Lost; // AccessLost: fullscreen switch, resolution change, etc.

        try
        {
            // LastPresentTime == 0 means only the mouse pointer moved.
            if (info.LastPresentTime == 0) return GrabResult.NoChange;

            using var frame = resource.QueryInterface<ID3D11Texture2D>();
            EnsureResources(frame.Description);
            _context.CopySubresourceRegion(_mipTexture!, 0, 0, 0, 0, frame, 0);
        }
        finally
        {
            resource.Dispose();
            _duplication.ReleaseFrame();
        }

        // Box-filter the whole chain on the GPU, then pull back only the small level.
        _context.GenerateMips(_mipView!);
        _context.CopySubresourceRegion(_staging!, 0, 0, 0, 0, _mipTexture!, (uint)_mipLevel);

        var map = _context.Map(_staging!, 0, MapMode.Read);
        try
        {
            int rowBytes = SampleWidth * 4;
            if (_mipTexture!.Description.Format == Format.R16G16B16A16_Float)
            {
                for (int y = 0; y < SampleHeight; y++)
                {
                    Marshal.Copy(map.DataPointer + (nint)(y * map.RowPitch), _row, 0, SampleWidth * 8);
                    var halves = MemoryMarshal.Cast<byte, Half>(_row.AsSpan(0, SampleWidth * 8));
                    for (int x = 0; x < SampleWidth; x++)
                    {
                        int o = (y * SampleWidth + x) * 4;
                        _pixels[o + 2] = LinearToSrgbByte((float)halves[x * 4] / _sdrWhite);
                        _pixels[o + 1] = LinearToSrgbByte((float)halves[x * 4 + 1] / _sdrWhite);
                        _pixels[o] = LinearToSrgbByte((float)halves[x * 4 + 2] / _sdrWhite);
                    }
                }
            }
            else
            {
                for (int y = 0; y < SampleHeight; y++)
                    Marshal.Copy(map.DataPointer + (nint)(y * map.RowPitch), _pixels, y * rowBytes, rowBytes);
            }
        }
        finally
        {
            _context.Unmap(_staging!, 0);
        }
        return GrabResult.NewFrame;
    }

    public ReadOnlySpan<byte> Pixels => _pixels;
    public bool IsHdr => _mipTexture?.Description.Format == Format.R16G16B16A16_Float;
    public float SdrWhiteNits => _sdrWhite * 80f;

    /// <summary>scRGB is linear BT.709, so (after scaling SDR white to 1) only the sRGB curve is needed.
    /// HDR highlights brighter than SDR white clip to full.</summary>
    static byte LinearToSrgbByte(float v)
    {
        if (!(v > 0)) return 0;
        if (v >= 1) return 255;
        float s = v <= 0.0031308f ? v * 12.92f : 1.055f * MathF.Pow(v, 1 / 2.4f) - 0.055f;
        return (byte)(s * 255f + 0.5f);
    }

    void EnsureResources(Texture2DDescription src)
    {
        if (_mipTexture is not null)
        {
            var cur = _mipTexture.Description;
            if (cur.Width == src.Width && cur.Height == src.Height && cur.Format == src.Format) return;
        }
        ReleaseTextures();

        _mipTexture = _device!.CreateTexture2D(new Texture2DDescription
        {
            Width = src.Width,
            Height = src.Height,
            MipLevels = 0, // full chain
            ArraySize = 1,
            Format = src.Format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            MiscFlags = ResourceOptionFlags.GenerateMips,
        });
        _mipView = _device.CreateShaderResourceView(_mipTexture);

        // Pick the first mip level whose width is <= target width.
        _mipLevel = 0;
        uint w = src.Width, h = src.Height;
        while (w > _targetWidth && w > 1 && h > 1) { w = Math.Max(1, w / 2); h = Math.Max(1, h / 2); _mipLevel++; }
        SampleWidth = (int)w;
        SampleHeight = (int)h;
        _pixels = new byte[SampleWidth * SampleHeight * 4];
        _row = new byte[SampleWidth * 8];

        _staging = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = w,
            Height = h,
            MipLevels = 1,
            ArraySize = 1,
            Format = src.Format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
        });
    }

    void ReleaseTextures()
    {
        _staging?.Dispose(); _staging = null;
        _mipView?.Dispose(); _mipView = null;
        _mipTexture?.Dispose(); _mipTexture = null;
    }

    void ReleaseAll()
    {
        ReleaseTextures();
        _duplication?.Dispose(); _duplication = null;
        _context?.Dispose(); _context = null;
        _device?.Dispose(); _device = null;
    }

    public void Dispose() => ReleaseAll();
}
