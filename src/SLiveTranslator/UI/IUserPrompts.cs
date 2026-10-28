using System.Threading.Tasks;

namespace SLiveTranslator.UI;

public enum MessageKind { Info, Success, Warning, Error }

/// <summary>Which button of a question with two answers and Cancel was pressed.</summary>
public enum DialogAnswer { Primary, Secondary, Cancel }

/// <summary>What a page asks of the window it is in: a short message, a question, a file or folder, the clipboard.</summary>
public interface IUserPrompts
{
    void Show(string title, string text, MessageKind kind);
    Task<bool> ConfirmAsync(string title, string text, string yes, string no);

    /// <summary>A question with two answers besides Cancel (move or copy; use the folder's data or replace it).</summary>
    Task<DialogAnswer> AskAsync(string title, string text, string primary, string secondary, string cancel);

    /// <returns>The file picked; null when the user cancelled.</returns>
    string? PickOpenFile(string filter);

    /// <returns>The file picked; null when the user cancelled.</returns>
    string? PickSaveFile(string fileName, string filter);

    /// <returns>The folder picked; null when the user cancelled.</returns>
    string? PickFolder(string? start);

    /// <returns>False when another program holds the clipboard.</returns>
    bool CopyText(string text);
}

/// <summary>Prompts nobody sees, until a window takes over: questions are declined and nothing is picked or copied.</summary>
public sealed class NoPrompts : IUserPrompts
{
    public static NoPrompts Instance { get; } = new();

    public void Show(string title, string text, MessageKind kind)
    {
    }

    public Task<bool> ConfirmAsync(string title, string text, string yes, string no) => Task.FromResult(false);
    public Task<DialogAnswer> AskAsync(string title, string text, string primary, string secondary, string cancel) => Task.FromResult(DialogAnswer.Cancel);
    public string? PickOpenFile(string filter) => null;
    public string? PickSaveFile(string fileName, string filter) => null;
    public string? PickFolder(string? start) => null;
    public bool CopyText(string text) => false;
}
