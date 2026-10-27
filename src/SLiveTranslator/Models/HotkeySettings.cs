using System.Collections.Generic;

namespace SLiveTranslator.Models;

public enum HotkeyAction { TranslateScreen, ToggleSubtitles, SelectRegion, Clear, History }

public sealed class HotkeySettings : SettingsSection
{
    public static readonly IReadOnlyDictionary<HotkeyAction, string> Defaults = new Dictionary<HotkeyAction, string>
    {
        [HotkeyAction.TranslateScreen] = "Ctrl+Shift+T",
        [HotkeyAction.ToggleSubtitles] = "Ctrl+Shift+S",
        [HotkeyAction.SelectRegion] = "Ctrl+Shift+R",
        [HotkeyAction.Clear] = "Ctrl+Shift+X",
        [HotkeyAction.History] = "Ctrl+Shift+H",
    };

    /// <summary>Gesture per action, e.g. "Ctrl+Shift+T".</summary>
    public Dictionary<HotkeyAction, string> Map { get; set; } = new(Defaults);

    /// <summary>Actions switched off. Their gesture stays in <see cref="Map"/> so switching back on restores it.</summary>
    public HashSet<HotkeyAction> Disabled { get; set; } = new();

    /// <summary>The gesture for <paramref name="action"/>; the default when an older or hand-edited file lacks it.</summary>
    public string this[HotkeyAction action]
    {
        get => Map.TryGetValue(action, out var gesture) && gesture is not null ? gesture : Defaults[action];
        set => Map[action] = value;
    }
}
