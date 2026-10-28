using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SLiveTranslator.UI.Pages;

public partial class StoragePage : Page
{
    public StoragePage()
    {
        InitializeComponent();
        // The files, the cache and the log change while the page is away.
        Loaded += (_, _) => (DataContext as StorageViewModel)?.PageShown();
    }

    /// <summary>Editing a translation selects its row, so Delete takes the row being edited.</summary>
    void OnListFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is ItemsControl list && e.NewFocus is DependencyObject focused && ItemsControl.ContainerFromElement(list, focused) is ListViewItem row)
            row.IsSelected = true;
    }
}
