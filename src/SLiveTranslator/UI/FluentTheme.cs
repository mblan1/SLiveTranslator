using System.Linq;
using System.Windows;
using Microsoft.Win32;
using SLiveTranslator.Models;
using ApplicationTheme = Wpf.Ui.Appearance.ApplicationTheme;
using ApplicationThemeManager = Wpf.Ui.Appearance.ApplicationThemeManager;
using ControlsDictionary = Wpf.Ui.Markup.ControlsDictionary;
using SystemThemeWatcher = Wpf.Ui.Appearance.SystemThemeWatcher;
using ThemesDictionary = Wpf.Ui.Markup.ThemesDictionary;
using WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType;

namespace SLiveTranslator.UI;

/// <summary>
/// WPF-UI's theme, kept away from the overlays. Its dictionaries are merged into the app's resources only when the
/// main window first opens, so a run that never opens it never loads them. WPF-UI paints the background and backdrop
/// of the app's MainWindow, which by default is the first window made (an overlay): every theme call here first
/// checks that MainWindow is the main window. ThemeMode is never set.
/// </summary>
internal static class FluentTheme
{
    const string PersonalizeKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>Merges the dictionaries once, in the theme the setting asks for. Must come before anything else of WPF-UI.</summary>
    public static void EnsureLoaded(AppTheme theme)
    {
        // WPF-UI decides once per thread whether it runs inside an Application, by finding its dictionaries in the
        // app's resources: they must be there before any of its code runs.
        var merged = Application.Current.Resources.MergedDictionaries;
        if (merged.OfType<ThemesDictionary>().Any()) return;
        merged.Add(new ThemesDictionary { Theme = Resolve(theme) });
        merged.Add(new ControlsDictionary());
    }

    /// <summary>Applies the theme to <paramref name="window"/>, which must be the app's MainWindow; for "System", follows Windows from now on.</summary>
    /// <returns>Whether the window now follows Windows' theme (then <see cref="Unfollow"/> on close).</returns>
    public static bool Apply(Window window, AppTheme theme)
    {
        if (!ReferenceEquals(Application.Current.MainWindow, window))
        {
            Log.Warn("theme: the main window is not the app's MainWindow; the theme is not applied");
            return false;
        }
        if (theme == AppTheme.System)
        {
            ApplicationThemeManager.ApplySystemTheme(updateAccent: true);
            SystemThemeWatcher.Watch(window, WindowBackdropType.Mica, updateAccents: true);
            return true;
        }
        ApplicationThemeManager.Apply(Resolve(theme), WindowBackdropType.Mica, updateAccent: true);
        return false;
    }

    public static void Unfollow(Window window)
    {
        try
        {
            SystemThemeWatcher.UnWatch(window);
        }
        catch (System.InvalidOperationException ex)
        {
            Log.Warn("theme: " + ex.Message);
        }
    }

    static ApplicationTheme Resolve(AppTheme theme) => theme switch
    {
        AppTheme.Light => ApplicationTheme.Light,
        AppTheme.Dark => ApplicationTheme.Dark,
        _ => SystemParameters.HighContrast ? ApplicationTheme.HighContrast
            : Registry.GetValue(PersonalizeKey, "AppsUseLightTheme", 1) is 0 ? ApplicationTheme.Dark : ApplicationTheme.Light,
    };
}
