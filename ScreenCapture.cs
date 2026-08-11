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

    public bool PixelSearch(double x1, double y1, double x2, double y2, int r, int g, int b, int variation, out int px, out int py, int minMatches = 1)
    {
        px = 0;
        py = 0;
        if (Width == 0 || Height == 0) return false;
        minMatches = Math.Max(1, minMatches);

        int sx = Math.Clamp((int)Math.Min(x1, x2), 0, Width - 1);
        int ex = Math.Clamp((int)Math.Max(x1, x2), 0, Width - 1);
        int sy = Math.Clamp((int)Math.Min(y1, y2), 0, Height - 1);
        int ey = Math.Clamp((int)Math.Max(y1, y2), 0, Height - 1);

        int matches = 0;
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
                    if (matches++ == 0)
                    {
                        px = x;
                        py = y;
                    }
                    if (matches >= minMatches) return true;
                }
            }
        }
        return false;
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
}

public static class ScreenCapture
{
    public static ScreenFrame Capture(ScreenFrame reusable)
    {
        var bounds = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
        return Capture(reusable, bounds);
    }

    public static ScreenFrame Capture(ScreenFrame reusable, Rectangle region)
    {
        if (region.Width <= 0 || region.Height <= 0)
        {
            region = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
        }

        using var bmp = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(region.Left, region.Top, 0, 0, region.Size, CopyPixelOperation.SourceCopy);
        }

        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int bytes = data.Stride * data.Height;
            if (reusable == null || reusable.Buffer == null || reusable.Buffer.Length < bytes)
                reusable = new ScreenFrame { Buffer = new byte[bytes] };
            Marshal.Copy(data.Scan0, reusable.Buffer, 0, bytes);
            reusable.Width = region.Width;
            reusable.Height = region.Height;
            reusable.Stride = data.Stride;
            return reusable;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }
}
