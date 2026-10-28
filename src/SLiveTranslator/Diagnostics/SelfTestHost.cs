using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SLiveTranslator.Hotkeys;
using SLiveTranslator.Models;
using SLiveTranslator.Translation;
using SLiveTranslator.Translation.Providers;
using SLiveTranslator.UI;

namespace SLiveTranslator.Diagnostics;

/// <summary>
/// The app as the main window sees it, for --selftest: default settings, the real providers (nothing is checked or
/// sent: no key is set), and game profiles in the output folder, so the user's data folder is never written.
/// </summary>
internal sealed class SelfTestHost(string dir) : ISettingsHost
{
    public AppSettings Current { get; private set; } = new() { General = { FirstRunDone = true } };

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

    public GameProfileStore Profiles { get; } = new(Path.Combine(dir, "Profiles"));

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
}
