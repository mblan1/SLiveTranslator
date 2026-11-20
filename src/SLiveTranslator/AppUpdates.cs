using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using SLiveTranslator.Diagnostics;
using SLiveTranslator.Models;
using SLiveTranslator.Services;
using SLiveTranslator.UI;
using SLiveTranslator.Updates;
using Forms = System.Windows.Forms;

namespace SLiveTranslator;

/// <summary>
/// Updates: the latest release on GitHub is checked at start and every <see cref="UpdateCheckEvery"/>. A newer one must be
/// installed before the app can be used again: subtitles stop, the hotkeys, the tray and the main window bring up the
/// update window instead, and closing that window quits. The new exe is downloaded next to this one, put in its place,
/// and started; it deletes the old exe (and the unpacked files of the old version) once this copy has ended. A check
/// that fails (offline, GitHub down) changes nothing and is tried again after <see cref="UpdateRetryAfter"/>.
/// </summary>
public partial class App : IUpdateActions
{
    static readonly TimeSpan UpdateCheckEvery = TimeSpan.FromHours(6);
    static readonly TimeSpan UpdateRetryAfter = TimeSpan.FromHours(1);

    /// <summary>How long after an update the unpacked files of the old version are deleted: the new version's are loaded by then.</summary>
    static readonly TimeSpan OldUnpackedFilesDelay = TimeSpan.FromSeconds(10);

    /// <summary>How long the new copy keeps trying to delete the old exe (the old copy may take a moment to end).</summary>
    static readonly TimeSpan OldExeDeleteWait = TimeSpan.FromSeconds(10);

    HttpClient? _updateHttp;
    DispatcherTimer? _updateTimer;
    UpdateInfo? _requiredUpdate;
    UpdateWindow? _updateWindow;
    UpdateCheck? _lastUpdateCheck;
    DateTime? _lastUpdateCheckAt;
    bool _checkingForUpdate;
    bool _restartingForUpdate;

    /// <summary>Only the published single-file exe updates itself: a build run from the source is a folder of files.</summary>
    static bool UpdatesSupported
    {
        get
        {
#pragma warning disable IL3000 // an empty Location is exactly what tells a single-file exe apart
            return string.IsNullOrEmpty(typeof(App).Assembly.Location) && Environment.ProcessPath is not null;
#pragma warning restore IL3000
        }
    }

    static string Exe => Environment.ProcessPath!;

    /// <summary>Follows redirects (GitHub serves a release's file from another host), apart from the translation services' pool, which must not.</summary>
    HttpClient UpdateHttp => _updateHttp ??= CreateUpdateHttp();

    static HttpClient CreateUpdateHttp()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
        };
        // The timeout is for the answer's headers; a download that stalls is ended by UpdateDownloader.
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SLiveTranslator/" + AppInfo.Version);
        return http;
    }

    /// <summary>
    /// At start, after the single-instance check: deletes what an update left next to the exe (after an update, the old
    /// exe, waiting for the old copy to let go of it, then the old version's unpacked files), and starts the checks.
    /// </summary>
    void StartUpdates(bool updated)
    {
        if (!UpdatesSupported) return;
        string exe = Exe;
        _ = Task.Run(() => UpdateInstaller.CleanUp(exe, updated ? OldExeDeleteWait : TimeSpan.Zero));
        if (updated) _ = DeleteOldUnpackedFilesAsync();

        _updateTimer = new DispatcherTimer();
        _updateTimer.Tick += (_, _) =>
        {
            _updateTimer.Stop();
            _ = CheckForUpdateAsync(fromUser: false, atStart: false);
        };
        _ = CheckForUpdateAsync(fromUser: false, atStart: true);
    }

    static async Task DeleteOldUnpackedFilesAsync()
    {
        await Task.Delay(OldUnpackedFilesDelay);
        await Task.Run(UpdateInstaller.DeleteOldExtractions);
    }

    /// <param name="fromUser">The About page's button: the window comes to the front, and a check already running is waited for.</param>
    /// <param name="atStart">The check at start: the window opens in front (nothing else is going on yet).</param>
    async Task<UpdateCheck> CheckForUpdateAsync(bool fromUser, bool atStart)
    {
        if (!UpdatesSupported) return UpdateCheck.Disabled;
        if (_requiredUpdate is { } known)
        {
            ShowUpdateWindow(activate: true);
            return new UpdateCheck(UpdateCheckState.Available, known);
        }
        if (_checkingForUpdate) return _lastUpdateCheck ?? UpdateCheck.UpToDate;

        _checkingForUpdate = true;
        StateChanged?.Invoke(this, EventArgs.Empty);
        UpdateCheck check;
        try
        {
            var current = UpdateFeed.ParseVersion(AppInfo.Version) ?? new Version(0, 0, 0, 0);
            var feed = UpdateFeed.FeedFrom(Environment.GetEnvironmentVariable(UpdateFeed.TestFeedVariable));
            if (feed != UpdateFeed.Latest) Log.Write("update: reading the test feed " + feed);
            check = await UpdateFeed.CheckAsync(UpdateHttp, feed, current, CancellationToken.None);
        }
        finally
        {
            _checkingForUpdate = false;
        }

        _lastUpdateCheck = check;
        _lastUpdateCheckAt = DateTime.Now;
        switch (check.State)
        {
            case UpdateCheckState.Available:
                RequireUpdate(check.Update!, activate: fromUser || atStart);
                break;
            case UpdateCheckState.Failed:
                Log.Warn("update: check failed: " + check.Problem);
                ScheduleUpdateCheck(UpdateRetryAfter);
                break;
            default:
                Log.Write($"update: {AppInfo.Version} is the latest version");
                ScheduleUpdateCheck(UpdateCheckEvery);
                break;
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
        return check;
    }

    void ScheduleUpdateCheck(TimeSpan after)
    {
        if (_updateTimer is null) return;
        _updateTimer.Stop();
        _updateTimer.Interval = after;
        _updateTimer.Start();
    }

    /// <summary>From now on the app waits for the update: what runs stops, and the update window is the only thing it shows.</summary>
    /// <param name="activate">False for a check while the user is in a game: the window shows without taking the keyboard, and a notice says why.</param>
    void RequireUpdate(UpdateInfo update, bool activate)
    {
        if (_requiredUpdate is null)
        {
            _requiredUpdate = update;
            _updateTimer?.Stop();
            Log.Write($"update: {update.Tag} ({update.Size:N0} bytes) is out; {AppInfo.Version} cannot be used until it is installed");
            _screenCts?.Cancel();
            _previewTimer?.Stop();
            StopSubtitles();
            _overlay?.ClearLabels();
            _presenter?.HideHistory();
            if (_mainWindow is { } main)
            {
                SkipUnsavedQuestion(main);
                main.Close();
            }
            if (!activate) _notifier.Feedback(NoticeCategory.Warning, Loc.F("Notice.UpdateRequired", update.VersionText), Forms.Screen.PrimaryScreen?.Bounds);
        }
        ShowUpdateWindow(activate);
    }

    /// <summary>
    /// While an update waits, what the user asks for (a hotkey, the tray, the main window) brings up the update window.
    /// </summary>
    /// <returns>Whether an update waits (and the request was answered with the window).</returns>
    bool UpdateBlocks()
    {
        if (_requiredUpdate is null) return false;
        ShowUpdateWindow(activate: true);
        return true;
    }

    void ShowUpdateWindow(bool activate)
    {
        if (_requiredUpdate is not { } update) return;
        if (_updateWindow is { } open)
        {
            if (!activate) return;
            if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
            open.Activate();
            return;
        }
        var window = _updateWindow = new UpdateWindow(new UpdateViewModel(update, AppInfo.Version, this), UpdateWindow.DarkFor(_settings.General.Theme))
        {
            ShowActivated = activate,
        };
        window.Closed += (_, _) =>
        {
            _updateWindow = null;
            if (!_restartingForUpdate) ExitApp(); // without the update the app cannot be used
        };
        window.Show();
    }

    // ---- the update window's view of the app (IUpdateActions) ----

    Task IUpdateActions.DownloadAsync(UpdateInfo update, IProgress<DownloadProgress> progress, CancellationToken ct) =>
        UpdateDownloader.DownloadAsync(UpdateHttp, update, UpdateInstaller.DownloadPath(Exe), progress, ct);

    void IUpdateActions.InstallAndRestart()
    {
        string exe = Exe;
        string old = UpdateInstaller.Swap(exe, UpdateInstaller.DownloadPath(exe));
        ReleaseInstance();
        try
        {
            var start = new ProcessStartInfo(exe) { UseShellExecute = false };
            start.ArgumentList.Add(UpdateInstaller.UpdatedArgument);
            start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            using var _ = Process.Start(start);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            Log.Error("update: the new version did not start: " + ex.Message);
            UpdateInstaller.RollBack(exe, old);
            ReacquireInstance();
            throw new UpdateException(UpdateProblem.Restart, ex.Message);
        }
        Log.Write($"update: started {_requiredUpdate?.Tag}; this copy ends");
        _restartingForUpdate = true;
        ExitApp();
    }

    void IUpdateActions.OpenPage(Uri page)
    {
        try
        {
            Process.Start(new ProcessStartInfo(page.AbsoluteUri) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Log.Warn("update: release page not opened: " + ex.Message);
        }
    }

    void IUpdateActions.Quit() => ExitApp();

    // ---- the About page ----

    UpdateCheck? ISettingsHost.LastUpdateCheck => UpdatesSupported ? _lastUpdateCheck : UpdateCheck.Disabled;
    DateTime? ISettingsHost.LastUpdateCheckTime => _lastUpdateCheckAt;
    bool ISettingsHost.CheckingForUpdate => _checkingForUpdate;
    Task<UpdateCheck> ISettingsHost.CheckForUpdatesAsync() => CheckForUpdateAsync(fromUser: true, atStart: false);
}
