using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SLiveTranslator.Models;

namespace SLiveTranslator.UI.Pages;

/// <param name="Size">Written out ("1.2 MB").</param>
public sealed record DataFileRow(string Name, string Size);

/// <summary>
/// The data folder on the Storage page: where it is, what it holds, and moving it. Change copies or moves the data to
/// a folder the user picks; Portable copies it to &lt;exe&gt;\Data. When the new folder holds SLiveTranslator data
/// already, the user picks: use that data (from the next start), replace it with the data in use, or cancel. A copy
/// shows its progress and can be cancelled, which leaves both folders as they were; once done, the app uses the new
/// folder at once.
/// </summary>
public sealed class DataFolderViewModel : ObservableObject
{
    readonly SettingsViewModel _root;
    readonly ISettingsHost _host;
    CancellationTokenSource? _copy;
    IReadOnlyList<DataFileRow> _files = [];
    string _freeText = "";
    string _nextStart = "";
    double _progress;
    bool _busy;

    public DataFolderViewModel(SettingsViewModel root, ISettingsHost host)
    {
        _root = root;
        _host = host;
        ChangeCommand = new AsyncRelayCommand(ChangeAsync, () => !IsBusy && !IsPortable);
        OpenCommand = new RelayCommand(() => host.OpenFolder(Root));
        CancelCommand = new RelayCommand(() => _copy?.Cancel(), () => IsBusy);
        Refresh();
    }

    IUserPrompts Prompts => _root.Prompts;
    DataFolderMover Mover => _host.DataFolders;
    static string Title => Loc.Instance["Storage.Folder"];

    public string Root => _host.DataPaths.Root;

    /// <summary>The data lives next to the exe (portable.flag). Switching asks first and copies the data over.</summary>
    public bool IsPortable
    {
        get => _host.DataPaths.IsPortable;
        set
        {
            if (value != IsPortable && CanTogglePortable) _ = SetPortableAsync(value);
            OnPropertyChanged(); // shows the folder in use until the switch is done
        }
    }

    public bool CanTogglePortable => !IsBusy && Mover.PortableRoot is not null;

    public string PortableHint => Mover.PortableRoot is { } portable ? Loc.F("Storage.PortableHint", portable) : "";

    public IReadOnlyList<DataFileRow> Files
    {
        get => _files;
        private set => SetProperty(ref _files, value);
    }

    public string TotalText => Loc.F("Storage.Total", ByteSize.Text(DataFolderMover.Size(Root)));

    public string FreeText
    {
        get => _freeText;
        private set => SetProperty(ref _freeText, value);
    }

    /// <summary>The folder the app uses from its next start, when the user chose the data already in a folder; "" otherwise.</summary>
    public string NextStart
    {
        get => _nextStart;
        private set => SetProperty(ref _nextStart, value);
    }

    public bool IsBusy
    {
        get => _busy;
        private set
        {
            if (!SetProperty(ref _busy, value)) return;
            ChangeCommand.NotifyCanExecuteChanged();
            CancelCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(CanTogglePortable));
        }
    }

    /// <summary>The share of the copy done, 0 to 1.</summary>
    public double Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    public AsyncRelayCommand ChangeCommand { get; }
    public RelayCommand OpenCommand { get; }
    public RelayCommand CancelCommand { get; }

    /// <summary>The window closes: a copy under way stops, and both folders stay as they were.</summary>
    public void Cancel() => _copy?.Cancel();

    /// <summary>Reads the files and the free space again.</summary>
    public void Refresh()
    {
        string root = Root;
        var files = DataFolderMover.DataFiles(root);
        string profiles = Path.GetFileName(DataPaths.At(root, isPortable: false).ProfilesDir);
        bool InProfiles(string f) => f.StartsWith(profiles + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        long SizeOf(string f) => DataFolderMover.FileSize(Path.Combine(root, f));
        var rows = files.Where(f => !InProfiles(f)).Select(f => new DataFileRow(f, ByteSize.Text(SizeOf(f)))).ToList();
        var profileFiles = files.Where(InProfiles).ToList();
        if (profileFiles.Count > 0)
            rows.Add(new DataFileRow(Loc.F("Storage.ProfilesRow", profiles, profileFiles.Count), ByteSize.Text(profileFiles.Sum(SizeOf))));
        Files = rows;
        FreeText = Mover.FreeSpace(root) is { } free ? Loc.F("Storage.Free", ByteSize.Text(free)) : "";
        OnPropertyChanged(nameof(Root));
        OnPropertyChanged(nameof(IsPortable));
        OnPropertyChanged(nameof(TotalText));
        ChangeCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Change: a folder the user picks.</summary>
    public async Task ChangeAsync()
    {
        if (Prompts.PickFolder(Root) is not { } picked) return;
        await MoveToAsync(DataFolderMover.DataFolderIn(picked), portable: false);
    }

    /// <summary>Portable on: the data goes to &lt;exe&gt;\Data. Off: back to the folder used without it (location.json's or the default).</summary>
    public Task SetPortableAsync(bool on) =>
        on ? MoveToAsync(Mover.PortableRoot ?? throw new InvalidOperationException("The folder of the running program is not known."), portable: true)
            : MoveToAsync(Mover.InstalledRoot, portable: false);

    async Task MoveToAsync(string target, bool portable)
    {
        if (IsBusy) return;
        string source = Root;
        bool togglesPortable = portable != _host.DataPaths.IsPortable;
        var problem = Mover.Check(source, target, portable);
        if (problem != FolderProblem.None)
        {
            Prompts.Show(Title, Loc.F("Storage.Problem." + problem, target), MessageKind.Error);
            return;
        }

        bool hasData = DataFolderMover.HasData(target);
        if (hasData)
        {
            var answer = await Prompts.AskAsync(Title, Loc.F("Storage.HasData", target), Loc.Instance["Storage.UseTheirs"], Loc.Instance["Storage.ReplaceWithMine"],
                Loc.Instance["Dialog.Cancel"]);
            if (answer == DialogAnswer.Cancel) return;
            if (answer == DialogAnswer.Primary)
            {
                await UseTheirsAsync(target, portable);
                return;
            }
        }

        bool move = false;
        if (!togglesPortable)
        {
            var how = await Prompts.AskAsync(Title, Loc.F("Storage.MoveOrCopy", source, target), Loc.Instance["Storage.Move"], Loc.Instance["Storage.Copy"],
                Loc.Instance["Dialog.Cancel"]);
            if (how == DialogAnswer.Cancel) return;
            move = how == DialogAnswer.Primary;
        }
        else if (!hasData && !await Prompts.ConfirmAsync(Title, Loc.F("Storage.CopyAsk", target), Loc.Instance["Storage.Copy"], Loc.Instance["Dialog.Cancel"]))
        {
            return;
        }

        if (await CopyAsync(source, target) is not { } files || !Point(target, portable)) return;
        _host.UseDataFolder(DataPaths.At(target, portable));
        NextStart = "";
        if (move && DataFolderMover.DeleteMoved(source, target, files) is { Count: > 0 } left)
            Prompts.Show(Title, Loc.F("Storage.LeftBehind", source, string.Join(", ", left)), MessageKind.Warning);
        else
            Prompts.Show(Title, Loc.F(move ? "Storage.Moved" : "Storage.Copied", target), MessageKind.Success);
        Refresh();
    }

    /// <returns>The files copied; null when cancelled or failed (the user was told, and both folders are as they were).</returns>
    async Task<IReadOnlyList<string>?> CopyAsync(string source, string target)
    {
        var cts = _copy = new CancellationTokenSource();
        Progress = 0;
        IsBusy = true;
        try
        {
            _host.FlushData(); // the copy takes what the app holds in memory too
            return await Mover.CopyAsync(source, target, new Progress<double>(p => Progress = p), cts.Token);
        }
        catch (OperationCanceledException)
        {
            Prompts.Show(Title, Loc.Instance["Storage.Cancelled"], MessageKind.Info);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Prompts.Show(Title, Loc.F("Storage.CopyFailed", ex.Message), MessageKind.Error);
            return null;
        }
        finally
        {
            _copy = null;
            cts.Dispose();
            IsBusy = false;
        }
    }

    /// <summary>The next start finds the data in <paramref name="target"/>: portable.flag on or off, or location.json.</summary>
    /// <returns>False when it could not be written (the user was told).</returns>
    bool Point(string target, bool portable)
    {
        try
        {
            if (portable) Mover.SetPortable(true);
            else if (_host.DataPaths.IsPortable) Mover.SetPortable(false); // back to the folder location.json names, or the default
            else Mover.WriteLocation(target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Prompts.Show(Title, Loc.F("Storage.PointFailed", ex.Message), MessageKind.Error);
            return false;
        }
    }

    /// <summary>The data already in <paramref name="target"/> is used: the app loads it when it starts again.</summary>
    async Task UseTheirsAsync(string target, bool portable)
    {
        if (!Point(target, portable)) return;
        NextStart = Loc.F("Storage.NextStart", target);
        OnPropertyChanged(nameof(IsPortable));
        if (await Prompts.ConfirmAsync(Title, Loc.F("Storage.RestartAsk", target), Loc.Instance["Storage.RestartNow"], Loc.Instance["Storage.Later"]))
            _host.Restart();
    }
}

/// <summary>A number of bytes for the user ("940 B", "12.5 KB", "3.2 MB"), in the culture of the app's language.</summary>
public static class ByteSize
{
    public static string Text(long bytes)
    {
        var culture = Loc.Instance.Culture;
        return bytes switch
        {
            < 1024 => string.Format(culture, "{0:N0} B", bytes),
            < 1024 * 1024 => string.Format(culture, "{0:N1} KB", bytes / 1024.0),
            < 1024L * 1024 * 1024 => string.Format(culture, "{0:N1} MB", bytes / (1024.0 * 1024)),
            _ => string.Format(culture, "{0:N2} GB", bytes / (1024.0 * 1024 * 1024)),
        };
    }
}
