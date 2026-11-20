using CommunityToolkit.Mvvm.Input;
using SLiveTranslator.Models;

namespace SLiveTranslator.UI.Pages;

/// <summary>
/// The Display &amp; sharing page: whether the overlay is private (hidden from screen capture) or shared on screen,
/// what viewers then see, and whether the main window is left out of screen sharing.
/// </summary>
public sealed class DisplayViewModel : SettingsPageViewModel
{
    public DisplayViewModel(SettingsViewModel root) : base(root)
    {
        OpenRegionsCommand = new RelayCommand(() => root.Navigate(SettingsViewModel.RegionsPage));
    }

    OverlaySettings O => Working.Overlay;

    public RelayCommand OpenRegionsCommand { get; }

    public void Relocalize() => Reload();

    public bool IsPrivate
    {
        get => O.DisplayMode == DisplayMode.Private;
        set => SetMode(value, DisplayMode.Private);
    }

    public bool IsShared
    {
        get => O.DisplayMode == DisplayMode.Shared;
        set => SetMode(value, DisplayMode.Shared);
    }

    void SetMode(bool chosen, DisplayMode mode)
    {
        if (!chosen) return;
        Set(O.DisplayMode, mode, v => O.DisplayMode = v, nameof(O.DisplayMode));
        KeepOffRead();
        Reload();
    }

    public bool ShareSubtitles
    {
        get => O.Share.Subtitles;
        set
        {
            Set(O.Share.Subtitles, value, v => O.Share.Subtitles = v);
            KeepOffRead();
            OnPropertyChanged(nameof(PlacementText));
            OnPropertyChanged(nameof(PlacementStatus));
            OnPropertyChanged(nameof(PlacementKind));
            OnPropertyChanged(nameof(MovedNote));
        }
    }

    string _movedNote = "";

    /// <summary>What switching to Shared on screen moved off the read region; "" when nothing moved.</summary>
    public string MovedNote => _movedNote;

    /// <summary>
    /// Shared on screen with the subtitles shared: a translation placed on the read region moves off it now (the app's
    /// own OCR would read it), and the page says so. Saved with the rest.
    /// </summary>
    void KeepOffRead()
    {
        _movedNote = "";
        if (Working.Subtitles.Region is not { } read) return;
        var fix = Overlay.RegionRules.Enforce(O, read, System.Windows.Forms.Screen.FromRectangle(read.ToRectangle()).Bounds);
        if (fix == Overlay.PlacementFix.None) return;
        _movedNote = Overlay.RegionRules.NoticeFor(fix);
        Root.Edited();
        Root.Regions.Reload();
    }

    public bool ShareHistory
    {
        get => O.Share.History;
        set => Set(O.Share.History, value, v => O.Share.History = v);
    }

    public bool ShareLabels
    {
        get => O.Share.Labels;
        set => Set(O.Share.Labels, value, v => O.Share.Labels = v);
    }

    public bool ShareNotices
    {
        get => O.Share.Notices;
        set => Set(O.Share.Notices, value, v => O.Share.Notices = v);
    }

    public bool HideMainWindow
    {
        get => Working.General.HideMainWindowFromCapture;
        set => Set(Working.General.HideMainWindowFromCapture, value, v => Working.General.HideMainWindowFromCapture = v);
    }

    /// <summary>"Display region: above the read region".</summary>
    public string PlacementText => Loc.F("Display.Placement", Loc.Instance["Position." + O.Position]);

    public string PlacementStatus => RegionsViewModel.OverlapOf(Working).Text;

    public MessageKind PlacementKind => RegionsViewModel.OverlapOf(Working).Kind;

    /// <summary>The hotkey that switches the mode, or a note that it is switched off.</summary>
    public string ModeHotkey => Working.Hotkeys.Disabled.Contains(HotkeyAction.SwitchDisplayMode)
        ? Loc.Instance["Display.HotkeyOff"]
        : Working.Hotkeys[HotkeyAction.SwitchDisplayMode];
}
