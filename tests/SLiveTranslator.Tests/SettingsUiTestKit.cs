using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SLiveTranslator.Hotkeys;
using SLiveTranslator.Models;
using SLiveTranslator.Services;
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

    /// <summary>Every question asked, confirmations and two-answer questions alike, in order.</summary>
    public List<string> Questions { get; } = new();

    public bool Confirm { get; set; } = true;

    /// <summary>The answers to the two-answer questions, in order; Cancel once they run out.</summary>
    public Queue<DialogAnswer> Answers { get; } = new();

    public string? OpenFile { get; set; }
    public string? SaveFile { get; set; }
    public string? Folder { get; set; }
    public string? Copied { get; private set; }
    public bool ClipboardBusy { get; set; }

    public void Show(string title, string text, MessageKind kind) => Messages.Add((title, text, kind));

    public Task<bool> ConfirmAsync(string title, string text, string yes, string no)
    {
        Questions.Add(text);
        return Task.FromResult(Confirm);
    }

    public Task<DialogAnswer> AskAsync(string title, string text, string primary, string secondary, string cancel)
    {
        Questions.Add(text);
        return Task.FromResult(Answers.Count > 0 ? Answers.Dequeue() : DialogAnswer.Cancel);
    }

    public string? PickOpenFile(string filter) => OpenFile;
    public string? PickSaveFile(string fileName, string filter) => SaveFile;
    public string? PickFolder(string? start) => Folder;

    public bool CopyText(string text)
    {
        if (ClipboardBusy) return false;
        Copied = text;
        return true;
    }
}

/// <summary>A Run key in memory: the tests never write the real registry.</summary>
sealed class FakeRegistry : IRegistry
{
    public Dictionary<string, string> Values { get; } = new();
    public string? GetValue(string name) => Values.GetValueOrDefault(name);
    public void SetValue(string name, string value) => Values[name] = value;
    public void DeleteValue(string name) => Values.Remove(name);
}

/// <summary>
/// The app as the main window sees it: settings in memory, profiles in a temp folder, providers of the test. Its data
/// folder, AppData and exe folder are folders beside the profiles' (under the test's temp folder).
/// </summary>
sealed class FakeHost(string profilesDir, params ITranslationProvider[] providers) : ISettingsHost
{
    public const string ExePath = @"C:\Program Files\SLive Translator\SLiveTranslator.exe";

    static string Beside(string profilesDir, string name) => Path.Combine(Path.GetDirectoryName(profilesDir)!, name);

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
    public GameProfileStore Profiles { get; } = TestProfiles.Store(profilesDir);

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
    public int RegionSelections { get; private set; }
    public void SelectRegion() => RegionSelections++;

    /// <summary>The settings each page-started pick began from (a copy, as the app clones them).</summary>
    public List<AppSettings> PickedWith { get; } = new();

    public void SelectRegions(RegionPickMode mode, AppSettings working)
    {
        RegionSelections++;
        PickedWith.Add(SettingsStore.Clone(working));
    }

    public void TranslateScreen()
    {
    }

    public void ToggleHistory()
    {
    }

    public OverlaySettings? Previewed { get; private set; }
    public void PreviewOverlay(OverlaySettings look) => Previewed = look;

    // ---- hotkeys ----

    public Dictionary<HotkeyAction, HotkeyResult> States { get; } = new();
    public bool Suspended { get; private set; }
    public int Suspends { get; private set; }
    public List<(string Gesture, HotkeyAction Action)> Probes { get; } = new();

    /// <summary>What a probe answers; by default the gesture's rules alone (no other program holds anything).</summary>
    public Func<string, HotkeyResult> ProbeResult { get; set; } = gesture => HotkeyManager.Validate(gesture).Result;

    public HotkeyResult? HotkeyState(HotkeyAction action) => States.TryGetValue(action, out var state) ? state : HotkeyResult.Ok;

    public void SuspendHotkeys()
    {
        Suspended = true;
        Suspends++;
    }

    public void ResumeHotkeys() => Suspended = false;

    public HotkeyResult ProbeHotkey(string gesture, HotkeyAction action)
    {
        Probes.Add((gesture, action));
        return ProbeResult(gesture);
    }

    public bool HotkeyTrial { get; set; }
    public event EventHandler<HotkeyAction>? HotkeyPressed;
    public void Press(HotkeyAction action) => HotkeyPressed?.Invoke(this, action);

    // ---- notifications ----

    public List<NotificationSettings> TestedNotices { get; } = new();

    public Task SendTestNotices(NotificationSettings look)
    {
        TestedNotices.Add(look);
        return Task.CompletedTask;
    }

    // ---- storage ----

    public DataPaths DataPaths { get; set; } = DataPaths.At(Beside(profilesDir, "Data"), isPortable: false);
    public DataFolderMover DataFolders { get; set; } = new(Beside(profilesDir, "AppData"), Beside(profilesDir, "Exe"));
    public CachedTranslator? Cache { get; set; }
    public int Flushes { get; private set; }
    public List<DataPaths> UsedFolders { get; } = new();
    public int Restarts { get; private set; }
    public List<string> Opened { get; } = new();

    public void FlushData() => Flushes++;

    public void UseDataFolder(DataPaths paths)
    {
        UsedFolders.Add(paths);
        DataPaths = paths;
    }

    public void Restart() => Restarts++;
    public void OpenFolder(string path) => Opened.Add(path);

    // ---- general ----

    public FakeRegistry Registry { get; } = new();
    public StartupRegistration Startup => field ??= new StartupRegistration(Registry, ExePath);

    public void SetStartWithWindows(bool on)
    {
        Startup.SetEnabled(on);
        Current.General.StartWithWindows = on;
    }

    public IReadOnlyList<string> OcrLanguages { get; set; } = ["en-US"];
    public List<string> AppliedLanguages { get; } = new();
    public void ApplyLanguage(string setting) => AppliedLanguages.Add(setting);

    // ---- history ----

    public List<SubtitleRecord> History { get; } = new();
    public IReadOnlyList<SubtitleRecord> HistoryRecords(int lines) => History.TakeLast(lines).ToList();
    public (string Src, string Tgt) Languages => ("en", "vi");
    public List<(string Source, string Translation)> Fixes { get; } = new();
    public void ShowFix(string source, string translation) => Fixes.Add((source, translation));
    public void RemoveHistory(int id) => History.RemoveAll(r => r.Id == id);
    public int HistoryClears { get; private set; }

    public void ClearHistory()
    {
        History.Clear();
        HistoryClears++;
    }
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
