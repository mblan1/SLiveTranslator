using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SLiveTranslator.Models;
using SLiveTranslator.Updates;
using SLiveTranslator.UI.Pages;

namespace SLiveTranslator.UI;

/// <summary>What the update window asks of the app.</summary>
public interface IUpdateActions
{
    /// <summary>Downloads the exe next to the running one; throws <see cref="UpdateException"/> when it fails.</summary>
    Task DownloadAsync(UpdateInfo update, IProgress<DownloadProgress> progress, CancellationToken ct);

    /// <summary>Puts the download in place and starts it; this copy then exits. Throws <see cref="UpdateException"/> when it fails (nothing changed).</summary>
    void InstallAndRestart();

    void OpenPage(Uri page);

    void Quit();
}

public enum UpdateStep { Ready, Downloading, Installing, Failed }

/// <summary>
/// The "Update required" window: the new version, its size and notes, then the download's progress (bytes, percent,
/// speed, time left), and what went wrong with a way to try again. The app cannot be used until it is updated, so the
/// only other way out is Quit.
/// </summary>
public sealed class UpdateViewModel : ObservableObject
{
    readonly IUpdateActions _actions;
    CancellationTokenSource? _download;

    public UpdateViewModel(UpdateInfo update, string currentVersion, IUpdateActions actions)
    {
        Update = update;
        CurrentVersion = currentVersion;
        _actions = actions;
        Notes = ReleaseNotesText.Plain(update.Notes);
        UpdateCommand = new AsyncRelayCommand(UpdateAsync, () => CanUpdate);
        CancelCommand = new RelayCommand(Cancel, () => Step == UpdateStep.Downloading);
        QuitCommand = new RelayCommand(() =>
        {
            Cancel();
            _actions.Quit();
        }, () => Step != UpdateStep.Installing);
        OpenPageCommand = new RelayCommand(() => _actions.OpenPage(update.Page));
        _progress = new DownloadProgress(0, update.Size, 0);
    }

    public UpdateInfo Update { get; }
    public string CurrentVersion { get; }
    public string NewVersion => Update.VersionText;
    public string Notes { get; }
    public bool HasNotes => Notes.Length > 0;

    public string Message => Loc.F("Update.Message", NewVersion, CurrentVersion);
    public string SizeText => Update.Size > 0 ? ByteSize.Text(Update.Size) : Loc.Instance["Update.SizeUnknown"];
    public string ReleasedText => Update.Published is { } at ? at.ToLocalTime().ToString("D", Loc.Instance.Culture) : "";
    public bool HasReleased => Update.Published is not null;

    public AsyncRelayCommand UpdateCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand QuitCommand { get; }
    public RelayCommand OpenPageCommand { get; }

    UpdateStep _step;
    string _problem = "";
    bool _cancelled;
    DownloadProgress _progress;

    public UpdateStep Step
    {
        get => _step;
        private set
        {
            if (!SetProperty(ref _step, value)) return;
            UpdateCommand.NotifyCanExecuteChanged();
            CancelCommand.NotifyCanExecuteChanged();
            QuitCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(string.Empty); // nearly everything shown depends on the step
        }
    }

    /// <summary>What went wrong, written for the user; empty when nothing did.</summary>
    public string Problem
    {
        get => _problem;
        private set
        {
            if (SetProperty(ref _problem, value)) OnPropertyChanged(nameof(ShowProblem));
        }
    }

    /// <summary>The download was cancelled: it must be done before the app can be used, which the window says.</summary>
    public bool Cancelled
    {
        get => _cancelled;
        private set
        {
            if (!SetProperty(ref _cancelled, value)) return;
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(ShowStatus));
        }
    }

    public bool CanUpdate => Step is UpdateStep.Ready or UpdateStep.Failed;
    public bool Busy => Step is UpdateStep.Downloading or UpdateStep.Installing;
    public bool ShowProgress => Busy;
    public bool ShowProblem => Step == UpdateStep.Failed && Problem.Length > 0;
    public string UpdateText => Loc.Instance[Step == UpdateStep.Failed ? "Update.TryAgain" : "Update.Now"];

    /// <summary>A line under the details: installing, or the reminder after a cancelled download.</summary>
    public string StatusText => Step == UpdateStep.Installing ? Loc.Instance["Update.Installing"]
        : Step == UpdateStep.Ready && Cancelled ? Loc.Instance["Update.Cancelled"] : "";

    public bool ShowStatus => StatusText.Length > 0;

    /// <summary>0 to 100.</summary>
    public double Percent => Step == UpdateStep.Installing ? 100 : _progress.Fraction * 100;

    /// <summary>No size is known: the bar runs without a position.</summary>
    public bool Indeterminate => Step == UpdateStep.Downloading && _progress.Total <= 0;

    /// <summary>"87.0 MB of 191.2 MB · 45 %", or "Connecting…" before the first byte.</summary>
    public string ProgressText
    {
        get
        {
            if (Step == UpdateStep.Installing) return ByteSize.Text(_progress.Received);
            if (_progress.Received == 0) return Loc.Instance["Update.Connecting"];
            return _progress.Total > 0
                ? Loc.F("Update.Progress", ByteSize.Text(_progress.Received), ByteSize.Text(_progress.Total), (int)Math.Floor(Percent))
                : ByteSize.Text(_progress.Received);
        }
    }

    /// <summary>"3.4 MB/s · about 30 s left".</summary>
    public string SpeedText
    {
        get
        {
            if (Step != UpdateStep.Downloading || _progress.BytesPerSecond < 1) return "";
            string speed = ByteSize.Text((long)_progress.BytesPerSecond);
            return _progress.Remaining is { } left ? Loc.F("Update.Speed", speed, Duration(left)) : Loc.F("Update.SpeedOnly", speed);
        }
    }

    /// <summary>"45 s", "3 min".</summary>
    internal static string Duration(TimeSpan t) =>
        t.TotalSeconds < 60 ? Loc.F("Update.Seconds", Math.Max(1, (int)Math.Ceiling(t.TotalSeconds))) : Loc.F("Update.Minutes", (int)Math.Ceiling(t.TotalMinutes));

    internal void Report(DownloadProgress progress)
    {
        if (Step != UpdateStep.Downloading) return; // a late report after a cancel or a failure
        _progress = progress;
        OnPropertyChanged(nameof(Percent));
        OnPropertyChanged(nameof(Indeterminate));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(SpeedText));
    }

    async Task UpdateAsync()
    {
        _download?.Dispose();
        var cts = _download = new CancellationTokenSource();
        Problem = "";
        Cancelled = false;
        _progress = new DownloadProgress(0, Update.Size, 0);
        Step = UpdateStep.Downloading;
        try
        {
            await _actions.DownloadAsync(Update, new Progress<DownloadProgress>(Report), cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            Step = UpdateStep.Installing;
            await Task.Yield(); // "Installing…" is drawn before the files move
            _actions.InstallAndRestart(); // this copy exits when it worked
        }
        catch (OperationCanceledException)
        {
            Cancelled = true;
            Step = UpdateStep.Ready;
        }
        catch (UpdateException ex)
        {
            Log.Warn("update: " + ex.Message);
            Fail(ProblemText(ex.Problem, ex.Detail));
        }
        catch (Exception ex)
        {
            Log.Error("update: " + ex);
            Fail(Loc.F("Update.Problem.Network", ex.Message));
        }
    }

    void Fail(string problem)
    {
        Problem = problem;
        Step = UpdateStep.Failed;
    }

    /// <summary>Stops a download in progress (the window closes, or Cancel).</summary>
    public void Cancel()
    {
        if (Step == UpdateStep.Downloading) _download?.Cancel();
    }

    /// <param name="detail">The raw reason; its closing full stop goes, as it is shown in brackets.</param>
    public static string ProblemText(UpdateProblem problem, string detail)
    {
        detail = detail.Trim().TrimEnd('.');
        return problem switch
        {
            UpdateProblem.Incomplete => Loc.F("Update.Problem.Incomplete", detail),
            UpdateProblem.Corrupt => Loc.F("Update.Problem.Corrupt", detail),
            UpdateProblem.WrongVersion => Loc.F("Update.Problem.WrongVersion", detail),
            UpdateProblem.NotWritable => Loc.F("Update.Problem.NotWritable", detail),
            UpdateProblem.Disk => Loc.F("Update.Problem.Disk", detail),
            UpdateProblem.Install => Loc.F("Update.Problem.Install", detail),
            UpdateProblem.Restart => Loc.F("Update.Problem.Restart", detail),
            _ => Loc.F("Update.Problem.Network", detail),
        };
    }

    /// <summary>The self-test's look at the download in progress.</summary>
    internal void ShowSample(DownloadProgress progress)
    {
        Step = UpdateStep.Downloading;
        Report(progress);
    }

    /// <summary>The self-test's look at a failure.</summary>
    internal void ShowSampleProblem(UpdateProblem problem, string detail) => Fail(ProblemText(problem, detail));
}
