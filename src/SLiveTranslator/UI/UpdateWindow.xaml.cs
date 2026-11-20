using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using SLiveTranslator.Capture;
using SLiveTranslator.Models;

namespace SLiveTranslator.UI;

/// <summary>The "Update required" window (see <see cref="UpdateViewModel"/>), dark or light like the app's theme.</summary>
public partial class UpdateWindow : Window
{
    readonly bool _dark;

    public UpdateWindow(UpdateViewModel vm, bool dark)
    {
        InitializeComponent();
        DataContext = vm;
        ViewModel = vm;
        _dark = dark;
        if (!dark) UseLightColors();
        SourceInitialized += (_, _) =>
        {
            // The title bar follows the window's colours (Windows 10 2004 and later).
            int on = _dark ? 1 : 0;
            Native.DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
        };
        Loaded += (_, _) => UpdateButton.Focus();
    }

    public UpdateViewModel ViewModel { get; }

    const string PersonalizeKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>Dark for the Dark theme, or for System when Windows' apps are dark (not in high contrast).</summary>
    public static bool DarkFor(AppTheme theme) => theme switch
    {
        AppTheme.Light => false,
        AppTheme.Dark => true,
        _ => !SystemParameters.HighContrast && Registry.GetValue(PersonalizeKey, "AppsUseLightTheme", 1) is 0,
    };

    protected override void OnClosing(CancelEventArgs e)
    {
        ViewModel.Cancel(); // closing during the download stops it (and the app quits)
        base.OnClosing(e);
    }

    void UseLightColors()
    {
        Set("Bg", "#F3F3F3");
        Set("Card", "#FFFFFF");
        Set("CardEdge", "#E2E2E2");
        Set("Text", "#1B1B1B");
        Set("Text2", "#5C5C5C");
        Set("Accent", "#005FB8");
        Set("AccentText", "#FFFFFF");
        Set("Button", "#FDFDFD");
        Set("ButtonEdge", "#D0D0D0");
        Set("Track", "#DADADA");
        Set("ProblemBg", "#FDE7E9");
        Set("ProblemEdge", "#F0B9BE");
        Set("Warn", "#C42B1C");
    }

    void Set(string key, string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        Resources[key] = brush;
    }
}
