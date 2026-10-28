using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SLiveTranslator.Hotkeys;
using SLiveTranslator.Models;

namespace SLiveTranslator.UI.Pages;

/// <summary>One action on the Hotkeys page: its gesture, whether it is on, and what became of it.</summary>
public sealed class HotkeyRowViewModel : ObservableObject
{
    readonly HotkeysViewModel _page;
    HotkeyResult _state;
    string _detail = "";
    bool _recording;

    internal HotkeyRowViewModel(HotkeysViewModel page, HotkeyAction action)
    {
        _page = page;
        Action = action;
        ResetCommand = new RelayCommand(() => page.Reset(action));
    }

    public HotkeyAction Action { get; }
    public string Name => HotkeyText.ActionName(Action);

    /// <summary>"" when cleared: the action then has no hotkey and is off.</summary>
    public string Gesture
    {
        get => _page.GestureOf(Action);
        set => _page.SetGesture(Action, value ?? "");
    }

    public bool IsEnabled
    {
        get => _page.IsEnabled(Action);
        set => _page.SetEnabled(Action, value);
    }

    public HotkeyResult State => _state;
    public string StateText => HotkeyText.State(_state);
    public bool StateOk => _state == HotkeyResult.Ok;
    public bool StateOff => _state == HotkeyResult.Disabled;
    public bool StateProblem => !StateOk && !StateOff;

    /// <summary>Why it does not work, a warning, or while recording how to record.</summary>
    public string Detail => _recording ? Loc.Instance["Hotkeys.RecordingHint"] : _detail;

    /// <summary>Its recorder has the focus: the app's hotkeys are off meanwhile.</summary>
    public bool IsRecording
    {
        get => _recording;
        set
        {
            if (!SetProperty(ref _recording, value)) return;
            _page.Recording(value);
            OnPropertyChanged(nameof(Detail));
        }
    }

    public RelayCommand ResetCommand { get; }

    internal void Show(HotkeyResult state, string detail)
    {
        _state = state;
        _detail = detail;
        OnPropertyChanged(string.Empty);
    }
}

/// <summary>
/// The Hotkeys page: a recorder per action, an on/off switch, the state of each gesture and Reset. The state of an
/// edited gesture is checked as it is recorded: the rules of <see cref="HotkeyManager.Validate(string)"/>, the other
/// actions on the page, and a probe of whether another program holds it. The edits apply on Save; while a recorder has
/// the focus the app's hotkeys are off, and they come back when it loses the focus or the window closes.
/// </summary>
public sealed class HotkeysViewModel : SettingsPageViewModel
{
    readonly ISettingsHost _host;
    readonly Dictionary<(HotkeyAction, string), HotkeyResult> _probes = new();
    bool _suspended;

    public HotkeysViewModel(SettingsViewModel root, ISettingsHost host) : base(root)
    {
        _host = host;
        Rows = Enum.GetValues<HotkeyAction>().Select(a => new HotkeyRowViewModel(this, a)).ToList();
        ResetAllCommand = new RelayCommand(ResetAll);
        UpdateStates();
    }

    HotkeySettings H => Working.Hotkeys;

    public IReadOnlyList<HotkeyRowViewModel> Rows { get; }
    public RelayCommand ResetAllCommand { get; }

    internal string GestureOf(HotkeyAction action) => H[action];
    internal bool IsEnabled(HotkeyAction action) => !H.Disabled.Contains(action);

    /// <summary>A recorded gesture turns the action on; a cleared one turns it off.</summary>
    internal void SetGesture(HotkeyAction action, string gesture)
    {
        gesture = HotkeyGesture.TryParse(gesture, out var parsed) ? parsed.ToString() : gesture.Trim();
        if (gesture == H[action] && IsEnabled(action) == gesture.Length > 0) return;
        H[action] = gesture;
        if (gesture.Length == 0) H.Disabled.Add(action);
        else H.Disabled.Remove(action);
        Changed();
    }

    /// <summary>Switched back on without a gesture, the action gets its default one.</summary>
    internal void SetEnabled(HotkeyAction action, bool on)
    {
        if (on == IsEnabled(action)) return;
        if (!on)
        {
            H.Disabled.Add(action);
        }
        else
        {
            H.Disabled.Remove(action);
            if (H[action].Length == 0) H[action] = HotkeySettings.Defaults[action];
        }
        Changed();
    }

    internal void Reset(HotkeyAction action)
    {
        H[action] = HotkeySettings.Defaults[action];
        H.Disabled.Remove(action);
        Changed();
    }

    void ResetAll()
    {
        foreach (var action in Enum.GetValues<HotkeyAction>())
        {
            H[action] = HotkeySettings.Defaults[action];
            H.Disabled.Remove(action);
        }
        Changed();
    }

    void Changed()
    {
        Root.Edited();
        UpdateStates();
    }

    /// <summary>A recorder took or lost the focus.</summary>
    internal void Recording(bool on)
    {
        if (on == _suspended) return;
        _suspended = on;
        if (on) _host.SuspendHotkeys();
        else _host.ResumeHotkeys();
    }

    /// <summary>The window closes: the hotkeys work again, whatever had the focus.</summary>
    public void EndRecording()
    {
        foreach (var row in Rows) row.IsRecording = false;
        Recording(false);
    }

    /// <summary>The page is on screen: another program may have taken a gesture meanwhile.</summary>
    public void PageShown()
    {
        _probes.Clear();
        UpdateStates();
    }

    public override void Reload()
    {
        base.Reload();
        UpdateStates();
    }

    /// <summary>The app's language changed: names, states and reasons are written again.</summary>
    public void Relocalize() => UpdateStates();

    /// <summary>
    /// Checks each enabled gesture in the order the app registers them: of two actions with one gesture the first gets
    /// it. A gesture the app has registered for the action shows how that went; any other one is probed.
    /// </summary>
    void UpdateStates()
    {
        var inUse = _host.Current.Hotkeys;
        var taken = new HashSet<HotkeyGesture>();
        foreach (var row in Rows)
        {
            var action = row.Action;
            string gesture = H[action];
            if (!IsEnabled(action))
            {
                row.Show(HotkeyResult.Disabled, "");
                continue;
            }
            var validation = HotkeyManager.Validate(gesture);
            if (validation.Result != HotkeyResult.Ok)
            {
                row.Show(validation.Result, HotkeyText.Explain(validation));
                continue;
            }
            HotkeyGesture.TryParse(gesture, out var parsed);
            if (!taken.Add(parsed))
            {
                row.Show(HotkeyResult.Duplicate, Loc.Instance["Hotkey.Why.Duplicate"]);
                continue;
            }
            bool registered = !inUse.Disabled.Contains(action) && HotkeyGesture.TryParse(inUse[action], out var current) && current == parsed;
            var state = registered && _host.HotkeyState(action) is { } result and not HotkeyResult.Duplicate ? result : Probe(action, gesture);
            row.Show(state, state == HotkeyResult.Ok ? HotkeyText.Explain(validation) : Loc.Instance["Hotkey.Why." + state]);
        }
    }

    /// <summary>Another action of the app holding it now is no conflict: the page found the real ones already.</summary>
    HotkeyResult Probe(HotkeyAction action, string gesture)
    {
        if (!_probes.TryGetValue((action, gesture), out var result))
            _probes[(action, gesture)] = result = _host.ProbeHotkey(gesture, action);
        return result == HotkeyResult.Duplicate ? HotkeyResult.Ok : result;
    }
}
