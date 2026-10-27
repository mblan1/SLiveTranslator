using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SLiveTranslator.Ocr;
using SLiveTranslator.Services;
using SLiveTranslator.Translation;
using Xunit;

namespace SLiveTranslator.Tests;

/// <summary>The conversation sent along with each subtitle line, and the game profile that goes with it.</summary>
public class SubtitleContextTests
{
    /// <summary>Records every request and answers "vi:" plus each line.</summary>
    sealed class RequestRecorder : ITranslator
    {
        readonly object _gate = new();
        readonly List<TranslateRequest> _requests = new();

        public string Name => "free-mt";

        public List<TranslateRequest> Requests
        {
            get { lock (_gate) return _requests.ToList(); }
        }

        public Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string sourceLang, string targetLang, CancellationToken ct) =>
            throw new InvalidOperationException("the watcher sends requests");

        public Task<TranslateResult> TranslateAsync(TranslateRequest request, CancellationToken ct)
        {
            lock (_gate) _requests.Add(request);
            return Task.FromResult(new TranslateResult(request.Lines.Select(l => "vi:" + l).ToList(), null, "google") { Provider = "free-mt" });
        }
    }

    sealed class Harness
    {
        /// <param name="profiles">The profile for a process name; by default <paramref name="profile"/> for any.</param>
        /// <param name="polling">Which window is the game, as Start would make it.</param>
        public Harness(int contextLines = 6, GameProfile? profile = null, Func<string?, GameProfile?>? profiles = null,
            IWindowProbe? windows = null, PollingPolicy? polling = null)
        {
            Watcher = new SubtitleWatcher(() => Ocr, () => Translator, () => new WatcherConfig("en", "vi", 300, 1.3) { ContextLines = contextLines }, Sink)
            {
                Delay = (_, _) => Task.CompletedTask,
                Clock = () => Now,
                Profiles = profiles ?? (_ => profile),
                Windows = windows ?? SystemWindowProbe.Instance,
                Polling = polling,
            };
        }

        public long Now { get; set; }
        public ScriptedOcr Ocr { get; } = new();
        public RequestRecorder Translator { get; } = new();
        public RecordingSink Sink { get; } = new();
        public SubtitleWatcher Watcher { get; }

        /// <summary>A new line, translated before the method returns.</summary>
        public TranslateRequest Line(int localId, string text)
        {
            int before = Translator.Requests.Count;
            Watcher.Emit(1, localId, text, false, update: false, CancellationToken.None);
            Assert.True(Sink.WaitFor(_ => Translator.Requests.Count > before && Sink.Providers.Count > before));
            return Translator.Requests[^1];
        }
    }

    [Fact]
    public void Each_line_goes_out_with_the_lines_shown_before_it()
    {
        var h = new Harness(contextLines: 3);

        Assert.Empty(h.Line(1, "Ciri, get down here!").Context);
        var second = h.Line(2, "Coming!");
        for (int i = 3; i <= 6; i++) h.Line(i, $"Line {i}.");
        var last = h.Line(7, "But you were asleep, Uncle Vesemir.");

        Assert.Equal([new ContextLine("Ciri, get down here!", "vi:Ciri, get down here!")], second.Context);
        Assert.Equal(["Line 4.", "Line 5.", "Line 6."], last.Context.Select(c => c.Source)); // the latest three, oldest first
    }

    [Fact]
    public void A_line_that_grew_is_translated_with_the_lines_before_it_not_with_its_own_earlier_reading()
    {
        var h = new Harness();
        h.Line(1, "Ciri, get down here!");
        h.Line(2, "Geralt: The griffin");

        h.Watcher.Emit(1, 2, "Geralt: The griffin has been hunting here.", false, update: true, CancellationToken.None);
        Assert.True(h.Sink.WaitFor(_ => h.Translator.Requests.Count == 3));

        Assert.Equal(["Ciri, get down here!"], h.Translator.Requests[2].Context.Select(c => c.Source));
    }

    [Fact]
    public void A_minute_without_a_line_starts_a_new_conversation()
    {
        var h = new Harness();
        h.Line(1, "Ciri, get down here!");
        h.Now += 59_000;
        Assert.Single(h.Line(2, "Coming!").Context);

        h.Now += 60_001;
        Assert.Empty(h.Line(3, "Where is the griffin?").Context);
        Assert.Single(h.Line(4, "Out by the old mill.").Context);
    }

    [Fact]
    public void Context_off_sends_none()
    {
        var h = new Harness(contextLines: 0);
        h.Line(1, "Ciri, get down here!");

        Assert.Empty(h.Line(2, "Coming!").Context);
    }

    [Fact]
    public void The_profile_goes_out_with_the_line_and_the_provider_reaches_the_overlay()
    {
        var profile = TestProfiles.Witcher3();
        var h = new Harness(profile: profile);

        Assert.Same(profile, h.Line(1, "Ciri, get down here!").Profile);
        Assert.Equal(["free-mt"], h.Sink.Providers);
    }

    [Fact]
    public void The_game_found_while_subtitles_run_picks_the_profile_by_its_process_name()
    {
        // Pausing is off: finding the game is then the only way the Witcher profile applies (the setting is "auto").
        var probe = new GameProbe();
        var policy = new PollingPolicy(probe, GameProbe.Own, new Rectangle(0, 0, 600, 100));
        var witcher = TestProfiles.Witcher3();
        var asked = new List<string?>();
        var h = new Harness(profiles: name =>
        {
            lock (asked) asked.Add(name);
            return name == "witcher3" ? witcher : null;
        }, windows: probe, polling: policy);

        Assert.Null(h.Line(1, "Loading...").Profile); // only the desktop showed the region
        probe.InFront = (new IntPtr(0x1234), GameProbe.Game); // the game started and shows it
        Assert.False(policy.GameAway(pauseWhenUnfocused: false));
        var line = h.Line(2, "Ciri, get down here!");

        Assert.Same(witcher, line.Profile);
        Assert.Equal([null, "witcher3"], asked);
    }

    /// <summary>One program in front that shows the region too; process 200 is the game.</summary>
    sealed class GameProbe : IWindowProbe
    {
        public const int Own = 100, Game = 200;

        public (IntPtr Window, int ProcessId) InFront { get; set; } = (IntPtr.Zero, 0);

        public (IntPtr Window, int ProcessId) Foreground() => InFront;
        public (IntPtr Window, int ProcessId) At(Point screenPoint) => InFront;
        public bool IsMinimized(IntPtr window) => false;
        public bool Exists(IntPtr window) => true;
        public int ShellProcessId() => 1;
        public string? ProcessName(int processId) => processId == Game ? "witcher3" : null;
    }

    [Fact]
    public async Task The_profile_names_repair_possessives_in_what_ocr_read()
    {
        var h = new Harness(profile: TestProfiles.Witcher3());
        var session = h.Watcher.CreateSession(new Rectangle(0, 0, 600, 100), new Rectangle(0, 0, 600, 100));
        h.Ocr.Lines = [new OcrLine("Cirils sword is gone.", new Rectangle(40, 40, 300, 28))];
        using var frame = new Bitmap(600, 100, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(frame)) g.Clear(Color.FromArgb(30, 30, 30));

        var result = await session.ProcessFrameAsync(frame, 0, CancellationToken.None);

        Assert.Equal("Ciri's sword is gone.", result.Reading);
    }
}

public class ContextWindowTests
{
    [Fact]
    public void A_newer_translation_of_a_line_replaces_its_old_one()
    {
        var context = new ContextWindow();
        context.LineSeen(1, 0);
        context.Shown(1, "Geralt: The griffin", "Geralt: Con griffin");
        context.Shown(1, "Geralt: The griffin has been hunting here.", "Geralt: Con griffin đã săn ở đây.");

        Assert.Equal([new ContextLine("Geralt: The griffin has been hunting here.", "Geralt: Con griffin đã săn ở đây.")], context.Before(2, 6));
    }

    [Fact]
    public void At_most_the_largest_setting_is_kept()
    {
        var context = new ContextWindow();
        for (int id = 1; id <= 30; id++)
        {
            context.LineSeen(id, id);
            context.Shown(id, $"line {id}", $"dòng {id}");
        }

        Assert.Equal(ContextWindow.MaxLines, context.Before(100, 100).Count);
        Assert.Equal("line 30", context.Before(100, 100)[^1].Source);
    }

    [Fact]
    public void After_a_clear_a_late_translation_of_the_old_conversation_is_not_kept()
    {
        // Another region was selected while a line of the old one was still being translated.
        var context = new ContextWindow();
        context.LineSeen(5, 0);
        context.Clear();
        context.LineSeen(1_000_001, 10);
        context.Shown(5, "old line", "dòng cũ");
        context.Shown(1_000_001, "new line", "dòng mới");

        Assert.Equal(["new line"], context.Before(1_000_002, 6).Select(c => c.Source));
    }
}
