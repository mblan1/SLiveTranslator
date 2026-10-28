using System.Threading.Tasks;

namespace SLiveTranslator.UI;

public enum MessageKind { Info, Success, Warning, Error }

/// <summary>What a page asks of the window it is in: a short message, a confirmation, a file to open or save.</summary>
public interface IUserPrompts
{
    void Show(string title, string text, MessageKind kind);
    Task<bool> ConfirmAsync(string title, string text, string yes, string no);

    /// <returns>The file picked; null when the user cancelled.</returns>
    string? PickOpenFile(string filter);

    /// <returns>The file picked; null when the user cancelled.</returns>
    string? PickSaveFile(string fileName, string filter);
}

/// <summary>Prompts nobody sees, until a window takes over: confirmations are declined and no file is picked.</summary>
public sealed class NoPrompts : IUserPrompts
{
    public static NoPrompts Instance { get; } = new();

    public void Show(string title, string text, MessageKind kind)
    {
    }

    public Task<bool> ConfirmAsync(string title, string text, string yes, string no) => Task.FromResult(false);
    public string? PickOpenFile(string filter) => null;
    public string? PickSaveFile(string fileName, string filter) => null;
}
