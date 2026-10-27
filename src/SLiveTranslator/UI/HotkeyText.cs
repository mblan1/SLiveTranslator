using System.Collections.Generic;
using System.Linq;
using SLiveTranslator.Hotkeys;
using SLiveTranslator.Models;

namespace SLiveTranslator.UI;

/// <summary>The hotkey texts the user reads: action names, result chips, why a gesture cannot be used.</summary>
public static class HotkeyText
{
    public static string ActionName(HotkeyAction action) => Loc.Instance["Action." + action];

    /// <summary>The short state of an action's hotkey ("OK", "In use" …).</summary>
    public static string State(HotkeyResult result) => Loc.Instance["Hotkey.State." + result];

    /// <summary>Why the gesture was refused, or the warning of an allowed one; "" for a gesture without either.</summary>
    public static string Explain(HotkeyValidation validation) => validation switch
    {
        { Rule: not HotkeyRule.None } => Loc.Instance["Hotkey.Rule." + validation.Rule],
        { Result: HotkeyResult.Ok, Warning: true } => Loc.Instance["Hotkey.WinWarning"],
        { Result: HotkeyResult.Ok } => "",
        _ => Loc.Instance["Hotkey.Why." + validation.Result],
    };

    /// <summary>One notice naming each hotkey that does not work and why; null when every enabled one does.</summary>
    public static string? Problems(IReadOnlyDictionary<HotkeyAction, HotkeyResult> results, HotkeySettings hotkeys)
    {
        var lines = results.Where(r => r.Value is not (HotkeyResult.Ok or HotkeyResult.Disabled)).OrderBy(r => r.Key)
            .Select(r => Loc.F("Hotkey.ProblemItem", hotkeys[r.Key], ActionName(r.Key), Why(r.Value, hotkeys[r.Key])))
            .ToList();
        return lines.Count == 0 ? null : Loc.F("Hotkey.Problems", string.Join("\n", lines));
    }

    static string Why(HotkeyResult result, string gesture)
    {
        // An invalid or reserved gesture says which rule it broke; Windows refusing a valid one says only that.
        var validation = HotkeyManager.Validate(gesture);
        return validation.Result == result && validation.Rule != HotkeyRule.None
            ? Loc.Instance["Hotkey.Rule." + validation.Rule]
            : Loc.Instance["Hotkey.Why." + result];
    }
}
