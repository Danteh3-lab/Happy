using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace HappyBot;

public sealed class ScreenFrame
{
    public byte[] Buffer;
    public int Width;
    public int Height;
    public int Stride;
    public int OriginX;
    public int OriginY;

    public bool PixelSearch(double x1, double y1, double x2, double y2, int r, int g, int b, int variation, out int px, out int py)
    {
        px = 0;
        py = 0;
        if (Width <= 0 || Height <= 0 || Buffer == null) return false;

        int requestedLeft = (int)Math.Min(x1, x2);
        int requestedRight = (int)Math.Max(x1, x2);
        int requestedTop = (int)Math.Min(y1, y2);
        int requestedBottom = (int)Math.Max(y1, y2);
        if (requestedRight < 0 || requestedTop > Height - 1 ||
            requestedLeft > Width - 1 || requestedBottom < 0)
            return false;

        int sx = Math.Clamp(requestedLeft, 0, Width - 1);
        int ex = Math.Clamp(requestedRight, 0, Width - 1);
        int sy = Math.Clamp(requestedTop, 0, Height - 1);
        int ey = Math.Clamp(requestedBottom, 0, Height - 1);

        for (int y = sy; y <= ey; y++)
        {
            int row = y * Stride;
            for (int x = sx; x <= ex; x++)
            {
                int i = row + x * 4;
                if (Math.Abs(Buffer[i + 2] - r) <= variation &&
                    Math.Abs(Buffer[i + 1] - g) <= variation &&
                    Math.Abs(Buffer[i] - b) <= variation)
                {
                    px = x;
                    py = y;
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>Searches using primary-screen coordinates and returns screen coordinates.</summary>
    public bool ScreenPixelSearch(double x1, double y1, double x2, double y2, int r, int g, int b, int variation, out int px, out int py)
    {
        double requestedLeft = Math.Min(x1, x2);
        double requestedRight = Math.Max(x1, x2);
        double requestedTop = Math.Min(y1, y2);
        double requestedBottom = Math.Max(y1, y2);
        Rectangle frameBounds = new(OriginX, OriginY, Width, Height);
        if (Width <= 0 || Height <= 0 ||
            requestedRight < frameBounds.Left || requestedLeft > frameBounds.Right - 1 ||
            requestedBottom < frameBounds.Top || requestedTop > frameBounds.Bottom - 1)
        {
            px = py = 0;
            return false;
        }

        if (!PixelSearch(requestedLeft - OriginX, requestedTop - OriginY,
                requestedRight - OriginX, requestedBottom - OriginY,
                r, g, b, variation, out px, out py))
            return false;
        px += OriginX;
        py += OriginY;
        return true;
    }

    public bool SamplePixel(int x, int y, out int r, out int g, out int b)
    {
        r = g = b = 0;
        if (Width == 0 || Height == 0 || x < 0 || x >= Width || y < 0 || y >= Height) return false;
        int i = y * Stride + x * 4;
        b = Buffer[i];
        g = Buffer[i + 1];
        r = Buffer[i + 2];
        return true;
    }

    public int CountColor(int r, int g, int b, int variation, out int firstX, out int firstY, int max = 100)
    {
        firstX = firstY = -1;
        int count = 0;
        if (Width == 0 || Height == 0) return 0;
        for (int y = 0; y < Height && count < max; y++)
        {
            int row = y * Stride;
            for (int x = 0; x < Width && count < max; x++)
            {
                int i = row + x * 4;
                if (Math.Abs(Buffer[i + 2] - r) <= variation &&
                    Math.Abs(Buffer[i + 1] - g) <= variation &&
                    Math.Abs(Buffer[i] - b) <= variation)
                {
                    if (count == 0) { firstX = x; firstY = y; }
                    count++;
                }
            }
        }
        return count;
    }

    /// <summary>
    /// The same match predicate as PixelSearch, with compact diagnostic data for
    /// telemetry. It does not influence the detector beyond supplying its result.
    /// </summary>
    public ColorProbe ProbeColor(int r, int g, int b, int variation, int max = 100) =>
        ProbeColor(0, 0, Width - 1, Height - 1, r, g, b, variation, max);

    public ColorProbe ProbeColor(double x1, double y1, double x2, double y2, int r, int g, int b, int variation, int max = 100)
    {
        int firstX = -1;
        int firstY = -1;
        int count = 0;
        int closestDistance = int.MaxValue;
        int closestR = 0, closestG = 0, closestB = 0;
        if (Width <= 0 || Height <= 0 || Buffer == null) return new ColorProbe(0, -1, -1, "n/a", -1);

        int requestedLeft = (int)Math.Min(x1, x2);
        int requestedRight = (int)Math.Max(x1, x2);
        int requestedTop = (int)Math.Min(y1, y2);
        int requestedBottom = (int)Math.Max(y1, y2);
        if (requestedRight < 0 || requestedTop > Height - 1 ||
            requestedLeft > Width - 1 || requestedBottom < 0)
            return new ColorProbe(0, -1, -1, "n/a", -1);

        int sx = Math.Clamp(requestedLeft, 0, Width - 1);
        int ex = Math.Clamp(requestedRight, 0, Width - 1);
        int sy = Math.Clamp(requestedTop, 0, Height - 1);
        int ey = Math.Clamp(requestedBottom, 0, Height - 1);
        for (int y = sy; y <= ey; y++)
        {
            int row = y * Stride;
            for (int x = sx; x <= ex; x++)
            {
                int i = row + x * 4;
                int pixelB = Buffer[i];
                int pixelG = Buffer[i + 1];
                int pixelR = Buffer[i + 2];
                int distance = Math.Abs(pixelR - r) + Math.Abs(pixelG - g) + Math.Abs(pixelB - b);
                if (distance < closestDistance)
                {
                    closestDistance = distance;
                    closestR = pixelR;
                    closestG = pixelG;
                    closestB = pixelB;
                }
                if (count < max && Math.Abs(pixelR - r) <= variation &&
                    Math.Abs(pixelG - g) <= variation && Math.Abs(pixelB - b) <= variation)
                {
                    if (count == 0) { firstX = x; firstY = y; }
                    count++;
                }
            }
        }
        return new ColorProbe(count, firstX, firstY, $"{closestR},{closestG},{closestB}", closestDistance);
    }
}

public sealed record ColorProbe(int MatchCount, int FirstX, int FirstY, string ClosestRgb, int ClosestDistance);

public static class ScreenCapture
{
    public static ScreenFrame Capture(ScreenFrame reusable)
    {
        var bounds = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
        return Capture(reusable, bounds);
    }

    public static ScreenFrame Capture(ScreenFrame reusable, Rectangle region)
    {
        using var session = new ScreenCaptureSession();
        return session.Capture(reusable, region);
    }
}

/// <summary>
/// Vision-thread-owned capture resources. Reusing the bitmap and Graphics
/// surface avoids allocating and tearing down GDI objects on every loop.
/// </summary>
public sealed class ScreenCaptureSession : IDisposable
{
    private Bitmap _bitmap;
    private Graphics _graphics;
    private int _surfaceWidth;
    private int _surfaceHeight;
    private bool _disposed;

    public ScreenFrame Capture(ScreenFrame reusable, Rectangle region)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ScreenCaptureSession));
        if (region.Width <= 0 || region.Height <= 0)
            region = System.Windows.Forms.Screen.PrimaryScreen.Bounds;

        EnsureSurface(region.Width, region.Height);
        _graphics.CopyFromScreen(region.Left, region.Top, 0, 0, region.Size, CopyPixelOperation.SourceCopy);

        BitmapData data = _bitmap.LockBits(new Rectangle(0, 0, region.Width, region.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int bytes = Math.Abs(data.Stride) * data.Height;
            if (reusable == null || reusable.Buffer == null || reusable.Buffer.Length < bytes)
                reusable = new ScreenFrame { Buffer = new byte[bytes] };
            Marshal.Copy(data.Scan0, reusable.Buffer, 0, bytes);
            reusable.Width = region.Width;
            reusable.Height = region.Height;
            reusable.Stride = data.Stride;
            reusable.OriginX = region.Left;
            reusable.OriginY = region.Top;
            return reusable;
        }
        finally
        {
            _bitmap.UnlockBits(data);
        }
    }

    private void EnsureSurface(int width, int height)
    {
        if (_bitmap != null && width <= _surfaceWidth && height <= _surfaceHeight) return;

        _graphics?.Dispose();
        _bitmap?.Dispose();
        _surfaceWidth = Math.Max(width, _surfaceWidth);
        _surfaceHeight = Math.Max(height, _surfaceHeight);
        _bitmap = new Bitmap(_surfaceWidth, _surfaceHeight, PixelFormat.Format32bppArgb);
        _graphics = Graphics.FromImage(_bitmap);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _graphics?.Dispose();
        _bitmap?.Dispose();
        _graphics = null;
        _bitmap = null;
    }
}
