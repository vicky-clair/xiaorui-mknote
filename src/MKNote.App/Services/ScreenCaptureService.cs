using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace MKNote.App.Services;

public sealed record CapturedScreenBuffer(int Left, int Top, int Width, int Height, byte[] BgraPixels);

/// <summary>
/// 纯内存多显示器高 DPI 屏幕截图服务（基于 Win32 GDI BitBlt 与 WinRT BitmapEncoder）。
/// 全程不向磁盘写入任何临时明文图片，裁剪编码后直接交由本地加密数据库落盘。
/// </summary>
public static partial class ScreenCaptureService
{
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;
    private const uint SRCCOPY = 0x00CC0020;
    private const uint CAPTUREBLT = 0x40000000;
    private const uint BI_RGB = 0;
    private const uint DIB_RGB_COLORS = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int nIndex);

    [LibraryImport("user32.dll")]
    private static partial nint GetDC(nint hWnd);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(nint hWnd, nint hDC);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleDC(nint hdc);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(nint hdc);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleBitmap(nint hdc, int cx, int cy);

    [LibraryImport("gdi32.dll")]
    private static partial nint SelectObject(nint hdc, nint h);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint ho);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BitBlt(nint hdc, int x, int y, int cx, int cy, nint hdcSrc, int x1, int y1, uint rop);

    [LibraryImport("gdi32.dll")]
    private static unsafe partial int GetDIBits(nint hdc, nint hbm, uint start, uint cLines, void* lpvBits, ref BITMAPINFOHEADER lpbmi, uint usage);

    public static unsafe CapturedScreenBuffer CaptureVirtualScreen()
    {
        var left = GetSystemMetrics(SM_XVIRTUALSCREEN);
        var top = GetSystemMetrics(SM_YVIRTUALSCREEN);
        var width = Math.Max(1, GetSystemMetrics(SM_CXVIRTUALSCREEN));
        var height = Math.Max(1, GetSystemMetrics(SM_CYVIRTUALSCREEN));

        var screenDc = GetDC(0);
        if (screenDc == 0)
            throw new InvalidOperationException("无法获取桌面屏幕上下文。");

        nint memDc = 0;
        nint bmp = 0;
        nint oldBmp = 0;
        try
        {
            memDc = CreateCompatibleDC(screenDc);
            bmp = CreateCompatibleBitmap(screenDc, width, height);
            oldBmp = SelectObject(memDc, bmp);

            BitBlt(memDc, 0, 0, width, height, screenDc, left, top, SRCCOPY | CAPTUREBLT);

            var pixels = new byte[width * height * 4];
            var bmi = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height, // top-down DIB
                biPlanes = 1,
                biBitCount = 32,
                biCompression = BI_RGB
            };

            fixed (byte* p = pixels)
            {
                GetDIBits(memDc, bmp, 0, (uint)height, p, ref bmi, DIB_RGB_COLORS);
            }

            // Ensure alpha channel is 255 (opaque) for WinRT SoftwareBitmap display & PNG encoding
            for (int i = 3; i < pixels.Length; i += 4)
                pixels[i] = 255;

            return new CapturedScreenBuffer(left, top, width, height, pixels);
        }
        finally
        {
            if (oldBmp != 0 && memDc != 0) SelectObject(memDc, oldBmp);
            if (bmp != 0) DeleteObject(bmp);
            if (memDc != 0) DeleteDC(memDc);
            ReleaseDC(0, screenDc);
        }
    }

    public static async Task<byte[]> EncodeRegionToPngAsync(CapturedScreenBuffer src, int x, int y, int width, int height)
    {
        x = Math.Clamp(x, 0, Math.Max(0, src.Width - 1));
        y = Math.Clamp(y, 0, Math.Max(0, src.Height - 1));
        width = Math.Clamp(width, 1, src.Width - x);
        height = Math.Clamp(height, 1, src.Height - y);

        var cropped = new byte[width * height * 4];
        var srcStride = src.Width * 4;
        var dstStride = width * 4;
        for (int row = 0; row < height; row++)
        {
            System.Buffer.BlockCopy(src.BgraPixels, (y + row) * srcStride + x * 4, cropped, row * dstStride, dstStride);
        }

        using var memStream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, memStream);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore,
            (uint)width,
            (uint)height,
            96,
            96,
            cropped);
        await encoder.FlushAsync();

        var outBytes = new byte[memStream.Size];
        memStream.Seek(0);
        await memStream.ReadAsync(outBytes.AsBuffer(), (uint)outBytes.Length, InputStreamOptions.None);
        return outBytes;
    }
}
