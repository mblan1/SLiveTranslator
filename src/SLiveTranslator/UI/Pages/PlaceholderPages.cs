using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace SLiveTranslator.UI.Pages;

/// <summary>A page the next step fills in; until then it says so. Each one has a type of its own for its navigation item.</summary>
public abstract class PlaceholderPage : Page
{
    protected PlaceholderPage(string titleKey)
    {
        var panel = new StackPanel { Margin = new Thickness(24, 12, 24, 24) };
        panel.Children.Add(Text(titleKey, 26, FontWeights.SemiBold));
        panel.Children.Add(Text("Page.ComingSoon", 14, FontWeights.Normal));
        Content = panel;
    }

    static TextBlock Text(string key, double size, FontWeight weight)
    {
        var text = new TextBlock { FontSize = size, FontWeight = weight, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        text.SetBinding(TextBlock.TextProperty, new Binding($"[{key}]") { Source = Loc.Instance });
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        return text;
    }
}

public sealed class HotkeysPage() : PlaceholderPage("Nav.Hotkeys");

public sealed class NotificationsPage() : PlaceholderPage("Nav.Notifications");

public sealed class StoragePage() : PlaceholderPage("Nav.Storage");

public sealed class GeneralPage() : PlaceholderPage("Nav.General");
