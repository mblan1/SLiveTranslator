using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SLiveTranslator.Hotkeys;
using SLiveTranslator.Models;
using SLiveTranslator.Translation;
using SLiveTranslator.Translation.Providers;
using SLiveTranslator.UI;

namespace SLiveTranslator.Tests;

/// <summary>A provider whose key checks wait until the test answers them; nothing leaves the PC.</summary>
sealed class FakeProvider(string id = ProviderIds.OpenAI, ProviderKind kind = ProviderKind.LLM, params ProviderFieldSpec[] fields) : ITranslationProvider
{
    public sealed record Check(ProviderCredentials Credentials, CancellationToken Token, TaskCompletionSource<ProviderCheck> Answer)
    {
        public void Reply(ProviderCheck result) => Answer.TrySetResult(result);
    }

    public string Id => id;
    public string DisplayName => "Fake " + id;
    public ProviderKind Kind => kind;
    public IReadOnlyList<ProviderFieldSpec> Fields { get; } = fields.Length > 0 ? fields : [new(ProviderField.ApiKey, Required: true)];
    public ProfileHandling Profile => ProfileHandling.Prompt;
    public string DefaultModel => "";
    public List<Check> Checks { get; } = new();

    public Task<ProviderCheck> CheckAsync(ProviderCredentials credentials, CancellationToken ct)
    {
        var answer = new TaskCompletionSource<ProviderCheck>();
        ct.Register(() => answer.TrySetCanceled(ct));
        Checks.Add(new Check(credentials, ct, answer));
        return answer.Task;
    }

    public ITranslator Create(ProviderConfig config) => throw new NotSupportedException("the settings tests translate nothing");
}

/// <summary>A clock the test moves on: a wait ends when the test has advanced past it, or is cancelled.</summary>
sealed class FakeScheduler
{
    readonly List<(TimeSpan Due, TaskCompletionSource Done)> _waits = new();
    TimeSpan _now;

    public int Waiting => _waits.Count(w => !w.Done.Task.IsCompleted);

    public Task Delay(TimeSpan wait, CancellationToken ct)
    {
        var done = new TaskCompletionSource();
        ct.Register(() => done.TrySetCanceled(ct));
        _waits.Add((_now + wait, done));
        return done.Task;
    }

    public void Advance(TimeSpan by)
    {
        _now += by;
        foreach (var wait in _waits.Where(w => w.Due <= _now).ToList())
        {
            _waits.Remove(wait);
            wait.Done.TrySetResult();
        }
    }

    public void Advance(int ms) => Advance(TimeSpan.FromMilliseconds(ms));
}

/// <summary>Answers questions as told and records what was said.</summary>
sealed class FakePrompts : IUserPrompts
{
    public List<(string Title, string Text, MessageKind Kind)> Messages { get; } = new();
    public bool Confirm { get; set; } = true;
    public string? OpenFile { get; set; }
    public string? SaveFile { get; set; }

    public void Show(string title, string text, MessageKind kind) => Messages.Add((title, text, kind));
    public Task<bool> ConfirmAsync(string title, string text, string yes, string no) => Task.FromResult(Confirm);
    public string? PickOpenFile(string filter) => OpenFile;
    public string? PickSaveFile(string fileName, string filter) => SaveFile;
}

/// <summary>The app as the main window sees it: settings in memory, profiles in a temp folder, providers of the test.</summary>
sealed class FakeHost(string profilesDir, params ITranslationProvider[] providers) : ISettingsHost
{
    public AppSettings Current { get; set; } = new();
    public List<(AppSettings Settings, SettingsChange Changes)> Saves { get; } = new();
    public bool FailSave { get; set; }

    public SaveResult Save(AppSettings settings, SettingsChange changes)
    {
        if (FailSave) return new SaveResult(false, ["the disk is full"]);
        Saves.Add((settings, changes));
        Current = settings;
        return new SaveResult(true, []);
    }

    public event EventHandler? SettingsChanged;
    public event EventHandler? StateChanged;

    /// <summary>The app changed and saved settings of its own (a region selected by hotkey).</summary>
    public void ChangeOutside(Action<AppSettings> change)
    {
        change(Current);
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    public IReadOnlyList<ITranslationProvider> Providers { get; } = providers;
    public GameProfileStore Profiles { get; } = new(profilesDir);

    public AppSettings? Tested { get; private set; }

    public Task<TestTranslation> TestTranslationAsync(AppSettings settings, string line, CancellationToken ct)
    {
        Tested = settings;
        return Task.FromResult(new TestTranslation("Gió đang rít.", "fake", null));
    }

    public bool SubtitlesOn { get; set; }
    public void SetSubtitles(bool on) => SubtitlesOn = on;
    public ProviderStatus ProviderStatus { get; set; } = new("Ready", true);
    public FreeServiceStatus FreeServices { get; set; } = new(null, 0, 5_000);
    public HotkeyResult? HotkeyState(HotkeyAction action) => HotkeyResult.Ok;
    public int RegionSelections { get; private set; }
    public void SelectRegion() => RegionSelections++;

    public void TranslateScreen()
    {
    }

    public void ToggleHistory()
    {
    }

    public OverlaySettings? Previewed { get; private set; }
    public void PreviewOverlay(OverlaySettings look) => Previewed = look;
}

static class SettingsUi
{
    /// <summary>
    /// Runs <paramref name="test"/> without a synchronization context, so an awaited fake check or wait continues the
    /// moment the test completes it, as the UI thread would.
    /// </summary>
    public static void Run(Action test)
    {
        var context = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            test();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(context);
        }
    }

    public static ProviderCheck Ok(params ModelOption[] options) => new(CheckStatus.Ok, options, null, "");
}
