using System;
using System.Windows;
using System.Windows.Input;
using FluentTextBox = Wpf.Ui.Controls.TextBox;

namespace SLiveTranslator.UI.Controls;

/// <summary>
/// A box that records a hotkey: while it has the keyboard focus, the keys pressed become its <see cref="Gesture"/>
/// (see <see cref="HotkeyRecorder"/>) and <see cref="IsRecording"/> is true, so the page can take the app's hotkeys
/// off meanwhile and pressing the current one records it instead of firing. Leaving the box, the window going to the
/// background or the page closing ends the recording.
/// </summary>
public sealed class HotkeyBox : FluentTextBox
{
    public static readonly DependencyProperty GestureProperty = DependencyProperty.Register(nameof(Gesture), typeof(string), typeof(HotkeyBox),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (box, _) => ((HotkeyBox)box).ShowGesture()));

    public static readonly DependencyProperty IsRecordingProperty = DependencyProperty.Register(nameof(IsRecording), typeof(bool), typeof(HotkeyBox),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    readonly HotkeyRecorder _recorder = new();
    Window? _window;

    public HotkeyBox()
    {
        // An implicit style applies to its own type only: the box takes the look of WPF-UI's text box.
        SetResourceReference(StyleProperty, typeof(FluentTextBox));
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        IsUndoEnabled = false;
        ClearButtonEnabled = false;
        InputMethod.SetIsInputMethodEnabled(this, false);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>The gesture in its canonical form ("Ctrl+Shift+T"); "" for none.</summary>
    public string Gesture
    {
        get => (string)GetValue(GestureProperty);
        set => SetValue(GestureProperty, value);
    }

    public bool IsRecording
    {
        get => (bool)GetValue(IsRecordingProperty);
        set => SetValue(IsRecordingProperty, value);
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        StartRecording();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        StopRecording();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (!IsRecording)
        {
            base.OnPreviewKeyDown(e);
            return;
        }
        var step = _recorder.KeyDown(e.Key, Keyboard.Modifiers, e.SystemKey);
        if (step == RecordStep.PassThrough)
        {
            base.OnPreviewKeyDown(e);
            return;
        }
        e.Handled = true; // Alt must not reach the window's menu handling, nor Ctrl+A the text
        if (step != RecordStep.Partial) Gesture = _recorder.Gesture;
        Text = _recorder.Text;
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        if (!IsRecording)
        {
            base.OnPreviewKeyUp(e);
            return;
        }
        _recorder.KeyUp(Keyboard.Modifiers);
        Text = _recorder.Text;
        e.Handled = true;
    }

    void StartRecording()
    {
        if (IsRecording) return;
        _recorder.Begin(Gesture ?? "");
        Text = _recorder.Text;
        IsRecording = true;
    }

    void StopRecording()
    {
        if (!IsRecording) return;
        IsRecording = false;
        ShowGesture();
    }

    void ShowGesture() => Text = Gesture ?? "";

    void OnLoaded(object sender, RoutedEventArgs e)
    {
        ShowGesture();
        _window = Window.GetWindow(this);
        if (_window is null) return;
        _window.Deactivated += OnWindowDeactivated;
        _window.Activated += OnWindowActivated;
    }

    void OnUnloaded(object sender, RoutedEventArgs e)
    {
        StopRecording();
        if (_window is null) return;
        _window.Deactivated -= OnWindowDeactivated;
        _window.Activated -= OnWindowActivated;
        _window = null;
    }

    /// <summary>Back in the game, the hotkeys work again even if the box kept the focus within its window.</summary>
    void OnWindowDeactivated(object? sender, EventArgs e) => StopRecording();

    void OnWindowActivated(object? sender, EventArgs e)
    {
        if (IsKeyboardFocused) StartRecording();
    }
}
