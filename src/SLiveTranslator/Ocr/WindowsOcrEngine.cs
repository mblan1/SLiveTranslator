using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using WinOcr = Windows.Media.Ocr;

namespace SLiveTranslator.Ocr;

/// <summary>OCR through the engine built into Windows 10/11 (Windows.Media.Ocr). Uses installed language packs; nothing to download.</summary>
public sealed class WindowsOcrEngine : IOcrEngine
{
    readonly WinOcr.OcrEngine _engine;

    // A Windows OcrEngine runs one recognition at a time; a second call fails with "Another RecognizeAsync operation is already running".
    readonly SemaphoreSlim _gate = new(1, 1);

    WindowsOcrEngine(WinOcr.OcrEngine engine)
    {
        _engine = engine;
        LanguageTag = engine.RecognizerLanguage.LanguageTag;
    }

    public string LanguageTag { get; }

    public static IReadOnlyList<(string Tag, string Name)> AvailableLanguages() =>
        WinOcr.OcrEngine.AvailableRecognizerLanguages.Select(l => (l.LanguageTag, l.DisplayName)).ToList();

    /// <param name="languageTag">BCP-47 tag, or empty to use the Windows user-profile languages.</param>
    public static WindowsOcrEngine Create(string? languageTag)
    {
        WinOcr.OcrEngine? engine = null;
        if (!string.IsNullOrWhiteSpace(languageTag))
        {
            var lang = new Language(languageTag);
            if (WinOcr.OcrEngine.IsLanguageSupported(lang))
                engine = WinOcr.OcrEngine.TryCreateFromLanguage(lang);
        }
        engine ??= WinOcr.OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null)
            throw new InvalidOperationException(
                "No Windows OCR language is installed. Open Settings > Time & Language > Language & region, add a language and make sure 'Optical character recognition' is included.");
        return new WindowsOcrEngine(engine);
    }

    public async Task<IReadOnlyList<OcrLine>> RecognizeAsync(Bitmap image, CancellationToken ct, double maxUpscale = 2.0)
    {
        // Windows OCR caps the image size; small UI text recognizes better when upscaled.
        double maxSide = Math.Max(image.Width, image.Height);
        double scale = Math.Min(Math.Max(1.0, maxUpscale), (WinOcr.OcrEngine.MaxImageDimension - 1.0) / maxSide);

        WinOcr.OcrResult result;
        await _gate.WaitAsync(ct);
        try
        {
            using var soft = ToSoftwareBitmap(image, scale);
            ct.ThrowIfCancellationRequested();
            result = await _engine.RecognizeAsync(soft);
        }
        finally
        {
            _gate.Release();
        }
        ct.ThrowIfCancellationRequested();

        var lines = new List<OcrLine>();
        foreach (var line in result.Lines)
        {
            var text = line.Text?.Trim();
            if (string.IsNullOrEmpty(text) || line.Words.Count == 0) continue;

            double l = double.MaxValue, t = double.MaxValue, r = double.MinValue, b = double.MinValue;
            foreach (var word in line.Words)
            {
                var rc = word.BoundingRect;
                l = Math.Min(l, rc.Left);
                t = Math.Min(t, rc.Top);
                r = Math.Max(r, rc.Right);
                b = Math.Max(b, rc.Bottom);
            }
            var bounds = Rectangle.FromLTRB((int)(l / scale), (int)(t / scale), (int)Math.Ceiling(r / scale), (int)Math.Ceiling(b / scale));
            lines.Add(new OcrLine(text, bounds));
        }
        return lines;
    }

    /// <summary>
    /// Copies the (optionally scaled) image straight into a native SoftwareBitmap.
    /// No managed pixel array is allocated: a per-frame array of tens of MB piled up faster than the GC reclaimed it.
    /// </summary>
    static SoftwareBitmap ToSoftwareBitmap(Bitmap image, double scale)
    {
        Bitmap? scaled = Math.Abs(scale - 1.0) < 0.01 ? null : Resize(image, scale);
        try
        {
            var src = scaled ?? image;
            var soft = new SoftwareBitmap(BitmapPixelFormat.Bgra8, src.Width, src.Height, BitmapAlphaMode.Ignore);
            try
            {
                CopyPixels(src, soft);
                return soft;
            }
            catch
            {
                soft.Dispose();
                throw;
            }
        }
        finally
        {
            scaled?.Dispose();
        }
    }

    static unsafe void CopyPixels(Bitmap src, SoftwareBitmap dst)
    {
        var data = src.LockBits(new Rectangle(0, 0, src.Width, src.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            using var buffer = dst.LockBuffer(BitmapBufferAccessMode.Write);
            var plane = buffer.GetPlaneDescription(0);
            using var reference = buffer.CreateReference();
            byte* target = GetBytePointer(reference, out uint capacity);
            long rowBytes = src.Width * 4L;
            for (int y = 0; y < src.Height; y++)
            {
                long offset = plane.StartIndex + (long)y * plane.Stride;
                Buffer.MemoryCopy((byte*)data.Scan0 + (long)y * data.Stride, target + offset, capacity - offset, rowBytes);
            }
        }
        finally
        {
            src.UnlockBits(data);
        }
    }

    /// <summary>Raw pointer to a WinRT memory buffer through the COM interface IMemoryBufferByteAccess.</summary>
    static unsafe byte* GetBytePointer(IMemoryBufferReference reference, out uint capacity)
    {
        var unknown = ((WinRT.IWinRTObject)reference).NativeObject.ThisPtr;
        var iid = new Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D");
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in iid, out var access));
        try
        {
            // vtable: QueryInterface, AddRef, Release, GetBuffer(byte** value, uint* capacity)
            var getBuffer = (delegate* unmanaged[Stdcall]<IntPtr, byte**, uint*, int>)(*(void***)access)[3];
            byte* value;
            uint size;
            Marshal.ThrowExceptionForHR(getBuffer(access, &value, &size));
            capacity = size;
            return value;
        }
        finally
        {
            Marshal.Release(access);
        }
    }

    static Bitmap Resize(Bitmap src, double scale)
    {
        int w = Math.Max(1, (int)Math.Round(src.Width * scale));
        int h = Math.Max(1, (int)Math.Round(src.Height * scale));
        var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(dst);
        g.InterpolationMode = InterpolationMode.Bilinear; // same OCR accuracy as bicubic in tests, several times faster
        g.DrawImage(src, new Rectangle(0, 0, w, h));
        return dst;
    }
}
