using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using SLiveTranslator.Ocr;

namespace SLiveTranslator.Diagnostics;

/// <summary>
/// --bench-subtitles: replays the scripted conversations through the subtitle pipeline with the real OCR and reports
/// what each frame costs, next to the targets.
/// </summary>
internal static class SubtitleBench
{
    public const string ReportFile = "bench_subtitles.txt";

    public static async Task<int> RunAsync(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var ocr = WindowsOcrEngine.Create("en-US");
        await SubtitleReplay.RunAsync(SubtitleScript.Hold(), still: true, ocr); // warms up the OCR engine and the JIT

        var report = new List<string>
        {
            $"bench-subtitles {DateTime.Now:yyyy-MM-dd HH:mm}  OCR {ocr.LanguageTag}, capture {SubtitleReplay.Capture.Width}x{SubtitleReplay.Capture.Height}, " +
            $"region {SubtitleReplay.Region.Width} px wide, poll {SubtitleReplay.IntervalMs} ms, {Environment.ProcessorCount} logical CPUs",
            "",
            string.Format(CultureInfo.InvariantCulture, "{0,-20}{1,7}{2,7}{3,7}{4,10}{5,12}{6,10}{7,12}{8,11}{9,10}{10,8}{11,10}",
                "scenario", "frames", "passes", "recog", "recog/fr", "recog/pass", "ms/pass", "ms/no-pass", "passes/s*", "KB/frame", "lines", "words"),
        };
        foreach (var (name, script, still, scene) in new[]
                 {
                     ("conversation-moving", SubtitleScript.Conversation(), false, 0),
                     ("conversation-still", SubtitleScript.StillConversation(), true, 0),
                     ("conversation-bright", SubtitleScript.Conversation(), true, SubtitlePainter.BrightScene),
                     ("hold-moving", SubtitleScript.Hold(), false, 0),
                     ("hold-still", SubtitleScript.Hold(), true, 0),
                     ("silence-moving", SubtitleScript.Silence(), false, 0),
                 })
        {
            var o = await SubtitleReplay.RunAsync(script, still, ocr, stillScene: scene);
            var c = o.Cost;
            report.Add(string.Format(CultureInfo.InvariantCulture, "{0,-20}{1,7}{2,7}{3,7}{4,10:F2}{5,12:F2}{6,10:F1}{7,12:F1}{8,11:F2}{9,10}{10,8}{11,10}",
                name, c.Frames, c.ChangedFrames, c.Recognitions, (double)c.Recognitions / c.Frames, c.RecognitionsPerChangedFrame,
                c.MsPerChangedFrame, c.MsPerUnchangedFrame, c.UnchangedReadsPerSecond, c.BytesPerFrame / 1024,
                $"{o.Caught}/{o.Lines}", $"{o.Found}/{o.Words}"));
        }
        report.AddRange(
        [
            "",
            "passes = frames that were read (OCR ran); recog = OCR recognitions (a pass reads the frame, its white-text mask, or both).",
            "ms/pass = pipeline time of a frame that was read (mask, signature, crop, OCR, tracking); ms/no-pass = a frame that was not.",
            "* OCR passes per second while the same line stays on screen after it was confirmed (steady state; the hold rows are 10 s).",
            "conversation-bright: the moving conversation's lines over a still, bright scene (pale sky, patches as light as the text).",
            "silence-moving: 10 s of a panning scene with no subtitle, in a new session: until the mask has won five times the region's pixels",
            "decide, so the plain frame is read; once it has (after a few lines), a frame without white text costs no OCR.",
            "Targets: <= 15 ms per changed frame, < 1 OCR pass/s while the text is unchanged, <= 1.3 recognitions per changed frame",
            "(every frame of a moving scene changes, so recog/fr of conversation-moving is that figure; M1 read every such frame twice: 2.0).",
        ]);
        string path = Path.Combine(outDir, ReportFile);
        File.WriteAllLines(path, report);
        foreach (var line in report) Console.WriteLine(line);
        return 0;
    }
}
