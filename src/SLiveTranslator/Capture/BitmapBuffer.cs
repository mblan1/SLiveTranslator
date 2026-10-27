using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace SLiveTranslator.Capture;

/// <summary>
/// Pixel memory allocated once and lent out as 32bpp bitmaps, so each frame, its mask and its crop cost no new pixel
/// buffer. The memory is native: it never moves under a bitmap, and it goes back the moment the buffer is disposed (a
/// pinned managed array would wait for a full GC, which an idle app hardly ever runs). A bitmap it returns is valid
/// until the next call that asks for another size, or <see cref="Dispose"/>.
/// </summary>
internal sealed unsafe class BitmapBuffer : IDisposable
{
    byte* _pixels;
    nuint _capacity;
    Bitmap? _bitmap, _rows;
    int _width, _height, _rowsTop, _rowsHeight;

    /// <summary>A bitmap of this size over the buffer; the same object while the size stays the same.</summary>
    public Bitmap Get(int width, int height) => Get(width, height, height);

    /// <param name="reserveHeight">Rows to make room for, so a taller bitmap later needs no new memory.</param>
    Bitmap Get(int width, int height, int reserveHeight)
    {
        if (_bitmap is not null && width == _width && height == _height) return _bitmap;
        DisposeBitmaps();
        nuint bytes = checked((nuint)(width * Math.Max(height, reserveHeight) * 4));
        if (_capacity < bytes)
        {
            Free();
            _pixels = (byte*)NativeMemory.Alloc(bytes);
            _capacity = bytes;
        }
        (_width, _height) = (width, height);
        return _bitmap = new Bitmap(width, height, width * 4, PixelFormat.Format32bppArgb, (IntPtr)_pixels);
    }

    /// <summary>Some rows of the last <see cref="Get"/> bitmap as a bitmap of their own, sharing its pixels.</summary>
    public Bitmap Rows(int top, int height)
    {
        if (_bitmap is null) throw new InvalidOperationException("No bitmap to take rows from.");
        if (top == 0 && height == _height) return _bitmap;
        if (top < 0 || height <= 0 || top + height > _height) throw new ArgumentOutOfRangeException(nameof(height));
        if (_rows is not null && top == _rowsTop && height == _rowsHeight) return _rows;
        _rows?.Dispose();
        (_rowsTop, _rowsHeight) = (top, height);
        return _rows = new Bitmap(_width, height, _width * 4, PixelFormat.Format32bppArgb, (IntPtr)(_pixels + (long)top * _width * 4));
    }

    /// <summary>
    /// Copies rows of <paramref name="source"/> into the buffer and returns them as a bitmap. Room for all of the
    /// source's rows is made at once, so crops of changing height share one allocation.
    /// </summary>
    public Bitmap CopyRows(Bitmap source, int top, int height)
    {
        var target = Get(source.Width, height, source.Height);
        var data = source.LockBits(new Rectangle(0, top, source.Width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            long rowBytes = source.Width * 4L;
            for (int y = 0; y < height; y++)
                Buffer.MemoryCopy((byte*)data.Scan0 + (long)y * data.Stride, _pixels + y * rowBytes, rowBytes, rowBytes);
        }
        finally
        {
            source.UnlockBits(data);
        }
        return target;
    }

    void DisposeBitmaps()
    {
        _rows?.Dispose();
        _bitmap?.Dispose();
        (_rows, _bitmap) = (null, null);
    }

    void Free()
    {
        NativeMemory.Free(_pixels);
        _pixels = null;
        _capacity = 0;
    }

    public void Dispose()
    {
        DisposeBitmaps();
        Free();
        GC.SuppressFinalize(this);
    }

    /// <summary>A buffer nobody disposed (a test's session) still returns its memory; its bitmaps are garbage by then too.</summary>
    ~BitmapBuffer() => Free();
}
