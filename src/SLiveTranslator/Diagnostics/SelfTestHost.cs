using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SLiveTranslator.Hotkeys;
using SLiveTranslator.Models;
using SLiveTranslator.Ocr;
using SLiveTranslator.Services;
using SLiveTranslator.Translation;
using SLiveTranslator.Translation.Providers;
using SLiveTranslator.UI;

namespace SLiveTranslator.Diagnostics;

/// <summary>
/// The app as the main window sees it, for --selftest: default settings, the real providers (nothing is checked or
/// sent: no key is set), and game profiles, a sample cache and a pretend AppData in the output folder, so the user's
/// data folder and the registry are never written. Hotkeys are only validated, never registered.
/// </summary>
internal sealed class SelfTestHost : ISettingsHost
{
    readonly CachedTranslator _cache;

    public SelfTestHost(string dir)
    {
        Profiles = new GameProfileStore(Path.Combine(dir, "Profiles"));
        DataFolders = new DataFolderMover(Path.Combine(dir, "AppData"), Path.GetDirectoryName(Environment.ProcessPath));
        _cache = new CachedTranslator(new NoTranslator(), Path.Combine(dir, "cache.json"));
        _cache.Set(ProviderKeys.Free, "en", "vi", "Wind's howling.", "Gió đang rít.");
        _cache.SetUserOverride("en", "vi", "Roach", "Roach");
        _cache.Set("claude/claude-opus-5-5", "en", "vi", "We should find shelter before nightfall.", "Ta nên tìm chỗ trú trước khi trời tối.");
    }

    /// <summary>Answers nothing: the self-test translates nothing.</summary>
    sealed class NoTranslator : ITranslator
    {
        public string Name => ProviderKeys.Free;

        public Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string sourceLang, string targetLang, CancellationToken ct) =>
            throw new InvalidOperationException("The self-test sends no translation.");
    }

    /// <summary>An in-memory Run key: the self-test must never write the real one.</summary>
    sealed class NoRegistry : IRegistry
    {
        readonly Dictionary<string, string> _values = new();
        public string? GetValue(string name) => _values.GetValueOrDefault(name);
        public void SetValue(string name, string value) => _values[name] = value;
        public void DeleteValue(string name) => _values.Remove(name);
    }

    /// <summary>Default settings with a subtitle region at the bottom of the primary monitor, so the region pictures show something.</summary>
    public AppSettings Current { get; private set; } = new()
    {
        General = { FirstRunDone = true },
        Subtitles = { Region = SampleRegion() },
    };

    static RegionSettings SampleRegion()
    {
        var b = System.Windows.Forms.Screen.PrimaryScreen?.Bounds ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
        return new RegionSettings(b.Left + b.Width / 5, b.Bottom - b.Height * 18 / 100, b.Width * 3 / 5, b.Height / 8);
    }

    public SaveResult Save(AppSettings settings, SettingsChange changes)
    {
        Current = settings;
        return new SaveResult(true, []);
    }

    public event EventHandler? SettingsChanged
    {
        add { }
        remove { }
    }

    public event EventHandler? StateChanged
    {
        add { }
        remove { }
    }

    public IReadOnlyList<ITranslationProvider> Providers { get; } =
        new ProviderRegistry().Providers.Select(p => p.Provider).OfType<ITranslationProvider>().ToList();

    public GameProfileStore Profiles { get; }

    public Task<TestTranslation> TestTranslationAsync(AppSettings settings, string line, CancellationToken ct) =>
        throw new InvalidOperationException("The self-test sends no translation.");

    public bool SubtitlesOn => false;

    public void SetSubtitles(bool on)
    {
    }

    public ProviderStatus ProviderStatus => new(Loc.Instance["Home.StatusReady"], true);
    public FreeServiceStatus FreeServices => new(null, 0, MyMemoryTranslator.DailyLimitFor(""));
    public HotkeyResult? HotkeyState(HotkeyAction action) => null;

    public void SelectRegion()
    {
    }

    public void TranslateScreen()
    {
    }

    public void ToggleHistory()
    {
    }

    public void PreviewOverlay(OverlaySettings look)
    {
    }

    public void SuspendHotkeys()
    {
    }

    public void ResumeHotkeys()
    {
    }

    public HotkeyResult ProbeHotkey(string gesture, HotkeyAction action) => HotkeyManager.Validate(gesture).Result;

    public bool HotkeyTrial { get; set; }

    public event EventHandler<HotkeyAction>? HotkeyPressed
    {
        add { }
        remove { }
    }

    public Task SendTestNotices(NotificationSettings look) => Task.CompletedTask;

    /// <summary>The folder the run uses (the portable copy's), only read.</summary>
    public DataPaths DataPaths => DataPaths.Current;

    public DataFolderMover DataFolders { get; }
    public CachedTranslator? Cache => _cache;

    public void FlushData()
    {
    }

    public void UseDataFolder(DataPaths paths)
    {
    }

    public void Restart()
    {
    }

    public void OpenFolder(string path)
    {
    }

    public StartupRegistration Startup { get; } = new(new NoRegistry(), Environment.ProcessPath);

    public void SetStartWithWindows(bool on) => Startup.SetEnabled(on);

    public IReadOnlyList<string> OcrLanguages => WindowsOcrEngine.AvailableLanguages().Select(l => l.Tag).ToList();

    public void ApplyLanguage(string setting)
    {
    }

    public IReadOnlyList<SubtitleRecord> HistoryRecords(int lines) =>
    [
        new(1, "Wind's howling.", "Gió đang rít.", ProviderKeys.Free),
        new(2, "We should find shelter before nightfall.", "Ta nên tìm chỗ trú trước khi trời tối.", "claude/claude-opus-5-5"),
        new(3, "Roach is tired, and so am I.", null, null),
    ];

    public (string Src, string Tgt) Languages => ("en", "vi");

    public void ShowFix(string source, string translation)
    {
    }
}
