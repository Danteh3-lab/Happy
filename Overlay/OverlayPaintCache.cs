using System.Drawing.Drawing2D;

namespace HappyBot.Overlay;

/// <summary>
/// GDI objects reused across overlay frames. The overlay repaints at ~30 Hz,
/// so creating fonts, pens and brushes per chip was hundreds of allocations a
/// second. Owned by the overlay form and disposed with it.
/// </summary>
internal sealed class OverlayPaintCache : IDisposable
{
    private readonly Dictionary<int, SolidBrush> _brushes = new();
    private readonly Dictionary<(int Argb, float Width, DashStyle Dash), Pen> _pens = new();

    public OverlayPaintCache()
    {
        Label = CreateFont(new[] { "Bahnschrift SemiBold", "Segoe UI Semibold" }, 9f, FontStyle.Regular);
        LabelLarge = CreateFont(new[] { "Bahnschrift SemiBold", "Segoe UI Semibold" }, 11f, FontStyle.Regular);
        Mono = CreateFont(new[] { "Cascadia Mono", "Consolas" }, 8f, FontStyle.Regular);
    }

    public Font Label { get; }

    public Font LabelLarge { get; }

    public Font Mono { get; }

    public StringFormat Format { get; } = (StringFormat)StringFormat.GenericTypographic.Clone();

    public SolidBrush Brush(Color color)
    {
        int key = color.ToArgb();
        if (!_brushes.TryGetValue(key, out SolidBrush brush))
        {
            brush = new SolidBrush(color);
            _brushes[key] = brush;
        }
        return brush;
    }

    public Pen Pen(Color color, float width = 1f, DashStyle dash = DashStyle.Solid)
    {
        var key = (color.ToArgb(), width, dash);
        if (!_pens.TryGetValue(key, out Pen pen))
        {
            pen = new Pen(color, width) { DashStyle = dash };
            _pens[key] = pen;
        }
        return pen;
    }

    public void Dispose()
    {
        foreach (SolidBrush brush in _brushes.Values) brush.Dispose();
        foreach (Pen pen in _pens.Values) pen.Dispose();
        _brushes.Clear();
        _pens.Clear();
        Label.Dispose();
        LabelLarge.Dispose();
        Mono.Dispose();
        Format.Dispose();
    }

    // GDI+ silently substitutes a default face for a missing family, so check
    // the resolved name and fall through the list.
    private static Font CreateFont(string[] families, float size, FontStyle style)
    {
        foreach (string family in families)
        {
            var font = new Font(family, size, style, GraphicsUnit.Point);
            if (font.Name.Equals(family, StringComparison.OrdinalIgnoreCase)) return font;
            font.Dispose();
        }
        return new Font(FontFamily.GenericSansSerif, size, style, GraphicsUnit.Point);
    }
}
