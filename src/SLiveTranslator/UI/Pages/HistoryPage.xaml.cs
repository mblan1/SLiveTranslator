using System.Windows.Controls;

namespace SLiveTranslator.UI.Pages;

public partial class HistoryPage : Page
{
    public HistoryPage()
    {
        InitializeComponent();
        // Lines keep coming while the page is away.
        Loaded += (_, _) => (DataContext as HistoryViewModel)?.Refresh();
    }
}
