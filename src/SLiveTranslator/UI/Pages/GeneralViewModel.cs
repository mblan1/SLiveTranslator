using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using SLiveTranslator.Diagnostics;
using SLiveTranslator.Models;
using SLiveTranslator.Updates;

namespace SLiveTranslator.UI.Pages;

/// <summary>
/// The General & About page: the app's language and theme (applied on Save), Start with Windows (applied at once: the
/// registry is the truth, and a value left pointing at the exe's old place is repaired when the page opens), the setup
/// wizard again, and what the app runs on.
/// </summary>
public sealed class GeneralViewModel : SettingsPageViewModel
{
    readonly ISettingsHost _host;

    public GeneralViewModel(SettingsViewModel root, ISettingsHost host) : base(root)
    {
        _host = host;
        Languages = LanguageChoices();
        Themes = Choices<AppTheme>("Theme.");
        RunSetupCommand = new RelayCommand(() => root.Welcome.Open());
        CheckUpdatesCommand = new AsyncRelayCommand(CheckUpdatesAsync);
        RepairStartup();
    }

    /// <summary>Follow Windows, then each language in its own words, so it can be found whatever the app shows now.</summary>
    internal static IReadOnlyList<Choice<string>> LanguageChoices() =>
    [
        new("", Loc.Instance["General.LanguageSystem"]),
        new(Loc.English, "English"),
        new(Loc.Vietnamese, "Tiếng Việt"),
    ];

    public IReadOnlyList<Choice<string>> Languages { get; private set; }

    public Choice<string> SelectedLanguage
    {
        get => Languages.FirstOrDefault(l => l.Value.Equals(Working.Ui.Language.Trim(), StringComparison.OrdinalIgnoreCase)) ?? Languages[0];
        set
        {
            if (value is not null) Set(Working.Ui.Language, value.Value, v => Working.Ui.Language = v);
        }
    }

    public IReadOnlyList<Choice<AppTheme>> Themes { get; private set; }

    public Choice<AppTheme> SelectedTheme
    {
        get => Themes.FirstOrDefault(t => t.Value == Working.General.Theme) ?? Themes[0];
        set
        {
            if (value is not null) Set(Working.General.Theme, value.Value, v => Working.General.Theme = v);
        }
    }

    /// <summary>The Run value as it is in the registry now.</summary>
    public bool StartWithWindows
    {
        get => _host.Startup.IsEnabled;
        set
        {
            if (value != _host.Startup.IsEnabled)
            {
                try
                {
                    _host.SetStartWithWindows(value);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException or InvalidOperationException)
                {
                    Root.Prompts.Show(Loc.Instance["General.Startup"], Loc.F("General.StartupFailed", ex.Message), MessageKind.Error);
                }
            }
            OnPropertyChanged();
        }
    }

    public RelayCommand RunSetupCommand { get; }

    public AsyncRelayCommand CheckUpdatesCommand { get; }

    /// <summary>"Up to date · checked at 14:05", "Checking for updates…", or why the check failed.</summary>
    public string UpdateStatus => UpdateStatusText(_host.LastUpdateCheck, _host.LastUpdateCheckTime, _host.CheckingForUpdate);

    /// <summary>A build run from the source does not update itself: there is nothing to check.</summary>
    public bool CanCheckUpdates => _host.LastUpdateCheck?.State != UpdateCheckState.Disabled && !_host.CheckingForUpdate;

    internal static string UpdateStatusText(UpdateCheck? check, DateTime? at, bool checking)
    {
        if (checking) return Loc.Instance["General.UpdateChecking"];
        string time = at?.ToString("t", Loc.Instance.Culture) ?? "";
        return check?.State switch
        {
            null => Loc.Instance["General.UpdateNotChecked"],
            UpdateCheckState.Disabled => Loc.Instance["General.UpdateDisabled"],
            UpdateCheckState.UpToDate => Loc.F("General.UpdateUpToDate", AppInfo.Version, time),
            UpdateCheckState.Available => Loc.F("General.UpdateAvailable", check.Update!.VersionText),
            _ => Loc.F("General.UpdateFailed", time, check.Problem),
        };
    }

    async Task CheckUpdatesAsync()
    {
        var check = _host.CheckForUpdatesAsync();
        UpdateStateChanged(); // "Checking for updates…"
        await check;
        UpdateStateChanged();
    }

    /// <summary>A check started or ended (here or by the app's timer).</summary>
    public void UpdateStateChanged()
    {
        OnPropertyChanged(nameof(UpdateStatus));
        OnPropertyChanged(nameof(CanCheckUpdates));
    }

    public string Version => AppInfo.Version;
    public string Runtime => AppInfo.Runtime;
    public string OsDescription => AppInfo.OsDescription;
    public string OcrLanguages => _host.OcrLanguages.Count > 0 ? string.Join(", ", _host.OcrLanguages) : Loc.Instance["General.NoOcr"];
    public string DataFolder => _host.DataPaths.IsPortable ? Loc.F("General.DataPortable", _host.DataPaths.Root) : _host.DataPaths.Root;

    /// <summary>The page is on screen: the registry and the data folder may have changed meanwhile.</summary>
    public void PageShown()
    {
        RepairStartup();
        OnPropertyChanged(nameof(StartWithWindows));
        OnPropertyChanged(nameof(DataFolder));
        OnPropertyChanged(nameof(OcrLanguages));
        UpdateStateChanged();
    }

    /// <summary>The app's language changed: the lists are written again, the selection kept.</summary>
    public void Relocalize()
    {
        Languages = LanguageChoices();
        Themes = Choices<AppTheme>("Theme.");
        OnPropertyChanged(nameof(Languages));
        OnPropertyChanged(nameof(SelectedLanguage));
        OnPropertyChanged(nameof(Themes));
        OnPropertyChanged(nameof(SelectedTheme));
        OnPropertyChanged(nameof(OcrLanguages));
        OnPropertyChanged(nameof(DataFolder));
        OnPropertyChanged(nameof(UpdateStatus));
    }

    void RepairStartup()
    {
        try
        {
            if (_host.Startup.RepairIfStale()) Log.Write("startup: the Run value pointed at another exe and was repaired");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            Log.Warn("startup: the Run value could not be repaired: " + ex.Message);
        }
    }
}
