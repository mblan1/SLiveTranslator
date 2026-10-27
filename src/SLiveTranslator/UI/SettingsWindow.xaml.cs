using System;
using System.Linq;
using System.Windows;
using SLiveTranslator.Hotkeys;
using SLiveTranslator.Models;
using SLiveTranslator.Ocr;
using DRect = System.Drawing.Rectangle;

namespace SLiveTranslator.UI;

public partial class SettingsWindow : Window
{
    readonly AppSettings _settings;
    readonly Func<DRect?> _selectRegion;

    sealed record LanguageItem(string Tag, string Label)
    {
        public override string ToString() => Label;
    }

    public SettingsWindow(AppSettings settings, Func<DRect?> selectRegion)
    {
        InitializeComponent();
        _settings = settings;
        _selectRegion = selectRegion;
        Populate();
    }

    void Populate()
    {
        OcrLang.Items.Add(new LanguageItem("", "Windows default (user profile languages)"));
        foreach (var (tag, name) in WindowsOcrEngine.AvailableLanguages())
            OcrLang.Items.Add(new LanguageItem(tag, $"{name} ({tag})"));
        OcrLang.SelectedItem = OcrLang.Items.OfType<LanguageItem>().FirstOrDefault(i => i.Tag == _settings.OcrLanguage) ?? OcrLang.Items[0];

        foreach (var code in LangCodes.CommonTargets) TargetLang.Items.Add(code);
        TargetLang.Text = _settings.TargetLanguage;

        Provider.SelectedIndex = _settings.Provider == "claude" ? 1 : 0;
        MyMemoryEmail.Text = _settings.MyMemoryEmail;
        ClaudeKey.Password = _settings.ClaudeApiKey;
        ClaudeModel.Text = _settings.ClaudeModel;
        Interval.Text = _settings.SubtitleIntervalMs.ToString();
        FontSizeBox.Text = _settings.FontSize.ToString();
        OverlaySeconds.Text = _settings.ScreenOverlaySeconds.ToString();
        HkScreen.Text = _settings.HotkeyTranslateScreen;
        HkSubtitles.Text = _settings.HotkeyToggleSubtitles;
        HkRegion.Text = _settings.HotkeySelectRegion;
        HkClear.Text = _settings.HotkeyClear;
        HkHistory.Text = _settings.HotkeyHistory;
        ShowRegion();
    }

    void ShowRegion() =>
        RegionText.Text = _settings.SubtitleRegion is { } r ? $"{r.Width} × {r.Height} px at ({r.X}, {r.Y})" : "not set";

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
                _settings.SubtitleRegion = RegionSettings.From(rect);
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
        foreach (var hotkey in new[] { HkScreen.Text, HkSubtitles.Text, HkRegion.Text, HkClear.Text, HkHistory.Text })
        {
            if (!HotkeyManager.TryParse(hotkey, out _, out _))
            {
                MessageBox.Show(this, $"\"{hotkey}\" is not a valid hotkey. Use the form Ctrl+Shift+T.", "SLiveTranslator", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        _settings.OcrLanguage = (OcrLang.SelectedItem as LanguageItem)?.Tag ?? "";
        _settings.TargetLanguage = string.IsNullOrWhiteSpace(TargetLang.Text) ? "en" : TargetLang.Text.Trim();
        _settings.Provider = Provider.SelectedIndex == 1 ? "claude" : "free";
        _settings.MyMemoryEmail = MyMemoryEmail.Text.Trim();
        _settings.ClaudeApiKey = ClaudeKey.Password.Trim();
        _settings.ClaudeModel = ClaudeModel.Text.Trim();
        _settings.SubtitleIntervalMs = ParseInt(Interval.Text, 300, 100, 5000);
        _settings.FontSize = ParseInt(FontSizeBox.Text, 22, 10, 80);
        _settings.ScreenOverlaySeconds = ParseInt(OverlaySeconds.Text, 12, 0, 300);
        _settings.HotkeyTranslateScreen = HkScreen.Text.Trim();
        _settings.HotkeyToggleSubtitles = HkSubtitles.Text.Trim();
        _settings.HotkeySelectRegion = HkRegion.Text.Trim();
        _settings.HotkeyClear = HkClear.Text.Trim();
        _settings.HotkeyHistory = HkHistory.Text.Trim();
        _settings.FirstRunDone = true;
        _settings.Save();
        DialogResult = true;
    }

    static int ParseInt(string text, int fallback, int min, int max) =>
        int.TryParse(text.Trim(), out var value) ? Math.Clamp(value, min, max) : fallback;
}
