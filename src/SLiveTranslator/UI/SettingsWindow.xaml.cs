using System;
using System.Linq;
using System.Windows;
using SLiveTranslator.Hotkeys;
using SLiveTranslator.Models;
using SLiveTranslator.Ocr;
using SLiveTranslator.Translation.Providers;
using DRect = System.Drawing.Rectangle;

namespace SLiveTranslator.UI;

public partial class SettingsWindow : Window
{
    readonly AppSettings _settings;
    readonly Func<DRect?> _selectRegion;
    readonly Action _save;
    bool _claudeKeyEdited;
    bool _providerPicked;

    sealed record LanguageItem(string Tag, string Label)
    {
        public override string ToString() => Label;
    }

    public SettingsWindow(AppSettings settings, Func<DRect?> selectRegion, Action save)
    {
        InitializeComponent();
        _settings = settings;
        _selectRegion = selectRegion;
        _save = save;
        Populate();
        ClaudeKey.PasswordChanged += (_, _) => _claudeKeyEdited = true;
        Provider.SelectionChanged += (_, _) => _providerPicked = true;
    }

    void Populate()
    {
        OcrLang.Items.Add(new LanguageItem("", "Windows default (user profile languages)"));
        foreach (var (tag, name) in WindowsOcrEngine.AvailableLanguages())
            OcrLang.Items.Add(new LanguageItem(tag, $"{name} ({tag})"));
        OcrLang.SelectedItem = OcrLang.Items.OfType<LanguageItem>().FirstOrDefault(i => i.Tag == _settings.General.OcrLanguage) ?? OcrLang.Items[0];

        foreach (var code in LangCodes.CommonTargets) TargetLang.Items.Add(code);
        TargetLang.Text = _settings.General.TargetLanguage;

        var translation = _settings.Translation;
        Provider.SelectedIndex = translation.Provider == ProviderIds.Claude ? 1 : 0;
        MyMemoryEmail.Text = translation.Free.MyMemoryEmail;
        ClaudeKey.Password = translation.Claude.ApiKey;
        if (translation.Claude.ApiKeyUnreadable)
            ClaudeKey.ToolTip = "The saved key cannot be decrypted on this Windows account. Enter it again, or leave the box empty to remove it.";
        // This window has no key check or model list (the settings page will): it offers the model a check pre-selects.
        ClaudeModel.Text = translation.Claude.Model.Trim() is { Length: > 0 } model ? model : ClaudeProvider.RecommendedModel;
        Interval.Text = _settings.Subtitles.IntervalMs.ToString();
        FontSizeBox.Text = _settings.Overlay.FontSize.ToString();
        OverlaySeconds.Text = _settings.Overlay.LabelSeconds.ToString();
        HkScreen.Text = _settings.Hotkeys[HotkeyAction.TranslateScreen];
        HkSubtitles.Text = _settings.Hotkeys[HotkeyAction.ToggleSubtitles];
        HkRegion.Text = _settings.Hotkeys[HotkeyAction.SelectRegion];
        HkClear.Text = _settings.Hotkeys[HotkeyAction.Clear];
        HkHistory.Text = _settings.Hotkeys[HotkeyAction.History];
        ShowRegion();
    }

    void ShowRegion() =>
        RegionText.Text = _settings.Subtitles.Region is { } r ? $"{r.Width} × {r.Height} px at ({r.X}, {r.Y})" : "not set";

    void OnSelectRegion(object sender, RoutedEventArgs e)
    {
        // Move out of the way instead of Hide(): hiding a modal window ends ShowDialog, and Save then crashed on DialogResult.
        double left = Left, top = Top;
        Left = -32000;
        Top = -32000;
        try
        {
            if (_selectRegion() is { } rect)
            {
                _settings.Subtitles.Region = RegionSettings.From(rect);
                ShowRegion();
            }
        }
        finally
        {
            Left = left;
            Top = top;
            Activate();
        }
    }

    void OnSave(object sender, RoutedEventArgs e)
    {
        if (TryApply()) DialogResult = true;
    }

    /// <summary>Copies the form into the settings and saves them; false when a value is invalid (the user was told).</summary>
    internal bool TryApply()
    {
        foreach (var hotkey in new[] { HkScreen.Text, HkSubtitles.Text, HkRegion.Text, HkClear.Text, HkHistory.Text })
        {
            var check = HotkeyManager.Validate(hotkey);
            if (check.Result != HotkeyResult.Ok)
            {
                MessageBox.Show(this, Loc.F("Hotkey.CannotUse", hotkey, HotkeyText.Explain(check)), "SLiveTranslator", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }

        _settings.General.OcrLanguage = (OcrLang.SelectedItem as LanguageItem)?.Tag ?? "";
        _settings.General.TargetLanguage = string.IsNullOrWhiteSpace(TargetLang.Text) ? "en" : TargetLang.Text.Trim();
        var translation = _settings.Translation;
        // This window offers only Free and Claude: another provider (set by hand or on the settings page) shows as Free
        // and stays selected unless the user picks one here.
        if (_providerPicked) translation.Provider = Provider.SelectedIndex == 1 ? ProviderIds.Claude : ProviderIds.Free;
        translation.Free.MyMemoryEmail = MyMemoryEmail.Text.Trim();
        // Only an edited key is stored: a key this Windows account cannot decrypt shows as empty and survives an
        // unrelated save, while emptying the box on purpose removes it.
        if (_claudeKeyEdited) translation.Claude.ApiKey = ClaudeKey.Password;
        translation.Claude.Model = ClaudeModel.Text.Trim();
        _settings.Subtitles.IntervalMs = ParseInt(Interval.Text, 300, 100, 5000);
        _settings.Overlay.FontSize = ParseInt(FontSizeBox.Text, 22, 10, 80);
        _settings.Overlay.LabelSeconds = ParseInt(OverlaySeconds.Text, 12, 0, 300);
        _settings.Hotkeys[HotkeyAction.TranslateScreen] = HkScreen.Text.Trim();
        _settings.Hotkeys[HotkeyAction.ToggleSubtitles] = HkSubtitles.Text.Trim();
        _settings.Hotkeys[HotkeyAction.SelectRegion] = HkRegion.Text.Trim();
        _settings.Hotkeys[HotkeyAction.Clear] = HkClear.Text.Trim();
        _settings.Hotkeys[HotkeyAction.History] = HkHistory.Text.Trim();
        _settings.General.FirstRunDone = true;
        _save();
        return true;
    }

    static int ParseInt(string text, int fallback, int min, int max) =>
        int.TryParse(text.Trim(), out var value) ? Math.Clamp(value, min, max) : fallback;
}
