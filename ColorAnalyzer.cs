namespace BgColour;

record struct Rgb(float R, float G, float B)
{
    public static Rgb Lerp(Rgb a, Rgb b, float t) =>
        new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t);

    public (byte R, byte G, byte B) ToBytes() => (ToByte(R), ToByte(G), ToByte(B));
    static byte ToByte(float v) => (byte)Math.Clamp(MathF.Round(v * 255f), 0, 255);
}

sealed class AnalyzerOptions
{
    /// <summary>How much more saturated pixels count than grey ones (0 = plain average).</summary>
    public float SaturationWeight = 3f;
    /// <summary>Extra weight for pixels near the screen edge (0 = uniform).</summary>
    public float EdgeWeight = 1.5f;
    /// <summary>Saturation boost applied to the final colour (1 = none).</summary>
    public float Boost = 1.2f;
    /// <summary>Output brightness multiplier.</summary>
    public float Brightness = 1f;
    /// <summary>Ignore the outer fraction of each side (letterbox bars, taskbar...).</summary>
    public float Crop = 0f;
}

/// <summary>
/// Turns the GPU thumbnail into one colour. The hue comes from a weighted average that
/// favours bright, saturated, edge pixels (so a red explosion beats a grey HUD), while the
/// overall brightness follows the plain average so dark scenes stay dark.
/// </summary>
static class ColorAnalyzer
{
    public static Rgb Analyze(ReadOnlySpan<byte> bgra, int width, int height, AnalyzerOptions o)
    {
        int x0 = (int)(width * o.Crop), x1 = width - x0;
        int y0 = (int)(height * o.Crop), y1 = height - y0;
        if (x1 <= x0 || y1 <= y0) { x0 = 0; x1 = width; y0 = 0; y1 = height; }

        float halfW = (x1 - x0) * 0.5f, halfH = (y1 - y0) * 0.5f;
        double sr = 0, sg = 0, sb = 0, sw = 0, sv = 0, svw = 0;

        for (int y = y0; y < y1; y++)
        {
            float ey = Math.Min(y - y0 + 0.5f, y1 - y - 0.5f) / halfH;
            int row = y * width * 4;
            for (int x = x0; x < x1; x++)
            {
                int i = row + x * 4;
                float b = bgra[i] * (1f / 255f), g = bgra[i + 1] * (1f / 255f), r = bgra[i + 2] * (1f / 255f);

                float max = MathF.Max(r, MathF.Max(g, b));
                float min = MathF.Min(r, MathF.Min(g, b));
                float sat = max > 0 ? (max - min) / max : 0;

                // 0 at the edge, 1 at the centre.
                float centre = MathF.Min(Math.Min(x - x0 + 0.5f, x1 - x - 0.5f) / halfW, ey);
                float edgeW = 1f + o.EdgeWeight * (1f - centre) * (1f - centre);

                float w = edgeW * max * max * (1f + o.SaturationWeight * sat);
                sr += r * w; sg += g * w; sb += b * w; sw += w;
                sv += max * edgeW; svw += edgeW;
            }
        }

        if (sw <= 1e-6) return default;

        var c = new Rgb((float)(sr / sw), (float)(sg / sw), (float)(sb / sw));

        // Saturation boost around the grey axis.
        float grey = (c.R + c.G + c.B) / 3f;
        c = new Rgb(grey + (c.R - grey) * o.Boost, grey + (c.G - grey) * o.Boost, grey + (c.B - grey) * o.Boost);
        c = new Rgb(MathF.Max(c.R, 0), MathF.Max(c.G, 0), MathF.Max(c.B, 0));

        // Rescale so the brightest channel matches scene brightness.
        float peak = MathF.Max(c.R, MathF.Max(c.G, c.B));
        if (peak <= 1e-6f) return default;
        float target = Math.Clamp((float)(sv / svw) * o.Brightness, 0f, 1f);
        float k = target / peak;
        return new Rgb(MathF.Min(c.R * k, 1), MathF.Min(c.G * k, 1), MathF.Min(c.B * k, 1));
    }
}
