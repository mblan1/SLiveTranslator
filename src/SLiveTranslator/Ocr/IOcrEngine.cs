using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;

namespace SLiveTranslator.Ocr;

public interface IOcrEngine
{
    /// <summary>BCP-47 tag of the recognizer language, e.g. "en-US".</summary>
    string LanguageTag { get; }

    /// <param name="maxUpscale">Upscale factor for small text. 1 is fastest; 2 reads small UI text better.</param>
    Task<IReadOnlyList<OcrLine>> RecognizeAsync(Bitmap image, CancellationToken ct, double maxUpscale = 2.0);
}
