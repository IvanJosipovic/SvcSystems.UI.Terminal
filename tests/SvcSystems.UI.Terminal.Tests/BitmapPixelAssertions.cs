using Avalonia;
using Avalonia.Media.Imaging;
using System.Runtime.InteropServices;

namespace SvcSystems.UI.Terminal.Tests;

internal static class BitmapPixelAssertions
{
    internal static (byte Blue, byte Green, byte Red, byte Alpha) GetBgraPixel(
        RenderTargetBitmap bitmap,
        int x,
        int y)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        if ((uint)x >= bitmap.PixelSize.Width || (uint)y >= bitmap.PixelSize.Height)
        {
            throw new ArgumentOutOfRangeException(nameof(x), "The requested pixel is outside the bitmap.");
        }

        int stride = checked(bitmap.PixelSize.Width * sizeof(uint));
        int size = checked(stride * bitmap.PixelSize.Height);
        nint native = Marshal.AllocHGlobal(size);
        try
        {
            bitmap.CopyPixels(new PixelRect(bitmap.PixelSize), native, size, stride);
            byte[] pixels = new byte[size];
            Marshal.Copy(native, pixels, 0, size);
            int offset = checked(y * stride + x * sizeof(uint));
            return (pixels[offset], pixels[offset + 1], pixels[offset + 2], pixels[offset + 3]);
        }
        finally
        {
            Marshal.FreeHGlobal(native);
        }
    }

    internal static bool ContainsRedPixel(RenderTargetBitmap bitmap)
    {
        int stride = checked(bitmap.PixelSize.Width * sizeof(uint));
        int size = checked(stride * bitmap.PixelSize.Height);
        nint native = Marshal.AllocHGlobal(size);
        try
        {
            bitmap.CopyPixels(new PixelRect(bitmap.PixelSize), native, size, stride);
            byte[] pixels = new byte[size];
            Marshal.Copy(native, pixels, 0, size);
            for (int offset = 0; offset < pixels.Length; offset += sizeof(uint))
            {
                if (pixels[offset + 2] > 180 && pixels[offset + 1] < 80 && pixels[offset] < 80 && pixels[offset + 3] > 0)
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(native);
        }
    }
}
