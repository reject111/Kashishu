#if WINDOWS
using System.Runtime.InteropServices;
using SkiaSharp;

namespace Kashishu;

/// <summary>
/// Grabs the desktop pixels under the island. The island window hides itself from screen
/// capture (WDA_EXCLUDEFROMCAPTURE), so capturing the area under the capsule yields the windows
/// and wallpaper behind it instead of the island itself. That is what turns the capsule's
/// material into a live "desktop acrylic" rather than a stored copy of the wallpaper.
/// </summary>
internal sealed class DesktopCapture : IDisposable
{
    private const int SrcCopy = 0x00CC0020;
    private const int CaptureBlt = 0x40000000;
    private const int Halftone = 4;
    private const int BiRgb = 0;

    private nint _screenDc;
    private nint _memoryDc;
    private nint _bitmap;
    private nint _previousBitmap;
    private nint _pixels;
    private int _width;
    private int _height;
    private bool _disposed;

    /// <summary>
    /// Copies <paramref name="sourceWidth"/> by <paramref name="sourceHeight"/> pixels at
    /// <paramref name="x"/>, <paramref name="y"/>, scales them to the requested size and blurs
    /// and desaturates them, which is the material the capsule paints. The image owns its
    /// pixels, so it stays valid after this call.
    /// </summary>
    public SKImage? Snapshot(int x, int y, int sourceWidth, int sourceHeight, int width, int height,
        int blurRadius, float saturation)
    {
        if (_disposed || sourceWidth <= 0 || sourceHeight <= 0 || width <= 0 || height <= 0)
            return null;
        if (!Ensure(width, height))
            return null;
        if (!StretchBlt(_memoryDc, 0, 0, width, height, _screenDc, x, y,
                sourceWidth, sourceHeight, SrcCopy | CaptureBlt))
            return null;
        // Flush GDI's batched writes before reading the DIB memory directly.
        GdiFlush();

        // The blur is done on the tiny capture rather than with a Skia image filter: it is a
        // handful of pixels per row and it keeps the material out of the drawing surface's
        // filter path, which does not produce usable output on the GPU-backed canvas.
        var stride = width * 4;
        var buffer = new byte[stride * height];
        Marshal.Copy(_pixels, buffer, 0, buffer.Length);
        BoxBlur(buffer, width, height, blurRadius);
        Desaturate(buffer, saturation);

        // The image copies the buffer, so it owns its pixels and outlives the capture buffer.
        return SKImage.FromPixelCopy(
            new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul),
            buffer, stride);
    }

    /// <summary>Three box passes approximate a Gaussian closely enough at this size.</summary>
    private static void BoxBlur(byte[] pixels, int width, int height, int radius)
    {
        if (radius <= 0)
            return;
        var stride = width * 4;
        var source = new byte[pixels.Length];
        for (var pass = 0; pass < 3; pass++)
        {
            Array.Copy(pixels, source, pixels.Length);
            BlurHorizontal(source, pixels, stride, width, height, radius);
            Array.Copy(pixels, source, pixels.Length);
            BlurVertical(source, pixels, stride, width, height, radius);
        }
    }

    private static void BlurHorizontal(byte[] source, byte[] target, int stride, int width,
        int height, int radius)
    {
        var window = radius * 2 + 1;
        for (var y = 0; y < height; y++)
        {
            var row = y * stride;
            for (var channel = 0; channel < 3; channel++)
            {
                var sum = 0;
                for (var i = -radius; i <= radius; i++)
                    sum += source[row + Clamp(i, width - 1) * 4 + channel];
                for (var x = 0; x < width; x++)
                {
                    target[row + x * 4 + channel] = (byte)(sum / window);
                    sum -= source[row + Clamp(x - radius, width - 1) * 4 + channel];
                    sum += source[row + Clamp(x + radius + 1, width - 1) * 4 + channel];
                }
            }
        }
    }

    private static void BlurVertical(byte[] source, byte[] target, int stride, int width,
        int height, int radius)
    {
        var window = radius * 2 + 1;
        for (var x = 0; x < width; x++)
        {
            var column = x * 4;
            for (var channel = 0; channel < 3; channel++)
            {
                var sum = 0;
                for (var i = -radius; i <= radius; i++)
                    sum += source[Clamp(i, height - 1) * stride + column + channel];
                for (var y = 0; y < height; y++)
                {
                    target[y * stride + column + channel] = (byte)(sum / window);
                    sum -= source[Clamp(y - radius, height - 1) * stride + column + channel];
                    sum += source[Clamp(y + radius + 1, height - 1) * stride + column + channel];
                }
            }
        }
    }

    private static void Desaturate(byte[] pixels, float saturation)
    {
        var mix = Math.Clamp(saturation, 0f, 1f);
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var luma = 0.072f * pixels[i] + 0.715f * pixels[i + 1] + 0.213f * pixels[i + 2];
            pixels[i] = (byte)Math.Clamp(luma + (pixels[i] - luma) * mix, 0f, 255f);
            pixels[i + 1] = (byte)Math.Clamp(luma + (pixels[i + 1] - luma) * mix, 0f, 255f);
            pixels[i + 2] = (byte)Math.Clamp(luma + (pixels[i + 2] - luma) * mix, 0f, 255f);
            pixels[i + 3] = 255;
        }
    }

    private static int Clamp(int value, int max) => value < 0 ? 0 : value > max ? max : value;

    private bool Ensure(int width, int height)
    {
        if (_bitmap != nint.Zero && _width == width && _height == height)
            return true;
        Release();
        _screenDc = GetDC(nint.Zero);
        if (_screenDc == nint.Zero)
            return false;
        _memoryDc = CreateCompatibleDC(_screenDc);
        if (_memoryDc == nint.Zero)
        {
            Release();
            return false;
        }

        var info = new BitmapInfo
        {
            // A negative height keeps the bitmap top-down, matching Skia's row order.
            Header = new BitmapInfoHeader
            {
                Size = Marshal.SizeOf<BitmapInfoHeader>(),
                Width = width,
                Height = -height,
                Planes = 1,
                BitCount = 32,
                Compression = BiRgb
            }
        };
        _bitmap = CreateDIBSection(_screenDc, ref info, 0, out _pixels, nint.Zero, 0);
        if (_bitmap == nint.Zero || _pixels == nint.Zero)
        {
            Release();
            return false;
        }

        _previousBitmap = SelectObject(_memoryDc, _bitmap);
        // Halftone keeps the downscale from shimmering as the desktop changes behind the pill.
        SetStretchBltMode(_memoryDc, Halftone);
        SetBrushOrgEx(_memoryDc, 0, 0, out _);
        _width = width;
        _height = height;
        return true;
    }

    private void Release()
    {
        if (_memoryDc != nint.Zero && _previousBitmap != nint.Zero)
            SelectObject(_memoryDc, _previousBitmap);
        _previousBitmap = nint.Zero;
        if (_bitmap != nint.Zero)
            DeleteObject(_bitmap);
        _bitmap = nint.Zero;
        _pixels = nint.Zero;
        if (_memoryDc != nint.Zero)
            DeleteDC(_memoryDc);
        _memoryDc = nint.Zero;
        if (_screenDc != nint.Zero)
            ReleaseDC(nint.Zero, _screenDc);
        _screenDc = nint.Zero;
        _width = 0;
        _height = 0;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Release();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ColorsUsed;
        public int ColorsImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint Colors;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hwnd, nint dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint CreateCompatibleDC(nint dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteDC(nint dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage,
        out nint bits, nint section, uint offset);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint SelectObject(nint dc, nint obj);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteObject(nint obj);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool StretchBlt(nint dest, int destX, int destY, int destWidth,
        int destHeight, nint source, int sourceX, int sourceY, int sourceWidth, int sourceHeight,
        int rop);

    [DllImport("gdi32.dll")]
    private static extern bool GdiFlush();

    [DllImport("gdi32.dll")]
    private static extern int SetStretchBltMode(nint dc, int mode);

    [DllImport("gdi32.dll")]
    private static extern bool SetBrushOrgEx(nint dc, int x, int y, out Point32 previous);
}
#endif
