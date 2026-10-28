using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using SLiveTranslator.Capture;
using SLiveTranslator.Models;
using SLiveTranslator.UI.Pages;
using ContentDialog = Wpf.Ui.Controls.ContentDialog;
using ContentDialogResult = Wpf.Ui.Controls.ContentDialogResult;
using ControlAppearance = Wpf.Ui.Controls.ControlAppearance;
using FluentWindow = Wpf.Ui.Controls.FluentWindow;
using INavigationViewPageProvider = Wpf.Ui.Abstractions.INavigationViewPageProvider;
using NumberBox = Wpf.Ui.Controls.NumberBox;
using Snackbar = Wpf.Ui.Controls.Snackbar;
using SymbolIcon = Wpf.Ui.Controls.SymbolIcon;
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;

namespace SLiveTranslator.UI;

/// <summary>
/// The settings and status window (WPF-UI): a page per part of the app, one Save/Discard bar for all of them.
/// Non-modal, not topmost, hidden from screen capture so the app never reads its own labels. Open it with
/// <see cref="Open"/>, which keeps WPF-UI's theme off the overlays (see <see cref="FluentTheme"/>).
/// </summary>
public partial class MainWindow : FluentWindow, IUserPrompts
{
    readonly SettingsViewModel _vm;
    AppTheme _theme;
    bool _followsSystem;
    bool _closeConfirmed;

    MainWindow(SettingsViewModel vm, AppTheme theme)
    {
        InitializeComponent();
        _vm = vm;
        _theme = theme;
        DataContext = vm;
        vm.Prompts = this;
        RootNavigation.SetPageProviderService(new PageProvider(vm));
        SourceInitialized += (_, _) => ExcludedFromCapture = Native.SetWindowDisplayAffinity(new WindowInteropHelper(this).Handle, Native.WDA_EXCLUDEFROMCAPTURE);
        Loaded += OnLoaded;
        AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler(OnTextChanged));
        AddHandler(LostFocusEvent, new RoutedEventHandler(OnLostFocus), handledEventsToo: true);
        Closing += OnClosing;
        Closed += OnClosed;
    }

    /// <summary>Screen capture, and so the app's own OCR, does not see the window.</summary>
    public bool ExcludedFromCapture { get; private set; }

    /// <summary>Closes without asking about unsaved edits (the app exits).</summary>
    public bool SkipUnsavedQuestion { get; set; }

    /// <summary>
    /// Makes and shows the window: WPF-UI's dictionaries are merged first (on the first open of the run), and the
    /// window becomes the app's MainWindow before any theme call, so the theme lands on it and not on an overlay.
    /// </summary>
    public static MainWindow Open(SettingsViewModel vm, AppTheme theme)
    {
        FluentTheme.EnsureLoaded(theme);
        var window = new MainWindow(vm, theme);
        Application.Current.MainWindow = window;
        window.Show();
        return window;
    }

    void OnLoaded(object sender, RoutedEventArgs e)
    {
        _followsSystem = FluentTheme.Apply(this, _theme);
        RootNavigation.Navigate(typeof(HomePage));
    }

    /// <summary>The theme setting was saved while the window is open.</summary>
    public void ApplyTheme(AppTheme theme)
    {
        if (theme == _theme || !IsLoaded) return;
        _theme = theme;
        if (_followsSystem) FluentTheme.Unfollow(this);
        _followsSystem = FluentTheme.Apply(this, theme);
    }

    async void OnClosing(object? sender, CancelEventArgs e)
    {
        CommitTyping(this);
        if (_closeConfirmed || SkipUnsavedQuestion || !_vm.HasChanges)
        {
            // Here and not in Closed: the watcher finds the window by its handle, which is gone by then.
            if (_followsSystem) FluentTheme.Unfollow(this);
            _followsSystem = false;
            return;
        }
        e.Cancel = true;
        var dialog = new ContentDialog(DialogHost)
        {
            Title = Loc.Instance["Main.UnsavedTitle"],
            Content = Loc.Instance["Main.UnsavedText"],
            PrimaryButtonText = Loc.Instance["Main.Save"],
            SecondaryButtonText = Loc.Instance["Main.DontSave"],
            CloseButtonText = Loc.Instance["Dialog.Cancel"],
        };
        var answer = await dialog.ShowAsync(CancellationToken.None);
        if (answer == ContentDialogResult.None || (answer == ContentDialogResult.Primary && !_vm.Save())) return;
        if (answer == ContentDialogResult.Secondary) _vm.Discard();
        _closeConfirmed = true;
        Close();
    }

    /// <summary>
    /// A number box takes what was typed only on Enter or when it loses focus, and closing by Alt+F4 or the title bar
    /// moves no focus. Taking the focus off the focused element makes a box take its number first, so the question
    /// about unsaved edits knows of it.
    /// </summary>
    internal static void CommitTyping(Window window)
    {
        var focused = Keyboard.FocusedElement as DependencyObject;
        if (focused is null || !ReferenceEquals(GetWindow(focused), window)) focused = FocusManager.GetFocusedElement(window) as DependencyObject;
        if (focused is null) return;
        FocusManager.SetFocusedElement(FocusManager.GetFocusScope(focused), null); // the box loses focus and takes the number
        if (ReferenceEquals(Keyboard.FocusedElement, focused)) Keyboard.ClearFocus();
    }

    /// <summary>A number typed but not taken yet already shows the Save bar; leaving the box or Enter takes it (see <see cref="CommitTyping"/>).</summary>
    void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (e.OriginalSource is NumberBox box) _vm.Typing = box.IsFocused && !ShowsValue(box);
    }

    void OnLostFocus(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is NumberBox) _vm.Typing = false; // after the box handed its number to the page
    }

    static bool ShowsValue(NumberBox box) =>
        double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double typed)
            ? box.Value is { } value && Math.Abs(value - typed) < 1e-9
            : box.Text.Trim().Length == 0 && box.Value is null;

    void OnClosed(object? sender, EventArgs e)
    {
        _vm.Dispose();
        // A closed window must not stay the app's MainWindow: it would be kept alive, and theme calls would go to it.
        if (ReferenceEquals(Application.Current.MainWindow, this)) Application.Current.MainWindow = null;
    }

    // ---- IUserPrompts ----

    public void Show(string title, string text, MessageKind kind)
    {
        var (appearance, icon) = kind switch
        {
            MessageKind.Success => (ControlAppearance.Success, SymbolRegular.Checkmark24),
            MessageKind.Warning => (ControlAppearance.Caution, SymbolRegular.Warning24),
            MessageKind.Error => (ControlAppearance.Danger, SymbolRegular.ErrorCircle24),
            _ => (ControlAppearance.Secondary, SymbolRegular.Info24),
        };
        new Snackbar(Snackbars)
        {
            Title = title,
            Content = text,
            Appearance = appearance,
            Icon = new SymbolIcon { Symbol = icon },
            Timeout = TimeSpan.FromSeconds(kind == MessageKind.Error ? 8 : 4),
        }.Show();
    }

    public async Task<bool> ConfirmAsync(string title, string text, string yes, string no)
    {
        var dialog = new ContentDialog(DialogHost) { Title = title, Content = text, PrimaryButtonText = yes, CloseButtonText = no };
        return await dialog.ShowAsync(CancellationToken.None) == ContentDialogResult.Primary;
    }

    public async Task<DialogAnswer> AskAsync(string title, string text, string primary, string secondary, string cancel)
    {
        var dialog = new ContentDialog(DialogHost)
        {
            Title = title, Content = text, PrimaryButtonText = primary, SecondaryButtonText = secondary, CloseButtonText = cancel,
        };
        return await dialog.ShowAsync(CancellationToken.None) switch
        {
            ContentDialogResult.Primary => DialogAnswer.Primary,
            ContentDialogResult.Secondary => DialogAnswer.Secondary,
            _ => DialogAnswer.Cancel,
        };
    }

    public string? PickOpenFile(string filter)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = filter };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    public string? PickSaveFile(string fileName, string filter)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = filter, FileName = fileName };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    public string? PickFolder(string? start)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = start ?? "" };
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    public bool CopyText(string text)
    {
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (COMException ex)
        {
            Log.Warn("clipboard: " + ex.Message); // another program holds it
            return false;
        }
    }

    /// <summary>
    /// Pages made once per window, each with its part of the view model: a page is a navigation boundary, so it does
    /// not inherit the window's DataContext, and a page made again would run its background key check again.
    /// </summary>
    sealed class PageProvider(SettingsViewModel vm) : INavigationViewPageProvider
    {
        readonly Dictionary<Type, FrameworkElement> _pages = new();

        public object? GetPage(Type pageType)
        {
            if (_pages.TryGetValue(pageType, out var page)) return page;
            page = (FrameworkElement)Activator.CreateInstance(pageType)!;
            page.DataContext = pageType == typeof(HomePage) ? vm.Home
                : pageType == typeof(HistoryPage) ? vm.History
                : pageType == typeof(TranslationPage) ? vm.Translation
                : pageType == typeof(GameProfilePage) ? vm.Profiles
                : pageType == typeof(SubtitlesPage) ? vm.Subtitles
                : pageType == typeof(OverlayPage) ? vm.Overlay
                : pageType == typeof(HotkeysPage) ? vm.Hotkeys
                : pageType == typeof(NotificationsPage) ? vm.Notifications
                : pageType == typeof(StoragePage) ? vm.Storage
                : pageType == typeof(GeneralPage) ? vm.General
                : vm;
            _pages[pageType] = page;
            return page;
        }
    }
}
