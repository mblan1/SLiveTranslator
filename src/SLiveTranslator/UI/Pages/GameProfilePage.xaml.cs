using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SLiveTranslator.UI.Pages;

public partial class GameProfilePage : Page
{
    public GameProfilePage() => InitializeComponent();

    /// <summary>Typing into a cell selects its row, so Remove takes the row being edited.</summary>
    void OnGridFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is ItemsControl list && e.NewFocus is DependencyObject focused && ItemsControl.ContainerFromElement(list, focused) is ListViewItem row)
            row.IsSelected = true;
    }
}
