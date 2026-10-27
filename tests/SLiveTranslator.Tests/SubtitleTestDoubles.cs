using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SLiveTranslator.Ocr;
using SLiveTranslator.Services;
using SLiveTranslator.Translation;

namespace SLiveTranslator.Tests;

/// <summary>Records what the watcher reports, from any thread.</summary>
sealed class RecordingSink : ISubtitleSink
{
    public sealed record Detected(int Id, string Text, bool LowConfidence);

    readonly object _gate = new();
    readonly List<object> _events = new();
    readonly List<bool> _translatedLowConfidence = new();
    readonly List<string> _providers = new();

    public List<object> Events
    {
        get { lock (_gate) return _events.ToList(); }
    }

    public List<Detected> DetectedLines => Events.OfType<Detected>().ToList();

    /// <summary>The low-confidence flag of each translation, in order.</summary>
    public List<bool> TranslatedLowConfidence
    {
        get { lock (_gate) return _translatedLowConfidence.ToList(); }
    }
    public List<string> Notices => Events.OfType<string>().Where(e => e.StartsWith("notice: ", StringComparison.Ordinal)).ToList();

    void Add(object e)
    {
        lock (_gate) _events.Add(e);
    }

    public void LineDetected(int id, string original, bool lowConfidence) => Add(new Detected(id, original, lowConfidence));
    /// <summary>The provider key of each translation, in order.</summary>
    public List<string> Providers
    {
        get { lock (_gate) return _providers.ToList(); }
    }

    public void LineTranslated(int id, string translated, bool lowConfidence, string provider)
    {
        lock (_gate)
        {
            _translatedLowConfidence.Add(lowConfidence);
            _providers.Add(provider);
            _events.Add(("translated", id, translated));
        }
    }
    public void LineFailed(int id, string original, string error) => Add(("failed", id, original));
    public void Cleared() => Add("cleared");
    public void Paused(bool paused) => Add(paused ? "paused" : "resumed");
    public void Error(string message) => Add("error: " + message);
    public void Notice(string message) => Add("notice: " + message);

    /// <summary>Waits for background translations: true once the condition holds, false after the timeout.</summary>
    public bool WaitFor(Func<List<object>, bool> condition, int timeoutMs = 5000)
    {
        var clock = Stopwatch.StartNew();
        while (!condition(Events))
        {
            if (clock.ElapsedMilliseconds > timeoutMs) return false;
            Thread.Sleep(5);
        }
        return true;
    }
}

/// <summary>
/// Returns the lines the test set. The watcher recognizes the frame and its white-text mask (dark text on white); a
/// mask is told apart by its pure white centre pixel (test frames are never white there) and gets <see cref="MaskLines"/>
/// when set.
/// </summary>
sealed class ScriptedOcr : IOcrEngine
{
    public IReadOnlyList<OcrLine> Lines { get; set; } = Array.Empty<OcrLine>();
    public IReadOnlyList<OcrLine>? MaskLines { get; set; }
    public Exception? Failure { get; set; }
    public int Calls { get; private set; }
    public int MaskCalls { get; private set; }
    public int PlainCalls => Calls - MaskCalls;
    /// <summary>Size of every image recognized, in order.</summary>
    public List<Size> Images { get; } = new();
    public string LanguageTag => "en-US";

    public Task<IReadOnlyList<OcrLine>> RecognizeAsync(Bitmap image, CancellationToken ct, double maxUpscale = 2.0)
    {
        Calls++;
        Images.Add(image.Size);
        bool mask = image.GetPixel(image.Width / 2, image.Height / 2).ToArgb() == Color.White.ToArgb();
        if (mask) MaskCalls++;
        if (Failure is { } failure) return Task.FromException<IReadOnlyList<OcrLine>>(failure);
        return Task.FromResult(mask && MaskLines is { } m ? m : Lines);
    }
}

/// <summary>Answers with the source text, or fails the first <see cref="FailuresLeft"/> calls with <see cref="Failure"/>.</summary>
sealed class EchoTranslator : ITranslator
{
    readonly object _gate = new();
    readonly List<string> _requests = new();

    public int FailuresLeft { get; set; }
    public Func<Exception> Failure { get; set; } = () => new TranslatorUnavailableException("echo is down");
    public string Name => "echo";

    public List<string> Requests
    {
        get { lock (_gate) return _requests.ToList(); }
    }

    public Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string sourceLang, string targetLang, CancellationToken ct)
    {
        lock (_gate)
        {
            _requests.Add(string.Join("\n", texts));
            if (FailuresLeft > 0)
            {
                FailuresLeft--;
                return Task.FromException<IReadOnlyList<string>>(Failure());
            }
        }
        return Task.FromResult<IReadOnlyList<string>>(texts.Select(t => "vi:" + t).ToList());
    }
}

/// <summary>Holds every request until the test answers it, so translations can arrive in any order.</summary>
sealed class GatedTranslator : ITranslator
{
    readonly object _gate = new();
    readonly List<(string Text, TaskCompletionSource<IReadOnlyList<string>> Answer)> _requests = new();

    public string Name => "gated";

    public int Count
    {
        get { lock (_gate) return _requests.Count; }
    }

    /// <summary>Answers the request with this index (in the order they came) with "vi:" plus its text.</summary>
    public void Answer(int index)
    {
        (string text, var answer) = Request(index);
        answer.SetResult(text.Split('\n').Select(t => "vi:" + t).ToList());
    }

    /// <summary>Answers the request with this index with "" for each of its lines, as an LLM does for noise.</summary>
    public void AnswerEmpty(int index)
    {
        (string text, var answer) = Request(index);
        answer.SetResult(text.Split('\n').Select(_ => "").ToList());
    }

    (string, TaskCompletionSource<IReadOnlyList<string>>) Request(int index)
    {
        lock (_gate) return _requests[index];
    }

    public Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string sourceLang, string targetLang, CancellationToken ct)
    {
        var answer = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) _requests.Add((string.Join("\n", texts), answer));
        return answer.Task;
    }
}
