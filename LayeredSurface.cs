using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace DshOrbit;

/// <summary>把一张 ARGB 位图以逐像素透明的方式推到窗口上（UpdateLayeredWindow）。</summary>
internal sealed class LayeredSurface : IDisposable
{
    private IntPtr _memDc;
    private IntPtr _dib;
    private IntPtr _bits;
    private IntPtr _previous;
    private int _width;
    private int _height;

    public unsafe void Push(IntPtr hwnd, Bitmap? surface, int x, int y, float opacity)
    {
        if (surface is null || surface.Width <= 0 || surface.Height <= 0)
        {
            return;
        }

        var screenDc = Native.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            return;
        }

        try
        {
            Ensure(screenDc, surface.Width, surface.Height);
            if (_bits == IntPtr.Zero)
            {
                return;
            }

            var globalAlpha = Math.Clamp((int)MathF.Round(opacity * 255f), 40, 255);
            var locks = surface.LockBits(
                new Rectangle(0, 0, surface.Width, surface.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var source = (byte*)locks.Scan0;
                var target = (byte*)_bits;
                var rowBytes = surface.Width * 4;

                for (var row = 0; row < surface.Height; row++)
                {
                    var from = source + (row * locks.Stride);
                    var to = target + (row * rowBytes);
                    for (var i = 0; i < rowBytes; i += 4)
                    {
                        var alpha = from[i + 3] * globalAlpha / 255;
                        to[i + 3] = (byte)alpha;
                        to[i + 2] = (byte)(from[i + 2] * alpha / 255);
                        to[i + 1] = (byte)(from[i + 1] * alpha / 255);
                        to[i] = (byte)(from[i] * alpha / 255);
                    }
                }
            }
            finally
            {
                surface.UnlockBits(locks);
            }

            var destination = new Native.POINT { X = x, Y = y };
            var size = new Native.SIZE { CX = surface.Width, CY = surface.Height };
            var point = new Native.POINT { X = 0, Y = 0 };
            var blend = new Native.BLENDFUNCTION
            {
                BlendOp = Native.AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = Native.AC_SRC_ALPHA,
            };

            _ = Native.UpdateLayeredWindow(
                hwnd, screenDc, ref destination, ref size, _memDc, ref point, 0, ref blend, Native.ULW_ALPHA);
        }
        finally
        {
            _ = Native.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private void Ensure(IntPtr screenDc, int width, int height)
    {
        if (_dib != IntPtr.Zero && _width == width && _height == height)
        {
            return;
        }

        Release();

        _memDc = Native.CreateCompatibleDC(screenDc);
        var header = new Native.BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height,
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0,
        };

        _dib = Native.CreateDIBSection(_memDc, ref header, 0, out _bits, IntPtr.Zero, 0);
        if (_dib != IntPtr.Zero)
        {
            _previous = Native.SelectObject(_memDc, _dib);
            _width = width;
            _height = height;
        }
    }

    private void Release()
    {
        if (_memDc != IntPtr.Zero)
        {
            if (_previous != IntPtr.Zero)
            {
                _ = Native.SelectObject(_memDc, _previous);
                _previous = IntPtr.Zero;
            }

            if (_dib != IntPtr.Zero)
            {
                _ = Native.DeleteObject(_dib);
                _dib = IntPtr.Zero;
            }

            _ = Native.DeleteDC(_memDc);
            _memDc = IntPtr.Zero;
        }

        _bits = IntPtr.Zero;
        _width = 0;
        _height = 0;
    }

    public void Dispose() => Release();
}
