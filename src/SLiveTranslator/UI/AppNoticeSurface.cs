using System;
using System.Media;
using System.Windows.Threading;
using SLiveTranslator.Services;
using SLiveTranslator.UI.Overlay;
using DRect = System.Drawing.Rectangle;
using Forms = System.Windows.Forms;

namespace SLiveTranslator.UI;

/// <summary>The <see cref="Notifier"/>'s output in the running app: the overlay, the tray icon and the system sounds, on the UI thread.</summary>
internal sealed class AppNoticeSurface(Dispatcher ui, Func<OverlayHost?> overlay, Func<Forms.NotifyIcon?> tray) : INoticeSurface
{
    const int BalloonMs = 4000;

    public bool HasOverlay => overlay() is not null;

    public void ShowToast(ToastRequest toast) =>
        OnUi(() => overlay()?.ShowStatus(toast.Text, toast.Kind, toast.Anchor, toast.Seconds, toast.Position, toast.Region));

    public void HideToast() => OnUi(() => overlay()?.HideStatus());

    public void ShowBalloon(string title, string text, bool warning) => OnUi(() =>
    {
        if (tray() is not { } icon) return;
        icon.BalloonTipTitle = title;
        icon.BalloonTipText = text;
        icon.BalloonTipIcon = warning ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info;
        icon.ShowBalloonTip(BalloonMs);
    });

    public void Play(NoticeSound sound) => (sound switch
    {
        NoticeSound.Hand => SystemSounds.Hand,
        NoticeSound.Exclamation => SystemSounds.Exclamation,
        _ => SystemSounds.Asterisk,
    }).Play();

    public void ShowBadge(DRect region, string text) => OnUi(() => overlay()?.ShowLiveBadge(region, text));

    public void HideBadge() => OnUi(() => overlay()?.HideLiveBadge());

    /// <summary>At once on the UI thread, so a toast and the badge keep the order of the calls; queued from any other.</summary>
    void OnUi(Action action)
    {
        if (ui.CheckAccess()) action();
        else ui.BeginInvoke(action);
    }
}
